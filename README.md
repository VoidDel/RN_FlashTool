# RN FlashTool

跨平台的 MCU 烧录上位机，基于 [Tauri 2](https://tauri.app)（Rust + 系统 WebView），底层调用 OpenOCD。
Windows / Linux / macOS 共用同一套代码。

目标芯片由 YAML 配置驱动，目前内置 STM32（28 款）与 ESP32（3 款）的定义，
增加新目标只需往 `chips/` 里放一个文件。

## 功能

- **烧录 / 验证 / 读取 / 复位**：支持 ELF、Intel HEX、Motorola S19 与 BIN，格式按扩展名自动识别
- **烧录后校验、烧录后运行**：默认开启，烧完即逐字节比对并让程序跑起来
- **全片擦除**：覆盖芯片的全部 Flash bank（双 bank 芯片不会漏掉后半片），自动跳过 OTP 区
- **复位方式可选**：软件复位只需 SWDIO / SWCLK / GND 三根线；接了 NRST 可改用硬件复位
- **实时日志**：OpenOCD 的输出逐行回显；操作可随时取消，也可设置超时
- **芯片列表可配置**：受支持的芯片由 YAML 文件定义，增删芯片不需要改代码、不需要重新编译
- **SWD / JTAG 均支持**：传输方式写在芯片定义里，Cortex-M 走 SWD、ESP32 走 JTAG
- **OpenOCD 版本管理**：自动发现本机已有的 OpenOCD，可在线下载 xPack OpenOCD 或
  Espressif openocd-esp32、一键切换、删除
- **下载校验**：OpenOCD 与程序更新包下载后都按 GitHub 发布页公布的 SHA-256 校验，
  使用第三方下载加速前缀也不怕被篡改
- **失败原因提示**：把 OpenOCD 的常见报错翻译成可操作的排查建议
- **程序自升级**：可检查、下载新版本并自动替换重启

## 开发

需要 [Rust](https://rustup.rs)（1.88+）。界面是 `ui/` 下的纯 HTML / CSS / JS，没有 npm 构建步骤。

```bash
cargo run            # 调试运行
cargo test           # 单元测试（含 chips/ 下全部 YAML 的解析检查）
cargo clippy --all-targets
```

Linux 下还需要 WebKitGTK 等开发包：

```bash
sudo apt install libwebkit2gtk-4.1-dev libgtk-3-dev librsvg2-dev libxdo-dev libayatana-appindicator3-dev
```

`ui/` 里的文件会在编译时嵌进可执行文件，改完界面要重新 `cargo run` 才能看到。

## 打包

安装包用 Tauri CLI 生成（需要 Node.js，`npx` 会自动下载 CLI）：

```bash
npx @tauri-apps/cli@2 build --bundles nsis               # Windows：setup.exe
npx @tauri-apps/cli@2 build --bundles dmg                # macOS：.dmg
npx @tauri-apps/cli@2 build --bundles deb,rpm,appimage   # Linux
```

产物在 `target/release/bundle/` 下。`chips/` 作为资源打进所有安装包，
`openocd/` 只打进 Windows 安装包（见 `tauri.windows.conf.json`），其它平台在程序里在线下载。

便携包就是把 `target/release/RnFlashTool[.exe]` 和 `chips/`（Windows 再加上 `openocd/`）放在同一个目录里压缩。

推送 `v*` 标签会自动构建全部安装包与便携包并发布 Release，详见
[.github/workflows/release.yml](.github/workflows/release.yml)：

| 平台 | 安装包 | 便携包 |
| --- | --- | --- |
| Windows x64 | `-win-x64-setup.exe` | `-win-x64.zip` |
| macOS | `-osx-arm64.dmg`、`-osx-x64.dmg` | `-osx-arm64.tar.gz`、`-osx-x64.tar.gz` |
| Linux x64 | `-linux-x64.deb`、`.rpm`、`.AppImage` | `-linux-x64.tar.gz` |

运行时依赖系统 WebView：Windows 10 / 11 自带 WebView2（安装包缺失时会自动补装）；
Linux 需要 `libwebkit2gtk-4.1`（deb / rpm 会自动拉取）；macOS 自带。安装包均未签名。

## OpenOCD

程序按以下顺序查找 OpenOCD，任选其一即可：

1. 程序目录或其上级目录中的 `openocd/bin/openocd[.exe]`（Windows 发行包自带）
2. 自己下载安装的版本（`<LocalAppData>/RN_FlashTool/openocd/`）
3. 系统 `PATH`
4. 在界面上手动指定的路径

点击底部状态栏上的 OpenOCD 版本号打开版本管理：可以查看本机已有版本、拉取在线版本列表、
下载安装、设为当前或删除。在线来源有两个：

| 来源 | 适用 |
| --- | --- |
| [xPack OpenOCD](https://github.com/xpack-dev-tools/openocd-xpack) | 通用，STM32 等 Cortex-M 用它 |
| [Espressif openocd-esp32](https://github.com/espressif/openocd-esp32) | **烧录 ESP32 必须用它**：xPack / 官方 OpenOCD 没有 ESP32 的 SPI Flash 驱动 |

访问 GitHub 受限时，可在"下载选项"里填写加速前缀。

> Linux 下使用 USB 调试器通常还需要安装 udev 规则，并把当前用户加入相应用户组。

## 程序自升级

点击状态栏右下角的版本号（如 `v3.0.0`）打开更新窗口，可检查并安装新版本。

升级方式取决于安装方式，下载的文件都会先校验 SHA-256：

| 安装方式 | 升级方式 |
| --- | --- |
| 便携包 | 下载新版便携包 → 解压到暂存目录 → 外部脚本等程序退出后覆盖程序目录并重新拉起 |
| Windows 安装包 | 下载新版 `setup.exe` 并启动它，程序随即退出，由安装程序完成覆盖 |
| .dmg / deb / rpm / AppImage | 文件归系统管，不自动替换，只给出「前往下载页」 |

便携包需要绕外部脚本这一圈，是因为正在运行的可执行文件无法覆盖自己。
覆盖失败时程序仍会被重新拉起，原因写在 `<LocalAppData>/RN_FlashTool/temp/update.log`。

以下两种情况也不提供自动替换，只会给出「前往下载页」：

- 从构建输出目录运行（`cargo run` 启动的开发版本）
- 便携包放在了不可写的目录（例如 `Program Files` 且未以管理员身份运行）

C# 版（2.x）可以直接通过自身的更新功能升级到 3.x，设置、自定义芯片与已下载的 OpenOCD 都会保留。

## 添加自己的芯片

每个芯片一个 YAML 文件。内置定义在 `chips/`，
用户自定义放在 `<AppData>/RN_FlashTool/chips/`（**同 `id` 会覆盖内置定义**）。
界面上点"打开自定义芯片目录"可直接跳转到该目录，改完点"重新加载"即可生效。

```yaml
# 唯一标识，小写
id: stm32f103

# 下拉列表中显示的名称
name: STM32F103

# 所属系列，用于下拉列表分组
series: STM32F1

# OpenOCD target 配置文件，相对于 OpenOCD 的 scripts 目录
targetConfig: target/stm32f1x.cfg

# 传输方式，留空按 swd 处理；ESP32 这类目标需要写 jtag
# transport: swd

# 烧录命令：openocd（默认，flash write_image 等通用命令）或 esp（Espressif 的 program_esp）
# flashMethod: openocd

# 手动输入完整型号（如 stm32f103vgt6）时用于匹配的前缀，按最长前缀命中
matchPrefixes:
  - stm32f103

# 默认烧录起始地址（BIN 固件用）
flashAddress: "0x08000000"

# 可选：Flash 容量，设置后“读取固件”会用它作为默认读取大小
# flashSize: "0x20000"

# 可选：调试器速度（kHz），覆盖编程器接口的默认速度
# adapterSpeed: 4000

# 可选：追加在 target 配置之后的自定义命令
# extraConfigLines:
#   - "reset_config srst_only srst_nogate"

description: STM32F103（Cortex-M3）
```

## 支持的目标与编程器

内置芯片定义：

| 系列 | 数量 | 传输方式 | 烧录命令 |
| --- | --- | --- | --- |
| STM32（F0/F1/F2/F3/F4/F7/H7/L0/L1/L4/G0/G4） | 28 | SWD | openocd |
| ESP32 / ESP32-S2 / ESP32-S3 | 3 | JTAG | esp |

可选编程器：ST-Link、J-Link、CMSIS-DAP、ESP USB-JTAG（内置）、ESP-PROG（FTDI）。

选择的编程器若不支持目标芯片所需的传输方式，程序会在执行前直接提示，
而不是把晦涩的 OpenOCD 报错丢给你。

> ESP32 只能烧录 BIN 固件（ESP-IDF 构建目录下的 `<工程名>.bin`），地址取芯片定义里的
> `flashAddress`（默认 `0x10000`，即应用分区）。**这三个定义尚未在真机上验证过**，
> 必要时请用 `extraConfigLines` 调整。

## 项目结构

```
Cargo.toml / build.rs / tauri.conf.json
src/
├── main.rs          入口：注册插件与命令
├── commands.rs      前端可调用的命令，只做参数转换与状态管理
├── model.rs         固件格式、操作类型、编程器接口、复位方式
├── chips.rs         芯片定义与 chips/ 目录的加载
├── settings.rs      settings.json 读写（兼容 C# 版的字段名）
├── paths.rs         各数据目录的位置与旧目录迁移
├── version.rs       版本号的宽松解析与比较
├── github.rs        GitHub Releases 访问、带 SHA-256 校验的下载
├── archive.rs       zip / tar.gz 解压
├── app_update.rs    程序自升级
└── openocd/
    ├── config.rs    拼 OpenOCD 配置与 Tcl 命令（纯函数）
    ├── runner.rs    启动 openocd、流式回报输出、取消与超时
    └── install.rs   本机发现、在线版本、下载安装与卸载
ui/                  界面（index.html / style.css / app.js）
chips/               内置芯片定义（YAML，每芯片一个文件）
openocd/             随 Windows 发行包分发的 xPack OpenOCD
```

## 数据存放位置

| 用途 | Windows | Linux | macOS |
| --- | --- | --- | --- |
| 配置 | `%APPDATA%\RN_FlashTool\settings.json` | `~/.config/RN_FlashTool/settings.json` | `~/.config/RN_FlashTool/settings.json` |
| 自定义芯片 | `%APPDATA%\RN_FlashTool\chips\` | `~/.config/RN_FlashTool/chips/` | `~/.config/RN_FlashTool/chips/` |
| 下载的 OpenOCD | `%LOCALAPPDATA%\RN_FlashTool\openocd\` | `~/.local/share/RN_FlashTool/openocd/` | `~/Library/Application Support/RN_FlashTool/openocd/` |

## 许可证

本项目以 [MIT License](LICENSE) 发布。

发行包内附带的 OpenOCD 为 xPack 构建，以 GPL-2.0-or-later 发布，
程序通过启动独立进程的方式调用它，不与其链接。
各组件的许可证与源码地址见 [第三方组件声明](THIRD-PARTY-NOTICES.md)。
