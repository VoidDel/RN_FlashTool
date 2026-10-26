//! OpenOCD 的多版本管理：发现本机已有的、拉取在线版本、下载安装、卸载。
//!
//! 在线来源有两个：
//! - xPack OpenOCD：随程序内置的就是它，通用，适合 STM32 等 Cortex-M；
//! - Espressif openocd-esp32：带 ESP32 的 SPI Flash 驱动与 `program_esp`，烧 ESP32 必须用它。

use std::cmp::Ordering;
use std::path::{Path, PathBuf};
use std::time::Duration;

use serde::{Deserialize, Serialize};
use tokio_util::sync::CancellationToken;

use super::config::supports_program_esp;
use super::{EXECUTABLE_NAME, hidden_command};
use crate::archive;
use crate::github::{DownloadSpec, GitHub, ReleaseAsset};
use crate::model::Progress;
use crate::{paths, version};

/// 一个 OpenOCD 安装的来源，同时决定列表里的排序。
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum OpenOcdSource {
    /// 随程序一起分发的 openocd 目录。没选过版本时默认用它。
    Bundled,
    /// 本工具下载并安装到用户目录的版本。
    Managed,
    /// 系统 PATH 中找到的版本。
    SystemPath,
    /// 用户手动指定的可执行文件。
    Custom,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Installation {
    /// 可执行文件完整路径，同时用作唯一标识。
    pub executable_path: String,
    pub source: OpenOcdSource,
    pub version: Option<String>,
    /// 只有自己下载安装的版本才有，也只有它们允许删除。
    pub install_directory: Option<String>,
    pub scripts_directory: Option<String>,
    /// scripts 里带 Espressif 的 program_esp，可以烧 ESP32。
    pub supports_esp: bool,
}

impl Installation {
    fn new(
        executable: &Path,
        source: OpenOcdSource,
        version: Option<String>,
        install_directory: Option<&Path>,
    ) -> Self {
        let scripts = resolve_scripts_dir(executable);
        Self {
            executable_path: paths::display(executable),
            source,
            supports_esp: supports_program_esp(scripts.as_deref()).unwrap_or(false),
            scripts_directory: scripts.as_deref().map(paths::display),
            install_directory: install_directory.map(paths::display),
            version,
        }
    }
}

/// 在可执行文件附近按常见发行布局查找 scripts 目录。
pub fn resolve_scripts_dir(executable: &Path) -> Option<PathBuf> {
    let root = executable.parent()?.parent()?;
    [
        root.join("openocd").join("scripts"),               // xPack 布局
        root.join("share").join("openocd").join("scripts"), // 标准 autotools 布局，openocd-esp32 也是
        root.join("scripts"),                               // 部分 Windows 绿色包
    ]
    .into_iter()
    .find(|dir| dir.is_dir())
}

/// 扫描已下载、内置、系统 PATH 与用户手动指定的全部 OpenOCD，并读取各自版本号。
pub async fn discover(custom_path: &str) -> Vec<Installation> {
    let mut candidates: Vec<(PathBuf, OpenOcdSource, Option<PathBuf>)> = Vec::new();

    // 1) 自己下载安装的版本
    if let Ok(entries) = std::fs::read_dir(paths::openocd_install_root()) {
        for dir in entries.filter_map(Result::ok).map(|e| e.path()).filter(|p| p.is_dir()) {
            if let Some(executable) = find_executable(&dir) {
                candidates.push((executable, OpenOcdSource::Managed, Some(dir)));
            }
        }
    }

    // 2) 随程序分发的 openocd 目录（兼容开发时 target/debug 的布局）
    for root in paths::app_roots() {
        candidates.push((root.join("openocd").join("bin").join(EXECUTABLE_NAME), OpenOcdSource::Bundled, None));
    }

    // 3) 系统 PATH
    if let Some(path) = std::env::var_os("PATH") {
        for dir in std::env::split_paths(&path) {
            candidates.push((dir.join(EXECUTABLE_NAME), OpenOcdSource::SystemPath, None));
        }
    }

    // 4) 用户手动指定的路径
    if !custom_path.trim().is_empty() {
        candidates.push((PathBuf::from(custom_path), OpenOcdSource::Custom, None));
    }

    let mut seen = std::collections::HashSet::new();
    candidates.retain(|(path, _, _)| path.is_file() && seen.insert(identity(path)));

    // 并发读取版本号，避免逐个启动进程拖慢界面。
    let mut found = futures_util::future::join_all(candidates.into_iter().map(|(path, source, dir)| async move {
        let path = std::path::absolute(&path).unwrap_or(path);
        // 自己下载的版本用安装目录名（即发行版本号，如 0.12.0-7）：
        // openocd --version 报的是上游版本，区分不出 xPack 的打包修订。
        let version = match &dir {
            Some(dir) => dir.file_name().map(|n| n.to_string_lossy().into_owned()),
            None => query_version(&path).await,
        };
        Installation::new(&path, source, version, dir.as_deref())
    }))
    .await;

    found.sort_by(|a, b| {
        a.source.cmp(&b.source).then_with(|| version::compare(b.version.as_deref(), a.version.as_deref()))
    });
    found
}

/// 把用户手动指定的可执行文件包装成一个安装条目。
pub async fn create_custom(path: &str) -> Option<Installation> {
    let path = std::path::absolute(path).ok()?;
    if !path.is_file() {
        return None;
    }
    let version = query_version(&path).await;
    Some(Installation::new(&path, OpenOcdSource::Custom, version, None))
}

/// 读取 `openocd --version`，失败时返回 `None`。
pub async fn query_version(executable: &Path) -> Option<String> {
    let mut command = hidden_command(executable);
    command.arg("--version");
    let output = tokio::time::timeout(Duration::from_secs(10), command.output()).await.ok()?.ok()?;

    // openocd 把版本信息写在 stderr 上，两边都看一下。
    let stdout = String::from_utf8_lossy(&output.stdout);
    let text = if stdout.trim().is_empty() { String::from_utf8_lossy(&output.stderr) } else { stdout };
    version::parse_from_version_output(&text)
}

/// 在目录里按常见布局查找 openocd 可执行文件；压缩包解压后常会多一层目录。
fn find_executable(directory: &Path) -> Option<PathBuf> {
    let direct = directory.join("bin").join(EXECUTABLE_NAME);
    if direct.is_file() {
        return Some(direct);
    }

    fn walk(dir: &Path, depth: u32, best: &mut Option<PathBuf>) {
        let Ok(entries) = std::fs::read_dir(dir) else { return };
        for path in entries.filter_map(Result::ok).map(|e| e.path()) {
            if path.is_dir() && depth < 4 {
                walk(&path, depth + 1, best);
            } else if path.file_name().is_some_and(|n| n == EXECUTABLE_NAME)
                && best.as_ref().is_none_or(|b| path.as_os_str().len() < b.as_os_str().len())
            {
                *best = Some(path);
            }
        }
    }

    let mut best = None;
    walk(directory, 0, &mut best);
    best
}

/// Windows 路径不区分大小写，去重时统一一下。
fn identity(path: &Path) -> String {
    let full = std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf());
    let text = paths::display(&full);
    if cfg!(windows) { text.to_lowercase() } else { text }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Provider {
    Xpack,
    Espressif,
}

impl Provider {
    fn repository(self) -> &'static str {
        match self {
            Self::Xpack => "xpack-dev-tools/openocd-xpack",
            Self::Espressif => "espressif/openocd-esp32",
        }
    }

    /// 当前系统对应的安装包。两家的命名规则各不相同：
    /// xPack 是 `…-<win32|linux|darwin>-<x64|arm64>.<zip|tar.gz>`，
    /// Espressif 是 `openocd-esp32-<win64|linux-amd64|macos-arm64…>-<版本>.<zip|tar.gz>`。
    fn matches_asset(self, name: &str) -> bool {
        let arch = std::env::consts::ARCH;
        let extension = if cfg!(windows) { ".zip" } else { ".tar.gz" };
        if !name.ends_with(extension) {
            return false;
        }

        match self {
            Self::Xpack => {
                let os = if cfg!(windows) {
                    "win32"
                } else if cfg!(target_os = "macos") {
                    "darwin"
                } else {
                    "linux"
                };
                let arch = match arch {
                    "aarch64" => "arm64",
                    "arm" => "arm",
                    "x86" => "ia32",
                    _ => "x64",
                };
                name.ends_with(&format!("-{os}-{arch}{extension}"))
            }
            Self::Espressif => {
                let platform = match (std::env::consts::OS, arch) {
                    ("windows", "aarch64") => "win-arm64",
                    ("windows", "x86") => "win32",
                    ("windows", _) => "win64",
                    ("macos", "aarch64") => "macos-arm64",
                    ("macos", _) => "macos",
                    (_, "aarch64") => "linux-arm64",
                    (_, "arm") => "linux-armhf",
                    _ => "linux-amd64",
                };
                // “macos-” 也是 “macos-arm64-” 的前缀，所以要求紧跟版本号（数字）。
                name.strip_prefix(&format!("openocd-esp32-{platform}-"))
                    .is_some_and(|rest| rest.starts_with(|c: char| c.is_ascii_digit()))
            }
        }
    }
}

/// 远端可供下载的一个 OpenOCD 发行版本。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Release {
    pub provider: Provider,
    /// 规范化后的版本号，同时用作安装目录名，例如 “0.12.0-7”、“0.12.0-esp32-20260831”。
    pub version: String,
    pub tag_name: String,
    pub asset_name: String,
    pub download_url: String,
    pub size_bytes: u64,
    pub published_at: String,
    pub is_prerelease: bool,
    pub digest: Option<String>,
    pub checksum_url: Option<String>,
}

impl Release {
    fn from_asset(provider: Provider, asset: ReleaseAsset) -> Self {
        let version = match provider {
            Provider::Xpack => version::parse(&asset.tag).unwrap_or_else(|| asset.tag.clone()),
            Provider::Espressif => asset.tag.trim_start_matches(['v', 'V']).to_string(),
        };
        Self {
            provider,
            version,
            tag_name: asset.tag,
            asset_name: asset.name,
            download_url: asset.url,
            size_bytes: asset.size,
            published_at: asset.published_at,
            is_prerelease: asset.prerelease,
            digest: asset.digest,
            checksum_url: asset.checksum_url,
        }
    }
}

/// 拉取可下载的版本列表，按版本号（相同时按发布时间）从新到旧排序。
pub async fn fetch_releases(github: &GitHub, provider: Provider) -> Result<Vec<Release>, String> {
    let assets = github.releases(provider.repository(), 30, |name| provider.matches_asset(name)).await?;
    let mut releases: Vec<Release> = assets.into_iter().map(|a| Release::from_asset(provider, a)).collect();

    releases.sort_by(|a, b| match version::compare(Some(&b.version), Some(&a.version)) {
        Ordering::Equal => b.published_at.cmp(&a.published_at),
        other => other,
    });
    Ok(releases)
}

/// 在 xPack 的稳定版里找出比 `current` 更新的版本；没有则返回 `None`。
pub async fn check_for_update(github: &GitHub, current: Option<&str>) -> Result<Option<Release>, String> {
    // 正在用 Espressif 版本的话，拿 xPack 的版本号去比没有意义。
    if current.is_some_and(|v| v.contains("esp32")) {
        return Ok(None);
    }

    let releases = fetch_releases(github, Provider::Xpack).await?;
    let latest = releases.iter().find(|r| !r.is_prerelease).or(releases.first());
    Ok(latest.filter(|r| version::is_upgrade(Some(&r.version), current)).cloned())
}

/// 下载、校验并安装一个版本，返回安装好的条目。
pub async fn install(
    github: &GitHub,
    release: &Release,
    mirror: &str,
    progress: &(dyn Fn(Progress) + Send + Sync),
    cancel: &CancellationToken,
) -> Result<Installation, String> {
    let root = paths::ensure_dir(&paths::openocd_install_root())?;
    let folder: String = release
        .version
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '_' | '+') { c } else { '_' })
        .collect();
    if folder.is_empty() || folder.starts_with('.') {
        return Err(format!("版本号不合法: {}", release.version));
    }

    let target = root.join(&folder);
    let archive_path = paths::ensure_dir(&paths::temp_dir())?.join(safe_file_name(&release.asset_name));

    progress(Progress::new(format!("正在下载 {} …", release.asset_name), Some(0.0)));
    let spec = DownloadSpec {
        name: &release.asset_name,
        url: &release.download_url,
        size: release.size_bytes,
        digest: release.digest.as_deref(),
        checksum_url: release.checksum_url.as_deref(),
        mirror,
    };

    let result = async {
        github.download_verified(&spec, &archive_path, false, progress, cancel).await?;

        progress(Progress::new("正在解压 …", None));
        if target.exists() {
            std::fs::remove_dir_all(&target).map_err(|e| format!("无法清理旧目录: {e}"))?;
        }
        let (archive_path, target_dir) = (archive_path.clone(), target.clone());
        tokio::task::spawn_blocking(move || archive::extract(&archive_path, &target_dir))
            .await
            .map_err(|e| format!("解压失败: {e}"))??;

        let executable = find_executable(&target).ok_or_else(|| format!("压缩包里没有找到 {EXECUTABLE_NAME}"))?;
        archive::ensure_executable(&executable);

        progress(Progress::new(format!("已安装 {}", release.version), Some(100.0)));
        Ok(Installation::new(&executable, OpenOcdSource::Managed, Some(folder.clone()), Some(&target)))
    }
    .await;

    let _ = std::fs::remove_file(&archive_path);
    if result.is_err() {
        // 安装失败时不要留下半个目录，否则下次会被当成可用版本。
        let _ = std::fs::remove_dir_all(&target);
    }
    result
}

/// 删除一个已下载的版本。只允许删安装根目录下的直接子目录，前端传什么都不会越界。
pub fn uninstall(install_directory: &str) -> Result<(), String> {
    let root = std::path::absolute(paths::openocd_install_root()).map_err(|e| e.to_string())?;
    let target = std::path::absolute(install_directory).map_err(|e| e.to_string())?;

    if target.parent() != Some(root.as_path()) || !target.is_dir() {
        return Err("只能删除通过本工具下载安装的版本。".into());
    }
    std::fs::remove_dir_all(&target).map_err(|e| format!("删除失败（可能有 openocd 正在运行）: {e}"))
}

fn safe_file_name(name: &str) -> String {
    Path::new(name).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "download.tmp".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_assets_for_current_platform() {
        if cfg!(all(windows, target_arch = "x86_64")) {
            assert!(Provider::Xpack.matches_asset("xpack-openocd-0.12.0-7-win32-x64.zip"));
            assert!(!Provider::Xpack.matches_asset("xpack-openocd-0.12.0-7-win32-x64.zip.sha"));
            assert!(Provider::Espressif.matches_asset("openocd-esp32-win64-0.12.0-esp32-20260831.zip"));
            assert!(!Provider::Espressif.matches_asset("openocd-esp32-win32-0.12.0-esp32-20260831.zip"));
        }
        if cfg!(all(target_os = "macos", target_arch = "x86_64")) {
            assert!(Provider::Espressif.matches_asset("openocd-esp32-macos-0.12.0-esp32-20260831.tar.gz"));
            assert!(!Provider::Espressif.matches_asset("openocd-esp32-macos-arm64-0.12.0-esp32-20260831.tar.gz"));
        }
    }

    #[test]
    fn espressif_version_keeps_suffix() {
        let asset = ReleaseAsset {
            tag: "v0.12.0-esp32-20260831".into(),
            prerelease: false,
            published_at: String::new(),
            notes: String::new(),
            name: String::new(),
            url: String::new(),
            size: 0,
            digest: None,
            checksum_url: None,
        };
        assert_eq!(Release::from_asset(Provider::Espressif, asset).version, "0.12.0-esp32-20260831");
    }

    #[test]
    fn uninstall_refuses_paths_outside_install_root() {
        assert!(uninstall(&paths::display(&paths::APP_DIR)).is_err());
        assert!(uninstall("../../").is_err());
    }
}
