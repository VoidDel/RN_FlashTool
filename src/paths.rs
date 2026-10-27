//! 程序使用的各个目录。配置与数据统一放在用户目录下，避免往安装目录写文件。
//!
//! 目录位置刻意与 C# 版（.NET 的 `Environment.SpecialFolder`）保持一致，
//! 这样从旧版升级上来后，设置、自定义芯片和已下载的 OpenOCD 都还在原处。

use std::path::{Path, PathBuf};
use std::sync::LazyLock;

const FOLDER_NAME: &str = "RN_FlashTool";

/// 项目改名前（STM32 时期）使用的目录名。
const LEGACY_FOLDER_NAME: &str = "Stm32FlashTool";

/// 可执行文件所在目录。
pub static APP_DIR: LazyLock<PathBuf> = LazyLock::new(|| {
    std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(Path::to_path_buf))
        .unwrap_or_else(|| PathBuf::from("."))
});

/// 漫游配置目录，存放 settings.json 与用户自定义芯片。
pub static CONFIG_DIR: LazyLock<PathBuf> = LazyLock::new(|| roaming_base().join(FOLDER_NAME));

/// 本地数据目录，存放下载的 OpenOCD 与临时文件。
pub static DATA_DIR: LazyLock<PathBuf> = LazyLock::new(|| local_base().join(FOLDER_NAME));

pub fn settings_file() -> PathBuf {
    CONFIG_DIR.join("settings.json")
}

pub fn user_chips_dir() -> PathBuf {
    CONFIG_DIR.join("chips")
}

/// 下载安装的 OpenOCD 各版本的根目录。
pub fn openocd_install_root() -> PathBuf {
    DATA_DIR.join("openocd")
}

/// 生成的临时 OpenOCD 配置、下载中的安装包等。
pub fn temp_dir() -> PathBuf {
    DATA_DIR.join("temp")
}

pub fn ensure_dir(path: &Path) -> Result<PathBuf, String> {
    std::fs::create_dir_all(path).map_err(|e| format!("无法创建目录 {}: {e}", path.display()))?;
    Ok(path.to_path_buf())
}

/// 安装包里的产品名，决定 Linux 下资源目录的名字，与 tauri.conf.json 的 productName 一致。
const PRODUCT_NAME: &str = "RN FlashTool";

/// 可能放着 `chips/`、`openocd/` 的目录，按优先级排列：
/// - 程序目录：便携包与 Windows 安装包都把资源放在可执行文件旁边
/// - macOS 的 .app：资源在 `Contents/Resources`，可执行文件在 `Contents/MacOS`
/// - Linux 的 deb / rpm / AppImage：可执行文件在 `usr/bin`，资源在 `usr/lib/<产品名>`
/// - 程序目录的若干级父目录：开发时可执行文件在 `target/debug/` 下，往上才能摸到仓库根部
pub fn resource_roots() -> Vec<PathBuf> {
    let mut roots = vec![APP_DIR.clone()];
    if let Some(parent) = APP_DIR.parent() {
        roots.push(parent.join("Resources"));
        roots.push(parent.join("lib").join(PRODUCT_NAME));
    }
    roots.extend(APP_DIR.ancestors().skip(1).take(5).map(Path::to_path_buf));
    roots
}

/// 内置芯片定义目录，找不到时退回程序目录下的 `chips/`（只用于提示）。
pub fn builtin_chips_dir() -> PathBuf {
    resource_roots()
        .into_iter()
        .map(|root| root.join("chips"))
        .find(|dir| dir.is_dir())
        .unwrap_or_else(|| APP_DIR.join("chips"))
}

/// 与 .NET 的 `SpecialFolder.ApplicationData` 对应。
fn roaming_base() -> PathBuf {
    if cfg!(windows) {
        env_dir("APPDATA").unwrap_or_else(|| home().join("AppData").join("Roaming"))
    } else {
        env_dir("XDG_CONFIG_HOME").unwrap_or_else(|| home().join(".config"))
    }
}

/// 与 .NET 的 `SpecialFolder.LocalApplicationData` 对应（.NET 8 起 macOS 上是 Application Support）。
fn local_base() -> PathBuf {
    if cfg!(windows) {
        env_dir("LOCALAPPDATA").unwrap_or_else(|| home().join("AppData").join("Local"))
    } else if cfg!(target_os = "macos") {
        home().join("Library").join("Application Support")
    } else {
        env_dir("XDG_DATA_HOME").unwrap_or_else(|| home().join(".local").join("share"))
    }
}

fn env_dir(name: &str) -> Option<PathBuf> {
    std::env::var_os(name).filter(|value| !value.is_empty()).map(PathBuf::from)
}

fn home() -> PathBuf {
    env_dir(if cfg!(windows) { "USERPROFILE" } else { "HOME" }).unwrap_or_else(|| PathBuf::from("."))
}

/// 把指向改名前数据目录的绝对路径改写到新目录。
/// 设置里存的是 openocd 的完整路径，光搬目录不改路径的话，会找不到之前选中的版本。
pub fn remap_legacy_path(path: &str) -> String {
    let legacy = local_base().join(LEGACY_FOLDER_NAME);
    let legacy = legacy.to_string_lossy();
    match path.get(..legacy.len()) {
        Some(prefix) if !path.is_empty() && prefix.eq_ignore_ascii_case(&legacy) => {
            format!("{}{}", DATA_DIR.display(), &path[legacy.len()..])
        }
        _ => path.to_string(),
    }
}

/// 把改名前的配置与数据目录搬到新位置。只在新目录尚不存在时执行，重复调用是安全的。
pub fn migrate_legacy_dirs() {
    for (legacy, target) in [
        (roaming_base().join(LEGACY_FOLDER_NAME), CONFIG_DIR.as_path()),
        (local_base().join(LEGACY_FOLDER_NAME), DATA_DIR.as_path()),
    ] {
        if legacy.is_dir() && !target.exists() {
            // 迁移失败就当作全新安装，不影响程序启动。
            let _ = std::fs::rename(&legacy, target);
        }
    }
}

/// 把路径转成字符串交给前端或写进配置。
pub fn display(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}
