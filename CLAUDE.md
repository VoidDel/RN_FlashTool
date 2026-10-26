# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

跨平台的 MCU 烧录工具 **RN FlashTool**，基于 **Tauri 2（Rust + 系统 WebView）**，底层调用 **OpenOCD**。功能：

- 烧录 / 验证 / 读取固件，复位目标设备；烧录后可选校验、运行
- 支持 ST-Link、J-Link、CMSIS-DAP、ESP USB-JTAG、ESP-PROG 五种编程器接口
- 传输方式（swd / jtag）与烧录命令（openocd / esp）由芯片定义决定
- 受支持芯片由 `chips/` 目录下的 YAML 文件定义，增删芯片无需改代码、无需重新编译
- OpenOCD 可在线下载与多版本切换，来源为 xPack OpenOCD 与 Espressif openocd-esp32
- 程序自身可检查更新、下载并自动替换重启；所有下载都做 SHA-256 校验

2026-10 由 C#（Avalonia）重写为 Rust，C# 版的代码只在 git 历史里。

## Build & Run

```bash
cargo run                                   # 调试运行
cargo test                                  # 单元测试，含 chips/*.yaml 全量解析
cargo clippy --all-targets -- -D warnings   # CI 要求零警告
cargo fmt                                   # 风格见 rustfmt.toml（120 列）
node --check ui/app.js                      # 前端没有构建步骤，CI 只做语法检查
```

`ui/` 在编译期嵌进可执行文件（`tauri.conf.json` 的 `frontendDist`），改了界面要重新编译。
`tauri` 开启了 `custom-protocol` 特性，所以直接 `cargo build` 就是可分发的产物，不需要 tauri-cli。

## Architecture

Rust 侧负责一切系统交互，前端只管状态与展示。前端通过 `window.__TAURI__.core.invoke` 调命令，
长任务的日志 / 进度用 `Channel` 推回（参数名在 JS 里是 camelCase，如 `onEvent`、`installDirectory`）。

### Rust（`src/`）

- `commands.rs` — 全部 Tauri 命令与 `AppState`。只做参数转换与状态管理；
  `Slot` 保证同一时间只有一个烧录操作、一个下载任务，并持有取消令牌
- `model.rs` — `FirmwareFormat`、`FlashOperation`、`ResetMode`、`ProgrammerInterface`（静态表 `INTERFACES`）
- `chips.rs` — `ChipDefinition`（YAML，camelCase 字段）与 `ChipCatalog`（加载、同 id 覆盖、按前缀解析）
- `openocd/config.rs` — **纯函数**：`FlashOptions::build_config_header()` 拼配置头，
  `build_commands()` 按操作 × 烧录方式拼 Tcl，`validate()` 做前置检查，`analyze_failure()` 翻译报错
- `openocd/runner.rs` — 写临时 cfg、启动 openocd、两个输出流并发逐行读、取消 / 超时用 `tokio::select!`
- `openocd/install.rs` — 本机发现、`Provider`（xpack / espressif）的资产匹配、下载安装、卸载
- `github.rs` — Releases 列表解析与 `download_verified()`
- `app_update.rs` — 自升级：检查、准备暂存目录、生成并启动替换脚本
- `settings.rs` — `settings.json`，前端用 `update_settings` 只传改动的字段
- `paths.rs` — 目录约定与改名前旧目录的迁移

### 前端（`ui/`）

`index.html` + `style.css` + `app.js`，不用框架。`app.js` 里 `state` 是主窗口状态，
`manager` / `updater` 两个对象分别对应 OpenOCD 版本管理与程序更新对话框，
`showMessage` / `askMemoryRange` 是按需创建的小对话框。

### 目录约定（`paths.rs`）

| 用途 | 位置 |
| --- | --- |
| 内置芯片定义 | `<程序目录>/chips/`，开发时向上找到仓库根部的 `chips/` |
| 用户芯片定义（同 id 覆盖内置） | `<AppData>/RN_FlashTool/chips/` |
| 配置 | `<AppData>/RN_FlashTool/settings.json` |
| 下载的 OpenOCD | `<LocalAppData>/RN_FlashTool/openocd/<版本>/` |
| 临时 cfg、下载中的包、升级脚本 | `<LocalAppData>/RN_FlashTool/temp/` |

## Common Tasks

### 新增受支持的芯片

复制 `chips/` 下任一 `.yaml`，改 `id` / `name` / `series` / `targetConfig` 即可。
可选字段：`transport`、`flashMethod`、`matchPrefixes`、`flashAddress`、`flashSize`、`adapterSpeed`、`extraConfigLines`。
**不需要改任何代码。** `cargo test` 会解析全部内置 YAML，写错字段能立刻发现。

### 修改 OpenOCD 命令

在 `openocd/config.rs` 的 `build_commands()` 里按 `(FlashOperation, FlashMethod)` 调整，
配置头部（接口、传输方式、target、复位方式）在 `build_config_header()`。两者都有单元测试。

### 新增编程器接口

在 `model.rs` 的 `INTERFACES` 里加一项，界面下拉框会自动出现。`supported_transports` 用于提前拦截不匹配的组合。

### 新增非 ARM 架构的目标

只要 OpenOCD 自带对应的 `target/*.cfg`，加一个 YAML 即可，把 `transport` 写成该架构需要的值。
烧录命令与 Cortex-M 不同时，在 `chips.rs` 的 `FlashMethod` 加一种方式并在 `build_commands()` 里实现，
`flashMethod: esp` 是现成的参照。

## Implementation Notes

- **Tcl 路径**：`tcl_path()` 统一转正斜杠，并转义 `[` `]` `$` `"` `\`。
  只套双引号的话，文件名 `fw[v1].hex` 会被 Tcl 当成执行命令 `v1`
- **复位方式**：软件复位不写 `reset_config`（Cortex-M 即 SYSRESETREQ，三线即可）；
  硬件复位写 `srst_only srst_nogate connect_assert_srst`，必须放在 `source target` 之后才能覆盖 target 的默认值
- **全片擦除**遍历 `flash list` 的所有 bank，按名字跳过 `*otp*`；擦过之后 `write_image` 不再带 `erase`
- **ESP32**：xPack / 官方 OpenOCD 没有 ESP32 的 SPI Flash 驱动，`program_esp` 只在 openocd-esp32 里有。
  `validate()` 会检查 scripts 目录里有没有 `proc program_esp`，没有就提前给出切换版本的提示
- **下载校验**：期望的 SHA-256 取自直连 api.github.com 的 `digest` 字段，镜像（加速前缀）改不了它；
  没有 `digest` 时从同一 Release 的校验和文件取（也直连 GitHub）。自升级拿不到校验和就拒绝安装
- **settings.json 字段名是 PascalCase**，与 C# 版一致，旧版写下的文件可以原样读；时间戳解析失败当作没检查过
- **可执行文件名保持 `RnFlashTool(.exe)`**，发行包名保持 `RnFlashTool-<版本>-<RID>`：
  C# 2.x 的自升级按这两个名字找包、校验、重启，改了就没法从旧版直接升上来
- 版本号：发布流水线用环境变量 `RN_FLASHTOOL_VERSION` 注入标签版本，本地构建取 Cargo.toml
- 自升级脚本：Windows 是 PowerShell（写入时带 BOM，否则中文路径会乱码），覆盖失败也会重新拉起程序，
  原因写进 `temp/update.log`
- 启动 openocd 一律经 `openocd::hidden_command()`：带 `CREATE_NO_WINDOW`（否则 Windows 会闪黑窗）与 `kill_on_drop`
- 内置 OpenOCD 的版本号形如 `0.12.0+dev`，解析不出 xPack 打包修订号；
  `version::is_upgrade()` 对开发版只比较 主.次.补丁，避免一直误报有新版本
- 文件对话框、打开目录都在 Rust 侧调用插件的 Rust API，所以 `capabilities/default.json` 只需要 `core:default`
