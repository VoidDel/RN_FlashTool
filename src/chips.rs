//! 从 `chips/` 目录里的 YAML 文件加载受支持的芯片列表。
//! 新增芯片只需往目录里放一个 .yaml 文件，无需改代码。

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::model::DEFAULT_ADDRESS;
use crate::paths;

pub const DEFAULT_TRANSPORT: &str = "swd";

/// 烧录走哪一套命令。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum FlashMethod {
    /// OpenOCD 通用的 flash write_image / verify_image / dump_image，适用于 Cortex-M 等。
    #[default]
    Openocd,
    /// Espressif 的 program_esp 与 flash *_bank 命令，只有 openocd-esp32 才带。
    Esp,
}

/// 一个受支持芯片的定义，对应一个 YAML 文件。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ChipDefinition {
    /// 唯一标识，小写。同 id 时用户目录下的定义覆盖内置定义。
    pub id: String,
    pub name: String,
    /// 所属系列，用于下拉列表分组。
    pub series: String,
    /// OpenOCD target 配置文件，相对于 scripts 目录，例如 “target/stm32f1x.cfg”。
    pub target_config: String,
    /// Cortex-M 一般是 swd，ESP32 这类只能走 jtag。
    pub transport: String,
    pub flash_method: FlashMethod,
    /// 按前缀解析完整型号时使用；留空则退回 id。
    pub match_prefixes: Vec<String>,
    pub flash_address: String,
    /// 设置后“读取固件”会用它作为默认读取大小。
    pub flash_size: Option<String>,
    /// 调试器速度（kHz），覆盖编程器接口的默认速度。
    pub adapter_speed: Option<u32>,
    /// 追加在 target 配置之后的自定义命令。
    pub extra_config_lines: Vec<String>,
    pub description: String,

    /// 以下字段运行期填充，不从 YAML 读。
    #[serde(skip_deserializing)]
    pub display_name: String,
    #[serde(skip_deserializing)]
    pub source_file: String,
    #[serde(skip_deserializing)]
    pub is_user_defined: bool,
}

impl Default for ChipDefinition {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            series: String::new(),
            target_config: String::new(),
            transport: DEFAULT_TRANSPORT.into(),
            flash_method: FlashMethod::Openocd,
            match_prefixes: Vec::new(),
            flash_address: DEFAULT_ADDRESS.into(),
            flash_size: None,
            adapter_speed: None,
            extra_config_lines: Vec::new(),
            description: String::new(),
            display_name: String::new(),
            source_file: String::new(),
            is_user_defined: false,
        }
    }
}

impl ChipDefinition {
    /// 本次使用的传输方式，留空按 swd 处理。
    pub fn transport(&self) -> &str {
        match self.transport.trim() {
            "" => DEFAULT_TRANSPORT,
            transport => transport,
        }
    }

    /// 烧录起始地址，没写时退回 0x08000000。
    pub fn flash_address(&self) -> &str {
        match self.flash_address.trim() {
            "" => DEFAULT_ADDRESS,
            address => address,
        }
    }

    /// 参与前缀匹配的列表，按长度降序，保证更精确的型号优先命中。
    fn effective_prefixes(&self) -> Vec<&str> {
        let mut prefixes: Vec<&str> = if self.match_prefixes.is_empty() {
            vec![self.id.as_str()]
        } else {
            self.match_prefixes.iter().map(String::as_str).collect()
        };
        prefixes.retain(|prefix| !prefix.trim().is_empty());
        prefixes.iter_mut().for_each(|prefix| *prefix = prefix.trim());
        prefixes.sort_by_key(|prefix| std::cmp::Reverse(prefix.len()));
        prefixes
    }

    /// 校验必填字段，返回错误说明。
    fn validate(&self) -> Result<(), String> {
        if self.id.trim().is_empty() {
            return Err("缺少 id 字段".into());
        }
        if self.target_config.trim().is_empty() {
            return Err("缺少 targetConfig 字段".into());
        }
        if crate::model::parse_number(self.flash_address()).is_none() {
            return Err(format!("flashAddress 无法识别: {}", self.flash_address));
        }
        if let Some(size) = self.flash_size.as_deref().filter(|s| !s.trim().is_empty())
            && crate::model::parse_number(size).is_none()
        {
            return Err(format!("flashSize 无法识别: {size}"));
        }
        Ok(())
    }
}

pub struct ChipCatalog {
    pub chips: Vec<ChipDefinition>,
    /// 加载过程中出现的问题（文件解析失败、字段缺失等）。
    pub errors: Vec<String>,
    pub builtin_dir: PathBuf,
    pub user_dir: PathBuf,
}

impl ChipCatalog {
    pub fn load() -> Self {
        Self::load_from(paths::builtin_chips_dir(), paths::user_chips_dir())
    }

    pub fn load_from(builtin_dir: PathBuf, user_dir: PathBuf) -> Self {
        let mut errors = Vec::new();
        let mut chips: Vec<ChipDefinition> = Vec::new();

        load_directory(&builtin_dir, false, &mut chips, &mut errors);
        load_directory(&user_dir, true, &mut chips, &mut errors);

        if chips.is_empty() {
            errors.push(format!("未找到任何芯片配置文件，请检查目录: {}", builtin_dir.display()));
        }

        chips.sort_by_cached_key(|chip| (chip.series.to_lowercase(), chip.display_name.to_lowercase()));

        Self { chips, errors, builtin_dir, user_dir }
    }

    /// 按 id / 名称精确查找，找不到再按最长前缀匹配（settings.json 里写成完整型号的情形）。
    pub fn resolve(&self, text: &str) -> Option<&ChipDefinition> {
        let text = text.trim();
        if text.is_empty() {
            return None;
        }

        let exact = self
            .chips
            .iter()
            .find(|chip| chip.id.eq_ignore_ascii_case(text) || chip.display_name.eq_ignore_ascii_case(text));
        if exact.is_some() {
            return exact;
        }

        let lowered = text.to_lowercase();
        self.chips
            .iter()
            .flat_map(|chip| chip.effective_prefixes().into_iter().map(move |prefix| (chip, prefix)))
            .filter(|(_, prefix)| lowered.starts_with(&prefix.to_lowercase()))
            .max_by_key(|(_, prefix)| prefix.len())
            .map(|(chip, _)| chip)
    }
}

fn load_directory(directory: &Path, is_user_defined: bool, chips: &mut Vec<ChipDefinition>, errors: &mut Vec<String>) {
    let Ok(entries) = std::fs::read_dir(directory) else {
        return;
    };

    let mut files: Vec<PathBuf> = entries
        .filter_map(|entry| entry.ok().map(|e| e.path()))
        .filter(|path| {
            path.is_file()
                && path
                    .extension()
                    .and_then(|ext| ext.to_str())
                    .is_some_and(|ext| ext.eq_ignore_ascii_case("yaml") || ext.eq_ignore_ascii_case("yml"))
        })
        .collect();
    files.sort_by_cached_key(|path| path.to_string_lossy().to_lowercase());

    for file in files {
        let name = file.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        match parse_file(&file, is_user_defined) {
            Ok(chip) => {
                // 同 id 覆盖：用户目录后加载，所以替换掉内置的那一份。
                match chips.iter_mut().find(|existing| existing.id.eq_ignore_ascii_case(&chip.id)) {
                    Some(existing) => *existing = chip,
                    None => chips.push(chip),
                }
            }
            Err(problem) => errors.push(format!("{name}: {problem}")),
        }
    }
}

fn parse_file(file: &Path, is_user_defined: bool) -> Result<ChipDefinition, String> {
    let text = std::fs::read_to_string(file).map_err(|e| format!("读取失败 - {e}"))?;
    if text.lines().all(|line| line.trim().is_empty() || line.trim_start().starts_with('#')) {
        return Err("文件为空".into());
    }

    let mut chip: ChipDefinition = serde_yaml::from_str(&text).map_err(|e| format!("解析失败 - {e}"))?;
    chip.validate()?;

    chip.id = chip.id.trim().to_string();
    chip.display_name = if chip.name.trim().is_empty() { chip.id.clone() } else { chip.name.trim().to_string() };
    chip.source_file = paths::display(file);
    chip.is_user_defined = is_user_defined;
    Ok(chip)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn repo_catalog() -> ChipCatalog {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
        ChipCatalog::load_from(root.join("chips"), root.join("no-such-user-dir"))
    }

    #[test]
    fn builtin_definitions_all_load() {
        let catalog = repo_catalog();
        assert!(catalog.errors.is_empty(), "{:?}", catalog.errors);
        assert!(catalog.chips.len() >= 31);
    }

    #[test]
    fn resolves_full_part_number_by_longest_prefix() {
        let catalog = repo_catalog();
        assert_eq!(catalog.resolve("STM32F407VGT6").map(|c| c.id.as_str()), Some("stm32f407"));
        assert_eq!(catalog.resolve("stm32f405rg").map(|c| c.id.as_str()), Some("stm32f405"));
        assert_eq!(catalog.resolve("stm32f446re").map(|c| c.id.as_str()), Some("stm32f4"));
        assert!(catalog.resolve("nrf52832").is_none());
    }

    #[test]
    fn esp_chips_use_jtag_and_esp_method() {
        let catalog = repo_catalog();
        let esp32 = catalog.resolve("esp32").unwrap();
        assert_eq!(esp32.transport(), "jtag");
        assert_eq!(esp32.flash_method, FlashMethod::Esp);
    }
}
