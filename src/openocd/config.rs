//! 拼 OpenOCD 配置与 Tcl 命令。这里全是纯函数，方便单元测试。

use std::path::{Path, PathBuf};

use crate::chips::{ChipDefinition, FlashMethod};
use crate::model::{FirmwareFormat, FlashOperation, ProgrammerInterface, ResetMode, parse_number};

/// 一次操作的全部参数快照。
pub struct FlashOptions {
    pub openocd_path: PathBuf,
    /// OpenOCD 的 scripts 目录；为空时交给 OpenOCD 自行推断。
    pub scripts_dir: Option<PathBuf>,
    pub chip: ChipDefinition,
    pub interface: &'static ProgrammerInterface,
    pub reset_mode: ResetMode,
    pub firmware_path: String,
    pub format: FirmwareFormat,
    pub address: String,
    pub read_size: String,
    pub full_erase: bool,
    pub verify_after_flash: bool,
    pub run_after_flash: bool,
    pub timeout_seconds: u32,
}

/// 擦除全部 flash bank。旧版只擦 bank 0，双 bank 芯片的后半片会漏掉；
/// OTP 区域不能整片擦，按名字跳过（OpenOCD 里都叫 `<芯片>.otp`）。
const ERASE_ALL_BANKS: &str = r#"set rn_bank 0
foreach rn_info [flash list] {
    if {![string match -nocase "*otp*" [dict get $rn_info name]]} {
        flash erase_sector $rn_bank 0 last
    }
    incr rn_bank
}"#;

/// openocd-esp32 里除了 SPI Flash 本身，还有 irom / drom 这类映射出来的 bank，只认 `*.flash` 那个。
const ESP_FLASH_BANK_PROC: &str = r#"proc rn_esp_flash_bank {} {
    set index 0
    foreach info [flash list] {
        if {[string match "*.flash" [dict get $info name]]} { return $index }
        incr index
    }
    return 0
}"#;

impl FlashOptions {
    /// 配置文件的公共头部：接口与传输方式、target、复位方式，最后 init + halt 进入可操作状态。
    pub fn build_config_header(&self) -> String {
        let mut lines: Vec<String> = self.interface.config_lines.iter().map(|line| line.to_string()).collect();

        // 传输方式由芯片决定：同一个调试器接 STM32 走 SWD、接 ESP32 走 JTAG。
        lines.push(format!("transport select {}", self.chip.transport()));

        // 芯片定义里的速度优先于接口默认速度，所以放在接口配置之后。
        let speed = self.chip.adapter_speed.filter(|s| *s > 0).unwrap_or(self.interface.default_speed);
        if speed > 0 {
            lines.push(format!("adapter speed {speed}"));
        }

        lines.push(format!("source [find {}]", self.chip.target_config.trim()));

        // 放在 target 之后，才能覆盖 target 配置里的默认复位方式。
        if let Some(reset) = self.reset_mode.config_line() {
            lines.push(reset.to_string());
        }

        lines.extend(self.chip.extra_config_lines.iter().filter(|line| !line.trim().is_empty()).cloned());
        lines.push("init".into());
        lines.push("halt".into());

        lines.join("\n")
    }

    /// 操作前的前置检查，返回给用户看的错误说明。
    pub fn validate(&self, operation: FlashOperation, output_path: Option<&str>) -> Result<(), String> {
        if !self.openocd_path.is_file() {
            return Err("未找到 openocd 可执行文件。请在“OpenOCD 版本”中下载或指定一个可用版本。".into());
        }

        let transport = self.chip.transport();
        if !self.interface.supports(transport) {
            return Err(format!(
                "编程器“{}”不支持 {transport} 传输方式，而 {} 需要 {transport}。请改选其它编程器。",
                self.interface.display_name, self.chip.display_name
            ));
        }

        let is_esp = self.chip.flash_method == FlashMethod::Esp;

        if matches!(operation, FlashOperation::Flash | FlashOperation::Verify) {
            if self.firmware_path.trim().is_empty() {
                return Err("固件路径为空".into());
            }
            if !Path::new(&self.firmware_path).is_file() {
                return Err(format!("固件文件不存在: {}", self.firmware_path));
            }
            if is_esp && self.format != FirmwareFormat::Bin {
                return Err(format!(
                    "{} 只能烧录 BIN 固件（ESP-IDF 构建目录下的 <工程名>.bin），当前是 {}。",
                    self.chip.display_name,
                    self.format.display_name()
                ));
            }
            if (is_esp || !self.format.carries_address()) && parse_number(&self.address).is_none() {
                return Err(format!("烧录地址无法识别: {}", self.address));
            }
        }

        if operation == FlashOperation::Read {
            if output_path.is_none_or(|p| p.trim().is_empty()) {
                return Err("未指定保存路径".into());
            }
            if parse_number(&self.address).is_none() {
                return Err(format!("起始地址无法识别: {}", self.address));
            }
            if parse_number(&self.read_size).is_none_or(|size| size == 0) {
                return Err(format!("读取大小无法识别: {}", self.read_size));
            }
        }

        if is_esp
            && operation != FlashOperation::Reset
            && supports_program_esp(self.scripts_dir.as_deref()) == Some(false)
        {
            return Err(format!(
                "当前 OpenOCD 不支持 {} 的 Flash 操作。\n\n\
                 xPack / 官方 OpenOCD 没有 Espressif 的 SPI Flash 驱动，\
                 请在“OpenOCD 版本管理”里切到“Espressif openocd-esp32”下载一个版本并设为当前。",
                self.chip.display_name
            ));
        }

        Ok(())
    }

    /// 按操作类型拼出要执行的 Tcl 命令。调用前应已通过 [`Self::validate`]。
    pub fn build_commands(&self, operation: FlashOperation, output_path: Option<&str>) -> String {
        let firmware = tcl_path(&self.firmware_path);
        let address = self.address.trim();
        let mut commands: Vec<String> = Vec::new();

        match (operation, self.chip.flash_method) {
            (FlashOperation::Reset, _) => commands.push("reset run".into()),

            (FlashOperation::Flash, FlashMethod::Openocd) => {
                // ELF / HEX / S19 不写 type，OpenOCD 会按文件内容自己认；只有裸二进制要补地址。
                let image =
                    if self.format.carries_address() { firmware.clone() } else { format!("{firmware} {address} bin") };

                commands.push("reset halt".into());
                if self.full_erase {
                    commands.push(ERASE_ALL_BANKS.into());
                    // 已经整片擦过，write_image 不必再按扇区擦一遍。
                    commands.push(format!("flash write_image {image}"));
                } else {
                    commands.push(format!("flash write_image erase {image}"));
                }
                if self.verify_after_flash {
                    commands.push(format!("verify_image {image}"));
                }
                if self.run_after_flash {
                    commands.push("reset run".into());
                }
            }

            (FlashOperation::Verify, FlashMethod::Openocd) => commands.push(if self.format.carries_address() {
                format!("verify_image {firmware}")
            } else {
                format!("verify_image {firmware} {address} bin")
            }),

            (FlashOperation::Read, FlashMethod::Openocd) => commands.push(format!(
                "dump_image {} {address} {}",
                tcl_path(output_path.unwrap_or_default()),
                self.read_size.trim()
            )),

            (FlashOperation::Flash, FlashMethod::Esp) => {
                commands.push(ESP_FLASH_BANK_PROC.into());
                if self.full_erase {
                    commands.push("flash erase_sector [rn_esp_flash_bank] 0 last".into());
                }
                let verify = if self.verify_after_flash { " verify" } else { "" };
                commands.push(format!("program_esp {firmware} {address}{verify}"));
                if self.run_after_flash {
                    commands.push("reset run".into());
                }
            }

            (FlashOperation::Verify, FlashMethod::Esp) => {
                commands.push(ESP_FLASH_BANK_PROC.into());
                commands.push(format!("flash verify_bank [rn_esp_flash_bank] {firmware} {address}"));
            }

            (FlashOperation::Read, FlashMethod::Esp) => {
                commands.push(ESP_FLASH_BANK_PROC.into());
                commands.push(format!(
                    "flash read_bank [rn_esp_flash_bank] {} {address} {}",
                    tcl_path(output_path.unwrap_or_default()),
                    self.read_size.trim()
                ));
            }
        }

        commands.join("\n")
    }
}

/// 把文件路径转成 Tcl 双引号字符串。
///
/// 统一用正斜杠，避免 Windows 反斜杠被当成转义符；
/// `[`、`]`、`$` 在 Tcl 双引号里会触发命令 / 变量替换（文件名 `fw[v1].hex` 会被当成执行 `v1`），必须转义。
pub fn tcl_path(path: &str) -> String {
    let absolute = std::path::absolute(path).unwrap_or_else(|_| PathBuf::from(path));
    let mut text = absolute.to_string_lossy().into_owned();
    if cfg!(windows) {
        text = text.replace('\\', "/");
    }

    let mut quoted = String::with_capacity(text.len() + 2);
    quoted.push('"');
    for c in text.chars() {
        if matches!(c, '\\' | '[' | ']' | '$' | '"') {
            quoted.push('\\');
        }
        quoted.push(c);
    }
    quoted.push('"');
    quoted
}

/// 检查 scripts 目录里有没有 Espressif 的 `program_esp`。目录不明时返回 `None`，交给 OpenOCD 自己报错。
pub fn supports_program_esp(scripts_dir: Option<&Path>) -> Option<bool> {
    let target_dir = scripts_dir?.join("target");
    let entries = std::fs::read_dir(&target_dir).ok()?;

    Some(entries.filter_map(Result::ok).any(|entry| {
        let name = entry.file_name().to_string_lossy().to_lowercase();
        name.starts_with("esp")
            && std::fs::read_to_string(entry.path()).is_ok_and(|text| text.contains("proc program_esp"))
    }))
}

/// 把 OpenOCD 的常见报错翻译成可操作的排查建议。
pub fn analyze_failure(output: &str, exit_code: i32) -> String {
    let lowered = output.to_lowercase();
    let has = |needle: &str| lowered.contains(needle);

    let hint = if has("invalid command name \"program_esp\"") {
        "当前 OpenOCD 没有 Espressif 的烧录命令：\n• 在“OpenOCD 版本管理”里下载 Espressif openocd-esp32 并设为当前"
    } else if has("cannot read idr") || has("error connecting dp") {
        "连接问题：\n• 检查调试器与目标板的接线（SWDIO / SWCLK / GND）\n• 确认目标芯片已上电\n\
         • 程序关闭了调试口或进入低功耗时，接上 NRST 并把“复位方式”改为硬件复位\n\
         • 尝试降低调试速度（在芯片 YAML 里设置 adapterSpeed）"
    } else if has("unknown chip") || has("unable to identify target") {
        "芯片识别失败：\n• 确认选择的目标芯片型号正确\n• 芯片可能处于读保护/锁定状态\n• 尝试接上 NRST 并改用硬件复位"
    } else if has("verify failed") || has("contents differ") || (has("checksum mismatch") && has("instead of")) {
        "校验不一致：\n• 芯片内容与固件文件不同，可能烧录未完成或写入了其它固件\n• BIN 文件请确认起始地址是否正确"
    } else if ["unable to find a matching", "no device found", "no j-link device", "device not found", "unable to open"]
        .iter()
        .any(|needle| has(needle))
    {
        "找不到调试器：\n• 确认调试器已插好并被系统识别\n• 检查“编程器”选择是否与实际硬件一致\n\
         • Windows 下可能需要用 Zadig 安装 WinUSB 驱动；Linux 下需要 udev 规则"
    } else if has("libusb_open failed") || has("open failed") {
        "打开调试器失败：\n• 设备可能被其它程序占用（如 STM32CubeProgrammer、Keil）\n• 关闭占用程序后重试"
    } else if has("can't find") || has("no such file") {
        "找不到配置文件：\n• 检查芯片 YAML 中的 targetConfig 在当前 OpenOCD 版本里是否存在\n• 或切换到另一个 OpenOCD 版本"
    } else if has("timed out") || has("timeout") {
        "通信超时：\n• 检查硬件连接与供电\n• 尝试降低调试速度"
    } else {
        ""
    };

    let summary = format!("OpenOCD 以退出码 {exit_code} 结束，详见日志。");
    if hint.is_empty() { summary } else { format!("{summary}\n\n{hint}") }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn chip(method: FlashMethod) -> ChipDefinition {
        ChipDefinition {
            id: "stm32f4".into(),
            display_name: "STM32F4 系列".into(),
            target_config: "target/stm32f4x.cfg".into(),
            flash_method: method,
            ..Default::default()
        }
    }

    fn options(method: FlashMethod, format: FirmwareFormat) -> FlashOptions {
        FlashOptions {
            openocd_path: PathBuf::from("openocd"),
            scripts_dir: None,
            chip: chip(method),
            interface: ProgrammerInterface::from_id("cmsis-dap"),
            reset_mode: ResetMode::Software,
            firmware_path: "fw/app.bin".into(),
            format,
            address: "0x08000000".into(),
            read_size: "0x10000".into(),
            full_erase: false,
            verify_after_flash: true,
            run_after_flash: true,
            timeout_seconds: 60,
        }
    }

    #[test]
    fn quotes_tcl_special_characters() {
        let quoted = tcl_path("fw[v1]/$name\"x\".hex");
        assert!(quoted.starts_with('"') && quoted.ends_with('"'));
        assert!(quoted.contains(r#"fw\[v1\]/\$name\"x\".hex"#));
        if cfg!(windows) {
            // 盘符后面不能留反斜杠，否则 Tcl 会把它当转义
            assert!(!quoted.contains(":\\"));
        }
    }

    #[test]
    fn header_uses_chip_transport_and_reset_mode() {
        let mut opts = options(FlashMethod::Openocd, FirmwareFormat::Elf);
        let header = opts.build_config_header();
        assert!(header.contains("transport select swd"));
        assert!(!header.contains("reset_config"));
        assert!(header.ends_with("init\nhalt"));

        opts.reset_mode = ResetMode::Hardware;
        let header = opts.build_config_header();
        let target = header.find("source [find target/").unwrap();
        let reset = header.find("reset_config srst_only").unwrap();
        assert!(reset > target, "复位方式要写在 target 之后才能覆盖它");
    }

    #[test]
    fn chip_speed_overrides_interface_default() {
        let mut opts = options(FlashMethod::Openocd, FirmwareFormat::Elf);
        opts.interface = ProgrammerInterface::from_id("stlink");
        assert!(opts.build_config_header().contains("adapter speed 4000"));
        opts.chip.adapter_speed = Some(1000);
        assert!(opts.build_config_header().contains("adapter speed 1000"));
    }

    #[test]
    fn flash_bin_writes_verifies_and_runs() {
        let commands = options(FlashMethod::Openocd, FirmwareFormat::Bin).build_commands(FlashOperation::Flash, None);
        let lines: Vec<&str> = commands.lines().collect();
        assert_eq!(lines[0], "reset halt");
        assert!(lines[1].starts_with("flash write_image erase \"") && lines[1].ends_with(" 0x08000000 bin"));
        assert!(lines[2].starts_with("verify_image \""));
        assert_eq!(lines[3], "reset run");
    }

    #[test]
    fn full_erase_covers_every_bank_and_skips_sector_erase() {
        let mut opts = options(FlashMethod::Openocd, FirmwareFormat::Elf);
        opts.full_erase = true;
        opts.verify_after_flash = false;
        opts.run_after_flash = false;
        let commands = opts.build_commands(FlashOperation::Flash, None);
        assert!(commands.contains("foreach rn_info [flash list]"));
        assert!(commands.contains("flash write_image \""));
        assert!(!commands.contains("write_image erase"));
        assert!(!commands.contains("reset run"));
    }

    #[test]
    fn esp_flash_uses_program_esp() {
        let opts = options(FlashMethod::Esp, FirmwareFormat::Bin);
        let commands = opts.build_commands(FlashOperation::Flash, None);
        assert!(commands.contains("program_esp \""));
        assert!(commands.contains(" 0x08000000 verify"));
        let read = opts.build_commands(FlashOperation::Read, Some("dump.bin"));
        assert!(read.contains("flash read_bank [rn_esp_flash_bank]"));
    }

    #[test]
    fn explains_common_failures() {
        assert!(analyze_failure("Error: Error connecting DP: cannot read IDR", 1).contains("NRST"));
        assert!(analyze_failure("invalid command name \"program_esp\"", 1).contains("openocd-esp32"));
        assert!(analyze_failure("Error: unable to find a matching CMSIS-DAP device", 1).contains("找不到调试器"));
        assert!(analyze_failure("Error: No J-Link device found.", 1).contains("找不到调试器"));
        assert_eq!(analyze_failure("whatever", 3), "OpenOCD 以退出码 3 结束，详见日志。");
    }
}
