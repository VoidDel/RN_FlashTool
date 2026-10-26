//! 用户选择持久化到 `settings.json`。
//!
//! 字段名沿用 C# 版的 PascalCase，旧版写下的文件可以原样读进来；
//! 新增字段都带默认值，缺了也不影响解析。

use std::sync::Mutex;

use chrono::{DateTime, FixedOffset};
use serde::{Deserialize, Deserializer, Serialize};
use serde_json::Value;

use crate::model::{DEFAULT_ADDRESS, DEFAULT_CHIP_ID, DEFAULT_READ_SIZE, DEFAULT_TIMEOUT_SECONDS};
use crate::paths;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct Settings {
    pub firmware_path: String,
    /// 扩展名认不出来时下拉框里选的格式：Elf / Bin / Ihex / S19。
    pub format: String,
    /// 读取 / 验证弹窗里上次填的起始地址。
    pub address: String,
    pub read_size: String,
    pub full_erase: bool,
    pub interface_id: String,
    pub target_chip: String,
    pub timeout_seconds: u32,
    /// 当前选中的 OpenOCD 可执行文件路径；为空表示自动选择。
    pub selected_open_ocd_path: String,
    pub auto_check_open_ocd_update: bool,
    /// 下载加速前缀，例如 “https://ghfast.top/”，留空表示直连 GitHub。
    pub download_mirror: String,
    #[serde(skip_serializing_if = "Option::is_none", deserialize_with = "lenient_time")]
    pub last_update_check: Option<DateTime<FixedOffset>>,
    pub auto_check_app_update: bool,
    #[serde(skip_serializing_if = "Option::is_none", deserialize_with = "lenient_time")]
    pub last_app_update_check: Option<DateTime<FixedOffset>>,
    /// software：SYSRESETREQ，三线即可；hardware：拉 NRST，需要接线。
    pub reset_mode: String,
    pub verify_after_flash: bool,
    pub run_after_flash: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            firmware_path: String::new(),
            format: "Elf".into(),
            address: DEFAULT_ADDRESS.into(),
            read_size: DEFAULT_READ_SIZE.into(),
            full_erase: true,
            interface_id: "cmsis-dap".into(),
            target_chip: DEFAULT_CHIP_ID.into(),
            timeout_seconds: DEFAULT_TIMEOUT_SECONDS,
            selected_open_ocd_path: String::new(),
            auto_check_open_ocd_update: true,
            download_mirror: String::new(),
            last_update_check: None,
            auto_check_app_update: true,
            last_app_update_check: None,
            reset_mode: "software".into(),
            verify_after_flash: true,
            run_after_flash: true,
        }
    }
}

/// .NET 写出的时间带 7 位小数，个别情况下还可能是手改坏的；解析不了就当没检查过。
fn lenient_time<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Option<DateTime<FixedOffset>>, D::Error> {
    let text = Option::<String>::deserialize(deserializer)?;
    Ok(text.and_then(|t| DateTime::parse_from_rfc3339(&t).ok()))
}

/// 线程安全的设置存取，每次修改后立即落盘。
pub struct SettingsStore {
    current: Mutex<Settings>,
}

impl SettingsStore {
    pub fn load() -> Self {
        let mut settings = std::fs::read_to_string(paths::settings_file())
            .ok()
            .and_then(|json| serde_json::from_str::<Settings>(&json).ok())
            .unwrap_or_default();

        // 改名前保存的设置里存着指向旧数据目录的绝对路径，这里一并改写。
        settings.selected_open_ocd_path = paths::remap_legacy_path(&settings.selected_open_ocd_path);

        Self { current: Mutex::new(settings) }
    }

    pub fn get(&self) -> Settings {
        self.lock().clone()
    }

    pub fn update(&self, change: impl FnOnce(&mut Settings)) -> Settings {
        let mut guard = self.lock();
        change(&mut guard);
        save(&guard);
        guard.clone()
    }

    /// 前端只传改动过的字段，按字段名合并进当前设置。
    pub fn patch(&self, patch: Value) -> Result<Settings, String> {
        let Value::Object(fields) = patch else {
            return Err("设置补丁必须是对象".into());
        };

        let mut guard = self.lock();
        let mut merged = serde_json::to_value(&*guard).map_err(|e| e.to_string())?;
        if let Value::Object(target) = &mut merged {
            target.extend(fields);
        }

        *guard = serde_json::from_value(merged).map_err(|e| format!("设置格式不正确: {e}"))?;
        save(&guard);
        Ok(guard.clone())
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Settings> {
        // 写设置的代码里没有会 panic 的地方，真中毒了也照常用里面的值。
        self.current.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

fn save(settings: &Settings) {
    // 配置保存失败不应该影响使用，忽略即可。
    if paths::ensure_dir(&paths::CONFIG_DIR).is_ok()
        && let Ok(json) = serde_json::to_string_pretty(settings)
    {
        let _ = std::fs::write(paths::settings_file(), json);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_settings_written_by_csharp_version() {
        let json = r#"{
            "FirmwarePath": "D:\\fw\\app.hex",
            "Format": "Ihex",
            "Address": "0x08000000",
            "ReadSize": "0x10000",
            "FullErase": false,
            "InterfaceId": "stlink",
            "TargetChip": "stm32f407",
            "TimeoutSeconds": 90,
            "SelectedOpenOcdPath": "C:\\openocd\\bin\\openocd.exe",
            "AutoCheckOpenOcdUpdate": true,
            "DownloadMirror": "",
            "LastUpdateCheck": "2026-10-20T09:30:12.1234567+08:00",
            "AutoCheckAppUpdate": false
        }"#;

        let settings: Settings = serde_json::from_str(json).unwrap();
        assert_eq!(settings.interface_id, "stlink");
        assert_eq!(settings.timeout_seconds, 90);
        assert!(!settings.full_erase);
        assert!(settings.last_update_check.is_some());
        assert!(settings.last_app_update_check.is_none());
        // 旧文件里没有的新字段取默认值
        assert_eq!(settings.reset_mode, "software");
        assert!(settings.run_after_flash);
    }

    #[test]
    fn broken_timestamp_is_ignored() {
        let settings: Settings = serde_json::from_str(r#"{ "LastUpdateCheck": "yesterday" }"#).unwrap();
        assert!(settings.last_update_check.is_none());
    }
}
