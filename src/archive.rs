//! 解压发行包：Windows 用 zip，Linux / macOS 用 tar.gz。

use std::fs::File;
use std::path::Path;

pub fn extract(archive: &Path, target: &Path) -> Result<(), String> {
    std::fs::create_dir_all(target).map_err(|e| format!("无法创建目录 {}: {e}", target.display()))?;
    let file = File::open(archive).map_err(|e| format!("无法打开安装包: {e}"))?;

    let name = archive.to_string_lossy().to_lowercase();
    if name.ends_with(".zip") {
        // extract 会拒绝 “../” 之类越出目标目录的条目
        zip::ZipArchive::new(file).and_then(|mut zip| zip.extract(target)).map_err(|e| format!("解压失败: {e}"))
    } else {
        tar::Archive::new(flate2::read::GzDecoder::new(file)).unpack(target).map_err(|e| format!("解压失败: {e}"))
    }
}

/// zip 不一定保留权限位；在类 Unix 系统上补一次 chmod +x，避免解压后跑不起来。
pub fn ensure_executable(path: &Path) {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        if let Ok(metadata) = std::fs::metadata(path) {
            let mut permissions = metadata.permissions();
            permissions.set_mode(permissions.mode() | 0o111);
            let _ = std::fs::set_permissions(path, permissions);
        }
    }
    #[cfg(not(unix))]
    let _ = path;
}
