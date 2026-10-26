//! 固件格式、操作类型、编程器接口与复位方式这些纯数据定义。

use serde::{Deserialize, Serialize};

pub const DEFAULT_ADDRESS: &str = "0x08000000";
pub const DEFAULT_READ_SIZE: &str = "0x10000";
pub const DEFAULT_CHIP_ID: &str = "stm32f4";
pub const DEFAULT_TIMEOUT_SECONDS: u32 = 60;

/// 固件文件格式。除 BIN 外都把地址写在文件里。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum FirmwareFormat {
    Elf,
    Bin,
    Ihex,
    S19,
}

impl FirmwareFormat {
    pub fn display_name(self) -> &'static str {
        match self {
            Self::Elf => "ELF",
            Self::Bin => "BIN",
            Self::Ihex => "Intel HEX",
            Self::S19 => "Motorola S19",
        }
    }

    /// 该格式是否把地址写在文件里，烧录 / 验证时不用再问。
    pub fn carries_address(self) -> bool {
        self != Self::Bin
    }

    /// 根据扩展名推断格式，认不出来时返回 `None`。这些扩展名都是 OpenOCD `image_open` 认的那几类。
    pub fn from_file_name(path: &str) -> Option<Self> {
        let extension = std::path::Path::new(path).extension()?.to_str()?.to_ascii_lowercase();
        match extension.as_str() {
            "bin" => Some(Self::Bin),
            "elf" | "axf" | "out" => Some(Self::Elf),
            "hex" | "ihex" => Some(Self::Ihex),
            "s19" | "s28" | "s37" | "srec" | "mot" => Some(Self::S19),
            _ => None,
        }
    }
}

/// 可以对目标芯片执行的操作。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum FlashOperation {
    Flash,
    Verify,
    Read,
    Reset,
}

impl FlashOperation {
    pub fn display_name(self) -> &'static str {
        match self {
            Self::Flash => "烧录",
            Self::Verify => "验证",
            Self::Read => "读取",
            Self::Reset => "复位",
        }
    }
}

/// 复位方式。
///
/// C# 版给 J-Link / CMSIS-DAP 默认写死了 `srst_only`，
/// 只接 SWDIO / SWCLK / GND 三根线时“复位”其实什么都没做，所以改成可选、默认走软件复位。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ResetMode {
    /// 不写 reset_config，沿用接口与 target 配置的默认值（Cortex-M 即 SYSRESETREQ）。
    Software,
    /// 通过 NRST 引脚硬复位，并在连接时拉住复位，适合程序关掉了调试口或进了低功耗的情形。
    Hardware,
}

impl ResetMode {
    pub fn config_line(self) -> Option<&'static str> {
        match self {
            Self::Software => None,
            Self::Hardware => Some("reset_config srst_only srst_nogate connect_assert_srst"),
        }
    }
}

/// 一种调试器 / 编程器接口及其 OpenOCD 配置片段。
/// 传输方式（swd / jtag）不在这里，由芯片定义决定。
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProgrammerInterface {
    pub id: &'static str,
    pub display_name: &'static str,
    #[serde(skip)]
    pub config_lines: &'static [&'static str],
    /// 默认调试速度（kHz），0 表示不显式设置。
    #[serde(skip)]
    pub default_speed: u32,
    /// 支持的传输方式，用于在选错时提前提示。
    pub supported_transports: &'static [&'static str],
}

impl ProgrammerInterface {
    pub fn supports(&self, transport: &str) -> bool {
        self.supported_transports.iter().any(|t| t.eq_ignore_ascii_case(transport))
    }

    pub fn from_id(id: &str) -> &'static Self {
        INTERFACES.iter().find(|item| item.id.eq_ignore_ascii_case(id)).unwrap_or(&INTERFACES[2])
    }
}

pub static INTERFACES: [ProgrammerInterface; 5] = [
    ProgrammerInterface {
        id: "stlink",
        display_name: "ST-Link",
        config_lines: &["source [find interface/stlink.cfg]"],
        default_speed: 4000,
        supported_transports: &["swd", "hla_swd", "jtag"],
    },
    ProgrammerInterface {
        id: "jlink",
        display_name: "J-Link",
        config_lines: &["source [find interface/jlink.cfg]"],
        default_speed: 0,
        supported_transports: &["swd", "jtag"],
    },
    ProgrammerInterface {
        id: "cmsis-dap",
        display_name: "CMSIS-DAP",
        config_lines: &["source [find interface/cmsis-dap.cfg]"],
        default_speed: 0,
        supported_transports: &["swd", "jtag"],
    },
    ProgrammerInterface {
        id: "esp_usb_jtag",
        display_name: "ESP USB-JTAG（内置）",
        config_lines: &["source [find interface/esp_usb_jtag.cfg]"],
        default_speed: 0,
        supported_transports: &["jtag"],
    },
    ProgrammerInterface {
        id: "esp_prog",
        display_name: "ESP-PROG（FTDI）",
        config_lines: &["source [find interface/ftdi/esp32_devkitj_v1.cfg]"],
        default_speed: 0,
        supported_transports: &["jtag"],
    },
];

/// 一次操作的结论，最后一条 Channel 消息会带上它。
#[derive(Debug, Clone, Serialize)]
pub struct OperationResult {
    pub success: bool,
    pub summary: String,
    pub detail: String,
}

impl OperationResult {
    pub fn ok(summary: impl Into<String>) -> Self {
        Self { success: true, summary: summary.into(), detail: String::new() }
    }

    pub fn fail(summary: impl Into<String>, detail: impl Into<String>) -> Self {
        Self { success: false, summary: summary.into(), detail: detail.into() }
    }
}

/// 下载 / 安装过程中的进度回报，`percent` 为 `None` 表示无法计算。
#[derive(Debug, Clone, Serialize)]
pub struct Progress {
    pub stage: String,
    pub percent: Option<f64>,
}

impl Progress {
    pub fn new(stage: impl Into<String>, percent: Option<f64>) -> Self {
        Self { stage: stage.into(), percent }
    }
}

/// 接受 `0x` 前缀的十六进制与普通十进制，与 OpenOCD 的写法一致。
pub fn parse_number(text: &str) -> Option<u64> {
    let text = text.trim();
    match text.get(..2) {
        Some(prefix) if prefix.eq_ignore_ascii_case("0x") => u64::from_str_radix(&text[2..], 16).ok(),
        _ => text.parse().ok(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_format_from_extension() {
        assert_eq!(FirmwareFormat::from_file_name("a/b/app.HEX"), Some(FirmwareFormat::Ihex));
        assert_eq!(FirmwareFormat::from_file_name("app.axf"), Some(FirmwareFormat::Elf));
        assert_eq!(FirmwareFormat::from_file_name("app.srec"), Some(FirmwareFormat::S19));
        assert_eq!(FirmwareFormat::from_file_name("app"), None);
    }

    #[test]
    fn parses_numbers() {
        assert_eq!(parse_number("0x08000000"), Some(0x0800_0000));
        assert_eq!(parse_number("65536"), Some(65536));
        assert_eq!(parse_number("0xZZ"), None);
        assert_eq!(parse_number("1; shutdown"), None);
    }

    #[test]
    fn unknown_interface_falls_back_to_cmsis_dap() {
        assert_eq!(ProgrammerInterface::from_id("nope").id, "cmsis-dap");
        assert_eq!(ProgrammerInterface::from_id("STLINK").id, "stlink");
    }
}
