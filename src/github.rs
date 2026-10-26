//! 访问 GitHub Releases 的共用客户端：OpenOCD 版本管理与程序自升级都走这里。
//!
//! 下载可以走用户填的加速前缀（第三方镜像），所以下载完必须校验：
//! 期望的 SHA-256 取自直连 api.github.com 拿到的 `digest` 字段，镜像改不了它；
//! 老的发行版没有 `digest` 时，再从同一个 Release 里的校验和文件取（同样直连 GitHub 下载）。

use std::path::Path;
use std::time::Duration;

use futures_util::StreamExt;
use serde_json::Value;
use sha2::{Digest, Sha256};
use tokio::io::AsyncWriteExt;
use tokio_util::sync::CancellationToken;

use crate::model::Progress;

pub const CANCELLED: &str = "已取消";

/// 某个 Release 里与当前平台匹配的那个安装包。
#[derive(Debug, Clone)]
pub struct ReleaseAsset {
    pub tag: String,
    pub prerelease: bool,
    pub published_at: String,
    pub notes: String,
    pub name: String,
    pub url: String,
    pub size: u64,
    /// GitHub 给出的 “sha256:<hex>”。
    pub digest: Option<String>,
    /// 同一 Release 里的校验和文件（xPack 的 `.sha`、本项目的 `SHA256SUMS.txt` 等）。
    pub checksum_url: Option<String>,
}

pub struct GitHub {
    http: reqwest::Client,
}

impl GitHub {
    pub fn new(version: &str) -> Self {
        let http = reqwest::Client::builder()
            .user_agent(format!("RN_FlashTool/{version}"))
            .connect_timeout(Duration::from_secs(15))
            .build()
            .expect("HTTP 客户端初始化");
        Self { http }
    }

    /// 拉取发行版列表，并为每个 Release 挑出第一个满足 `matches` 的安装包。草稿会被跳过。
    pub async fn releases(
        &self,
        repository: &str,
        count: u32,
        matches: impl Fn(&str) -> bool,
    ) -> Result<Vec<ReleaseAsset>, String> {
        let url = format!("https://api.github.com/repos/{repository}/releases?per_page={count}");
        let response = self
            .http
            .get(&url)
            .header("Accept", "application/vnd.github+json")
            .timeout(Duration::from_secs(30))
            .send()
            .await
            .map_err(|e| format!("无法访问 GitHub: {e}"))?;

        let status = response.status();
        if status == reqwest::StatusCode::FORBIDDEN || status == reqwest::StatusCode::TOO_MANY_REQUESTS {
            return Err("GitHub API 拒绝了请求（多半是触发了匿名访问频率限制），请稍后再试。".into());
        }
        if !status.is_success() {
            return Err(format!("GitHub API 返回 {status}"));
        }

        let json: Value = response.json_value().await?;
        Ok(parse_releases(&json, matches))
    }

    /// 下载并校验。`required` 为真时拿不到期望哈希就放弃（程序自升级），否则只给出提示（OpenOCD）。
    pub async fn download_verified(
        &self,
        asset: &DownloadSpec<'_>,
        destination: &Path,
        required: bool,
        progress: &(dyn Fn(Progress) + Send + Sync),
        cancel: &CancellationToken,
    ) -> Result<(), String> {
        ensure_github_url(asset.url)?;

        let expected = self.expected_sha256(asset, cancel).await?;
        if expected.is_none() {
            if required {
                return Err("该版本没有提供校验和，无法确认安装包完整性，已放弃。".into());
            }
            progress(Progress::new("该版本没有提供校验和，下载后将跳过校验 …", None));
        }

        let actual =
            self.download(&apply_mirror(asset.mirror, asset.url), destination, asset.size, progress, cancel).await?;

        if let Some(expected) = expected
            && !actual.eq_ignore_ascii_case(&expected)
        {
            let _ = std::fs::remove_file(destination);
            return Err(format!(
                "安装包校验失败：SHA-256 与发布页不一致。\n期望 {expected}\n实际 {actual}\n\n\
                     若填写了下载加速前缀，请换一个或清空后直连重试。"
            ));
        }
        Ok(())
    }

    async fn expected_sha256(
        &self,
        asset: &DownloadSpec<'_>,
        cancel: &CancellationToken,
    ) -> Result<Option<String>, String> {
        if let Some(hex) = asset.digest.and_then(|d| d.strip_prefix("sha256:")) {
            return Ok(Some(hex.to_string()));
        }

        let Some(url) = asset.checksum_url else {
            return Ok(None);
        };
        ensure_github_url(url)?;

        // 校验和文件必须直连 GitHub 取，走镜像就失去意义了。
        let request = self.http.get(url).timeout(Duration::from_secs(30)).send();
        let text = tokio::select! {
            response = request => response
                .and_then(reqwest::Response::error_for_status)
                .map_err(|e| format!("无法获取校验和文件: {e}"))?
                .text()
                .await
                .map_err(|e| format!("无法获取校验和文件: {e}"))?,
            _ = cancel.cancelled() => return Err(CANCELLED.into()),
        };

        find_checksum(&text, asset.name).map(Some).ok_or_else(|| format!("校验和文件里没有 {} 的记录", asset.name))
    }

    /// 下载到文件，边下边算 SHA-256，返回十六进制摘要。
    async fn download(
        &self,
        url: &str,
        destination: &Path,
        expected_size: u64,
        progress: &(dyn Fn(Progress) + Send + Sync),
        cancel: &CancellationToken,
    ) -> Result<String, String> {
        let response = tokio::select! {
            response = self.http.get(url).send() => response.map_err(|e| format!("下载失败: {e}"))?,
            _ = cancel.cancelled() => return Err(CANCELLED.into()),
        };
        let response = response.error_for_status().map_err(|e| format!("下载失败: {e}"))?;

        let total = response.content_length().filter(|len| *len > 0).unwrap_or(expected_size);
        let mut file = tokio::fs::File::create(destination)
            .await
            .map_err(|e| format!("无法创建文件 {}: {e}", destination.display()))?;

        let mut stream = response.bytes_stream();
        let mut hasher = Sha256::new();
        let mut downloaded = 0u64;
        let mut last_percent = u64::MAX;

        loop {
            let chunk = tokio::select! {
                chunk = stream.next() => chunk,
                _ = cancel.cancelled() => {
                    drop(file);
                    let _ = std::fs::remove_file(destination);
                    return Err(CANCELLED.into());
                }
            };
            let Some(chunk) = chunk else { break };
            let chunk = chunk.map_err(|e| format!("下载中断: {e}"))?;

            hasher.update(&chunk);
            file.write_all(&chunk).await.map_err(|e| format!("写入失败: {e}"))?;
            downloaded += chunk.len() as u64;

            // 服务器没给长度、发布页也没写大小时没法算百分比，就不报了。
            if let Some(percent) = (downloaded * 100).checked_div(total)
                && percent != last_percent
            {
                last_percent = percent;
                progress(Progress::new(
                    format!("正在下载 … {:.1} / {:.1} MB", mb(downloaded), mb(total)),
                    Some(percent.min(100) as f64),
                ));
            }
        }

        file.flush().await.map_err(|e| format!("写入失败: {e}"))?;
        Ok(hex(&hasher.finalize()))
    }
}

/// 下载所需的信息，避免一长串参数。
pub struct DownloadSpec<'a> {
    pub name: &'a str,
    pub url: &'a str,
    pub size: u64,
    pub digest: Option<&'a str>,
    pub checksum_url: Option<&'a str>,
    pub mirror: &'a str,
}

trait JsonValue {
    async fn json_value(self) -> Result<Value, String>;
}

impl JsonValue for reqwest::Response {
    async fn json_value(self) -> Result<Value, String> {
        let bytes = self.bytes().await.map_err(|e| format!("读取 GitHub 响应失败: {e}"))?;
        serde_json::from_slice(&bytes).map_err(|e| format!("GitHub 响应格式不正确: {e}"))
    }
}

pub fn parse_releases(json: &Value, matches: impl Fn(&str) -> bool) -> Vec<ReleaseAsset> {
    let text = |value: &Value, key: &str| value.get(key).and_then(Value::as_str).unwrap_or_default().to_string();

    json.as_array()
        .into_iter()
        .flatten()
        .filter(|release| !release.get("draft").and_then(Value::as_bool).unwrap_or(false))
        .filter_map(|release| {
            let assets = release.get("assets")?.as_array()?;
            let asset = assets.iter().find(|a| a.get("name").and_then(Value::as_str).is_some_and(&matches))?;
            let name = text(asset, "name");
            let url = text(asset, "browser_download_url");
            if url.is_empty() {
                return None;
            }

            let checksum_url = find_checksum_asset(assets, &name).map(|a| text(a, "browser_download_url"));

            Some(ReleaseAsset {
                tag: text(release, "tag_name"),
                prerelease: release.get("prerelease").and_then(Value::as_bool).unwrap_or(false),
                published_at: text(release, "published_at"),
                notes: text(release, "body"),
                digest: asset.get("digest").and_then(Value::as_str).map(str::to_string),
                size: asset.get("size").and_then(Value::as_u64).unwrap_or(0),
                checksum_url,
                name,
                url,
            })
        })
        .collect()
}

/// 优先找与安装包同名的 `.sha` / `.sha256`，其次是整个 Release 共用的校验和文件。
fn find_checksum_asset<'a>(assets: &'a [Value], asset_name: &str) -> Option<&'a Value> {
    let name_of = |a: &Value| a.get("name").and_then(Value::as_str).unwrap_or_default().to_lowercase();
    let lowered = asset_name.to_lowercase();

    assets.iter().find(|a| [".sha", ".sha256"].iter().any(|ext| name_of(a) == format!("{lowered}{ext}"))).or_else(
        || {
            assets.iter().find(|a| {
                let name = name_of(a);
                name.contains("sha256sums") || name.contains("checksum")
            })
        },
    )
}

/// 从校验和文件里找某个文件的 SHA-256。兼容 `<hash>  <name>` 与 “一行文件名注释、下一行哈希” 两种写法；
/// 整个文件只有一个哈希（单文件的 `.sha`）时直接用它。
pub fn find_checksum(text: &str, asset_name: &str) -> Option<String> {
    let is_hash = |token: &str| token.len() == 64 && token.chars().all(|c| c.is_ascii_hexdigit());
    let hash_in = |line: &str| {
        line.split_whitespace().map(|t| t.trim_start_matches('*')).find(|t| is_hash(t)).map(str::to_string)
    };

    let mut named_previous_line = false;
    for line in text.lines() {
        let mentions = line.contains(asset_name);
        if let Some(hash) = hash_in(line)
            && (mentions || named_previous_line)
        {
            return Some(hash);
        }
        named_previous_line = mentions;
    }

    let hashes: Vec<String> = text.lines().filter_map(hash_in).collect();
    match hashes.as_slice() {
        [only] => Some(only.clone()),
        _ => None,
    }
}

/// 只接受 GitHub 自己的下载地址，前端传回来的 URL 也不例外。
fn ensure_github_url(url: &str) -> Result<(), String> {
    if url.starts_with("https://github.com/") || url.starts_with("https://objects.githubusercontent.com/") {
        Ok(())
    } else {
        Err(format!("拒绝从非 GitHub 地址下载: {url}"))
    }
}

/// 应用下载加速前缀，方便在访问 GitHub 受限的网络里下载。
fn apply_mirror(mirror: &str, url: &str) -> String {
    match mirror.trim() {
        "" => url.to_string(),
        mirror if mirror.ends_with('/') => format!("{mirror}{url}"),
        mirror => format!("{mirror}/{url}"),
    }
}

pub fn mb(bytes: u64) -> f64 {
    bytes as f64 / 1024.0 / 1024.0
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    const HASH: &str = "6bfd3c97135aafef8affc9af1acf34fd0e2b9ca26044506f6abd7f95b7630052";

    #[test]
    fn finds_hash_in_sums_file() {
        let text = format!("{}  other.zip\n{HASH}  app-win-x64.zip\n", "a".repeat(64));
        assert_eq!(find_checksum(&text, "app-win-x64.zip").as_deref(), Some(HASH));
    }

    #[test]
    fn finds_hash_on_line_after_name() {
        let text = format!("# app-linux.tar.gz: 123 bytes\n{HASH}\n# other.zip: 1 bytes\n{}\n", "b".repeat(64));
        assert_eq!(find_checksum(&text, "app-linux.tar.gz").as_deref(), Some(HASH));
    }

    #[test]
    fn single_hash_file() {
        assert_eq!(find_checksum(&format!("{HASH}\n"), "whatever.zip").as_deref(), Some(HASH));
    }

    #[test]
    fn mirror_prefix() {
        assert_eq!(apply_mirror("", "https://github.com/a"), "https://github.com/a");
        assert_eq!(
            apply_mirror("https://ghfast.top", "https://github.com/a"),
            "https://ghfast.top/https://github.com/a"
        );
    }

    #[test]
    fn picks_matching_asset_and_sidecar() {
        let json: Value = serde_json::from_str(
            r#"[{"tag_name":"v0.12.0-7","prerelease":false,"draft":false,"published_at":"2025-01-01T00:00:00Z",
                "assets":[
                  {"name":"x-linux-x64.tar.gz","browser_download_url":"https://github.com/l","size":1},
                  {"name":"x-win32-x64.zip","browser_download_url":"https://github.com/w","size":2,"digest":"sha256:abc"},
                  {"name":"x-win32-x64.zip.sha","browser_download_url":"https://github.com/w.sha","size":3}]},
               {"tag_name":"draft","draft":true,"assets":[]}]"#,
        )
        .unwrap();

        let found = parse_releases(&json, |name| name.ends_with("win32-x64.zip"));
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].url, "https://github.com/w");
        assert_eq!(found[0].digest.as_deref(), Some("sha256:abc"));
        assert_eq!(found[0].checksum_url.as_deref(), Some("https://github.com/w.sha"));
    }
}
