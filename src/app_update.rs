//! 从本仓库的 GitHub Releases 自升级。
//!
//! 按安装方式分三种：
//! - 便携包：正在运行的可执行文件没法覆盖自己，所以实际替换交给一个外部脚本，
//!   它等本进程退出后把暂存目录覆盖到程序目录，再重新拉起程序；
//! - Windows 安装包：下载新版 setup.exe 并启动它，由安装程序完成覆盖；
//! - macOS .app、Linux deb / rpm / AppImage：文件布局归系统管，不自己动，只给出下载页。

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tokio_util::sync::CancellationToken;

use crate::github::{DownloadSpec, GitHub};
use crate::model::Progress;
use crate::{archive, paths, version};

const REPOSITORY: &str = "VoidDel/RN_FlashTool";

pub const RELEASES_PAGE: &str = "https://github.com/VoidDel/RN_FlashTool/releases";

/// 可执行文件名随平台而变，与 Cargo.toml 里的 `[[bin]] name` 一致。
const EXECUTABLE_NAME: &str = if cfg!(windows) { "RnFlashTool.exe" } else { "RnFlashTool" };

/// 本程序的一个可下载版本。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppRelease {
    pub version: String,
    pub tag_name: String,
    pub asset_name: String,
    pub download_url: String,
    pub size_bytes: u64,
    pub published_at: String,
    pub notes: String,
    pub digest: Option<String>,
    pub checksum_url: Option<String>,
}

/// 当前版本：发布流水线通过 `RN_FLASHTOOL_VERSION` 注入标签上的版本号，本地构建取 Cargo.toml。
pub fn current_version() -> &'static str {
    option_env!("RN_FLASHTOOL_VERSION").unwrap_or(env!("CARGO_PKG_VERSION"))
}

/// 程序是怎么装到这台机器上的，决定了怎么升级。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum InstallKind {
    /// 解压即用的 zip / tar.gz。
    Portable,
    /// Windows 的 NSIS 安装包，安装目录里会有 uninstall.exe。
    WindowsInstaller,
    /// macOS 的 .app（来自 .dmg）。
    MacApp,
    /// Linux 的 AppImage，运行时有 APPIMAGE 环境变量。
    AppImage,
    /// Linux 的 deb / rpm，装在 /usr 下。
    SystemPackage,
}

pub fn install_kind() -> InstallKind {
    let directory = paths::display(&paths::APP_DIR).replace('\\', "/");

    if cfg!(target_os = "linux") && std::env::var_os("APPIMAGE").is_some() {
        InstallKind::AppImage
    } else if cfg!(target_os = "linux") && directory.starts_with("/usr/") {
        InstallKind::SystemPackage
    } else if cfg!(target_os = "macos") && directory.contains(".app/Contents/MacOS") {
        InstallKind::MacApp
    } else if cfg!(windows) && paths::APP_DIR.join("uninstall.exe").is_file() {
        InstallKind::WindowsInstaller
    } else {
        InstallKind::Portable
    }
}

/// 当前环境不能自动升级的原因；能升级时返回 `None`，界面据此决定给“下载并安装”还是“前往下载页”。
pub fn block_reason() -> Option<String> {
    let directory = paths::APP_DIR.as_path();

    // 从 cargo 构建输出目录运行时，覆盖它没有意义。
    let text = paths::display(directory).replace('\\', "/").to_lowercase();
    if text.contains("/target/debug") || text.contains("/target/release") {
        return Some("当前是从构建输出目录运行的开发版本，不支持自动升级。".into());
    }

    match install_kind() {
        // 安装程序自己负责写文件。
        InstallKind::WindowsInstaller => return None,
        InstallKind::MacApp => return Some("请下载新版 .dmg，把程序拖进“应用程序”覆盖旧版即可，设置会保留。".into()),
        InstallKind::AppImage => return Some("请下载新版 AppImage 替换当前文件，设置会保留。".into()),
        InstallKind::SystemPackage => {
            return Some("当前版本由系统包管理器安装，请下载新版 deb / rpm 安装包升级，设置会保留。".into());
        }
        InstallKind::Portable => {}
    }

    let probe = directory.join(format!(".update-probe-{}", std::process::id()));
    match std::fs::write(&probe, b"") {
        Ok(()) => {
            let _ = std::fs::remove_file(&probe);
            None
        }
        Err(_) => Some(format!("程序目录不可写，无法就地升级：{}", directory.display())),
    }
}

/// 发行包命名规则见 .github/workflows/release.yml：
/// 便携包 `RnFlashTool-<版本>-<RID>.zip|tar.gz`，Windows 安装包 `RnFlashTool-<版本>-<RID>-setup.exe`。
fn asset_suffix(kind: InstallKind) -> String {
    let arch = match std::env::consts::ARCH {
        "aarch64" => "arm64",
        "x86" => "x86",
        _ => "x64",
    };
    if cfg!(windows) {
        if kind == InstallKind::WindowsInstaller { format!("win-{arch}-setup.exe") } else { format!("win-{arch}.zip") }
    } else if cfg!(target_os = "macos") {
        format!("osx-{arch}.tar.gz")
    } else {
        format!("linux-{arch}.tar.gz")
    }
}

/// 查询最新版本；没有比当前更新的版本时返回 `None`。预发布版本不参与。
pub async fn check(github: &GitHub) -> Result<Option<AppRelease>, String> {
    let suffix = asset_suffix(install_kind());
    let assets = github.releases(REPOSITORY, 10, |name| name.ends_with(&suffix)).await?;

    Ok(assets
        .into_iter()
        .filter(|asset| !asset.prerelease)
        .filter_map(|asset| {
            let version = version::parse(&asset.tag)?;
            version::is_newer(Some(&version), Some(current_version())).then_some(AppRelease {
                version,
                tag_name: asset.tag,
                asset_name: asset.name,
                download_url: asset.url,
                size_bytes: asset.size,
                published_at: asset.published_at,
                notes: asset.notes,
                digest: asset.digest,
                checksum_url: asset.checksum_url,
            })
        })
        .max_by(|a, b| version::compare(Some(&a.version), Some(&b.version))))
}

/// 下载并校验。便携包解压到暂存目录并返回它；Windows 安装包直接返回下载好的 setup.exe。
/// 此时尚未改动程序目录，随时可以放弃。
pub async fn prepare(
    github: &GitHub,
    release: &AppRelease,
    mirror: &str,
    progress: &(dyn Fn(Progress) + Send + Sync),
    cancel: &CancellationToken,
) -> Result<PathBuf, String> {
    let temp = paths::ensure_dir(&paths::temp_dir())?;
    let file_name = Path::new(&release.asset_name).file_name().ok_or("安装包文件名不合法")?;
    let archive_path = temp.join(file_name);
    let staging = temp.join(format!("update-{}", release.version.replace(['/', '\\'], "_")));

    progress(Progress::new(format!("正在下载 {} …", release.asset_name), Some(0.0)));
    let spec = DownloadSpec {
        name: &release.asset_name,
        url: &release.download_url,
        size: release.size_bytes,
        digest: release.digest.as_deref(),
        checksum_url: release.checksum_url.as_deref(),
        mirror,
    };

    if install_kind() == InstallKind::WindowsInstaller {
        let result = github.download_verified(&spec, &archive_path, true, progress, cancel).await;
        if result.is_err() {
            let _ = std::fs::remove_file(&archive_path);
        }
        result?;
        progress(Progress::new(format!("{} 安装程序已就绪", release.version), Some(100.0)));
        return Ok(archive_path);
    }

    let result = async {
        // 自升级会直接运行下载来的程序，拿不到校验和就不装。
        github.download_verified(&spec, &archive_path, true, progress, cancel).await?;

        progress(Progress::new("正在解压 …", None));
        if staging.exists() {
            std::fs::remove_dir_all(&staging).map_err(|e| format!("无法清理暂存目录: {e}"))?;
        }
        let (archive_path, staging_dir) = (archive_path.clone(), staging.clone());
        tokio::task::spawn_blocking(move || archive::extract(&archive_path, &staging_dir))
            .await
            .map_err(|e| format!("解压失败: {e}"))??;

        // 包里必须有可执行文件，否则覆盖过去会把程序弄坏。
        let executable = staging.join(EXECUTABLE_NAME);
        if !executable.is_file() {
            return Err(format!("安装包里没有找到 {EXECUTABLE_NAME}，已放弃升级。"));
        }
        archive::ensure_executable(&executable);

        progress(Progress::new(format!("{} 已就绪", release.version), Some(100.0)));
        Ok(staging.clone())
    }
    .await;

    let _ = std::fs::remove_file(&archive_path);
    if result.is_err() {
        let _ = std::fs::remove_dir_all(&staging);
    }
    result
}

/// 启动升级：Windows 安装包直接运行新版 setup.exe；便携包启动外部脚本，
/// 等本进程退出后用暂存目录覆盖程序目录，再重新拉起程序。调用方随后必须立即退出。
pub fn launch_updater(prepared: &Path) -> Result<(), String> {
    if install_kind() == InstallKind::WindowsInstaller {
        // 安装程序发现本程序还在运行会提示关闭，所以这边启动它之后马上退出。
        return std::process::Command::new(prepared).spawn().map(drop).map_err(|e| format!("无法启动安装程序: {e}"));
    }

    let staging = prepared;
    let app_dir = paths::APP_DIR.as_path();
    let executable = std::env::current_exe().unwrap_or_else(|_| app_dir.join(EXECUTABLE_NAME));
    let temp = paths::ensure_dir(&paths::temp_dir())?;
    let log = temp.join("update.log");

    let (script_path, script) = if cfg!(windows) {
        (temp.join("update.ps1"), windows_script(std::process::id(), staging, app_dir, &executable, &log))
    } else {
        (temp.join("update.sh"), unix_script(std::process::id(), staging, app_dir, &executable, &log))
    };

    // PowerShell 5 读无 BOM 的脚本会按系统代码页解码，路径里有中文就会乱，所以加 BOM。
    let bytes =
        if cfg!(windows) { [b"\xEF\xBB\xBF".as_slice(), script.as_bytes()].concat() } else { script.into_bytes() };
    std::fs::write(&script_path, bytes).map_err(|e| format!("无法写入升级脚本: {e}"))?;
    archive::ensure_executable(&script_path);

    let mut command = if cfg!(windows) {
        let mut command = std::process::Command::new("powershell.exe");
        command.args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File"]).arg(&script_path);
        command
    } else {
        let mut command = std::process::Command::new("/bin/sh");
        command.arg(&script_path);
        command
    };

    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }

    command.spawn().map(drop).map_err(|e| format!("无法启动升级脚本: {e}"))
}

/// PowerShell 单引号字符串里只需把 ' 转义成 ''；sh 单引号里则要先结束引号再拼一个转义的 '。
fn ps_quote(path: &Path) -> String {
    format!("'{}'", paths::display(path).replace('\'', "''"))
}

fn sh_quote(path: &Path) -> String {
    format!("'{}'", paths::display(path).replace('\'', r"'\''"))
}

/// 覆盖失败（文件被占用、杀毒软件拦截）时重试几次；无论成败都把程序重新拉起来，
/// 失败原因写进 update.log，不至于升级失败后程序直接“消失”。
fn windows_script(pid: u32, staging: &Path, app_dir: &Path, executable: &Path, log: &Path) -> String {
    let (staging, app_dir, executable, log) =
        (ps_quote(staging), ps_quote(app_dir), ps_quote(executable), ps_quote(log));
    format!(
        r#"$ErrorActionPreference = 'Stop'
$targetPid = {pid}
$staging   = {staging}
$appDir    = {app_dir}
$exe       = {executable}
$log       = {log}

for ($i = 0; $i -lt 200; $i++) {{
    if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 300
}}

try {{
    for ($try = 1; $try -le 10; $try++) {{
        try {{
            Copy-Item -Path (Join-Path $staging '*') -Destination $appDir -Recurse -Force
            break
        }} catch {{
            if ($try -eq 10) {{ throw }}
            Start-Sleep -Milliseconds 500
        }}
    }}
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
    "$(Get-Date -Format s) 升级完成" | Out-File -FilePath $log -Encoding utf8
}} catch {{
    "$(Get-Date -Format s) 升级失败: $_" | Out-File -FilePath $log -Encoding utf8
}}

Start-Process -FilePath $exe -WorkingDirectory $appDir
"#
    )
}

fn unix_script(pid: u32, staging: &Path, app_dir: &Path, executable: &Path, log: &Path) -> String {
    let (staging, app_dir, executable, log) =
        (sh_quote(staging), sh_quote(app_dir), sh_quote(executable), sh_quote(log));
    format!(
        r#"#!/bin/sh
target_pid={pid}
staging={staging}
app_dir={app_dir}
exe={executable}
log={log}

i=0
while [ $i -lt 200 ] && kill -0 "$target_pid" 2>/dev/null; do
    sleep 0.3
    i=$((i + 1))
done

if cp -Rf "$staging/." "$app_dir/"; then
    rm -rf "$staging"
    echo "$(date) 升级完成" > "$log"
else
    echo "$(date) 升级失败: 复制文件出错" > "$log"
fi

chmod +x "$exe" 2>/dev/null
"$exe" >/dev/null 2>&1 &
"#
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quotes_paths_for_scripts() {
        assert_eq!(ps_quote(Path::new("C:/it's")), "'C:/it''s'");
        assert_eq!(sh_quote(Path::new("/tmp/it's")), r"'/tmp/it'\''s'");
    }

    #[test]
    fn windows_script_keeps_powershell_braces() {
        let script = windows_script(42, Path::new("s"), Path::new("a"), Path::new("e"), Path::new("l"));
        assert!(script.contains("$targetPid = 42"));
        assert!(script.contains("if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }"));
    }

    #[test]
    fn development_build_cannot_self_update() {
        // 测试本身就从 target/ 下运行
        assert!(block_reason().is_some_and(|reason| reason.contains("开发版本")));
    }
}
