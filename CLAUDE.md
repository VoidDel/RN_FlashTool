# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

跨平台的 MCU 烧录工具 **RN FlashTool**，基于 **Avalonia (.NET 10)**，底层调用 **OpenOCD**。功能：

- 烧录 / 验证 / 读取固件，复位目标设备
- 支持 ST-Link、J-Link、CMSIS-DAP、ESP USB-JTAG、ESP-PROG 五种编程器接口
- 传输方式（swd / jtag）由芯片定义决定，同一调试器可用于不同架构的目标
- 受支持芯片由 `chips/` 目录下的 YAML 文件定义，增删芯片无需改代码、无需重新编译
- OpenOCD 可在线升级与多版本切换（来源为 xPack OpenOCD 发行版）
- 程序自身可检查更新、下载并自动替换重启

## Build & Run

```bash
dotnet build RN_FlashTool.sln
dotnet run --project src/RnFlashTool.App
```

发布到其它平台（框架依赖）：

```bash
dotnet publish src/RnFlashTool.App -c Release -r win-x64   --self-contained false
dotnet publish src/RnFlashTool.App -c Release -r linux-x64 --self-contained false
dotnet publish src/RnFlashTool.App -c Release -r osx-arm64 --self-contained false
```

## Architecture

`src/RnFlashTool.App/` 按 MVVM 分层，Models / Services / ViewModels 不引用任何 UI 类型
（只有 `OpenOcdManagerViewModel` 为修正列表选中时机用到了 `Dispatcher`）。

### Models

- `ChipDefinition` — 一个芯片定义，对应 `chips/` 下的一个 YAML 文件
- `FlashOptions` — 一次操作的参数快照，`BuildConfigHeader()` 负责拼 OpenOCD 配置头部
- `ProgrammerInterface` — 编程器接口及其 OpenOCD 配置片段（静态定义，不走 YAML）。
  **不含 `transport select`**：传输方式由芯片决定，`Supports()` 用于提前拦截不匹配的组合
- `OpenOcdInstallation` / `OpenOcdRelease` / `OpenOcdVersion` — 版本管理用的数据与版本号比较

### Services

- `ChipCatalogService` — 扫描并解析 `chips/*.yaml`，提供按 id / 名称 / 前缀的解析
- `OpenOcdRunner` — 生成临时 cfg、启动 openocd、**流式**回报输出、支持取消与超时
- `OpenOcdVersionService` — 发现本机 OpenOCD、拉取 xPack 发行版、下载安装、卸载
- `GitHubClient` — 访问 GitHub Releases 的共用客户端，下载加速前缀在此统一生效
- `ArchiveExtractor` — zip / tar.gz 解压与 Unix 执行位修正
- `AppUpdateService` — 程序自升级：检查、下载、生成并启动外部替换脚本
- `SettingsService` — 用户选择持久化到 `settings.json`
- `DialogService` — 文件对话框（Avalonia `StorageProvider`）与自绘消息框，**全部异步**

### 目录约定（`AppPaths`）

| 用途 | 位置 |
| --- | --- |
| 内置芯片定义 | `<程序目录>/chips/` |
| 用户芯片定义（同 id 覆盖内置） | `<AppData>/RN_FlashTool/chips/` |
| 配置 | `<AppData>/RN_FlashTool/settings.json` |
| 下载的 OpenOCD | `<LocalAppData>/RN_FlashTool/openocd/<版本>/` |
| 临时 cfg | `<LocalAppData>/RN_FlashTool/temp/` |

## Common Tasks

### 新增受支持的芯片

复制 `src/RnFlashTool.App/chips/` 下任一 `.yaml`，改 `id` / `name` / `series` / `targetConfig` 即可。
可选字段：`matchPrefixes`（按前缀解析型号，用于 settings.json 里写成完整型号的情形）、
`flashAddress`、`flashSize`、`adapterSpeed`、`extraConfigLines`。
**不需要改任何 C# 代码。**

### 修改 OpenOCD 命令

在 `OpenOcdRunner.BuildCommands()` 里按操作类型（`FlashOperation`）调整 Tcl 命令。
配置文件头部（接口、target、复位方式）在 `FlashOptions.BuildConfigHeader()`。

### 新增编程器接口

在 `ProgrammerInterface` 里加一个静态实例并加入 `All`，界面下拉框会自动出现该项。
`SupportedTransports` 留空表示不做限制。

### 新增非 ARM 架构的目标

只要 OpenOCD 自带对应的 `target/*.cfg`，加一个 YAML 即可，把 `transport` 写成
该架构需要的值（如 ESP32 用 `jtag`）。若烧录命令与 Cortex-M 不同，用
`extraConfigLines` 补充。`chips/esp32.yaml` 是现成的参照。

## Implementation Notes

- 项目在 2026-09 由 STM32_FLASH 改名为 RN_FlashTool。`AppPaths.MigrateLegacyDirectories()`
  负责把旧的 `Stm32FlashTool` 数据目录搬过来，`RemapLegacyPath()` 改写设置里残留的绝对路径；
  两者都是一次性的，可安全重复调用
- `OnOpened` 这类 `async void` 里逸出的异常会直接终止进程。
  ViewModel 的 `InitializeAsync` 必须自己兜住异常，`Program.Main` 里另有
  `Dispatcher.UIThread.UnhandledException` 作为兜底

- 目标框架是 `net10.0`（不带 `-windows`）；平台差异只在
  `OpenOcdVersionService`（可执行名、安装包架构与格式、Unix 执行权限）和
  `DialogService.OpenFolder`（explorer / open / xdg-open）里分支处理
- OpenOCD 的 Tcl 配置里路径统一用正斜杠，避免 Windows 反斜杠被当成转义符
- XAML 启用了编译绑定（`AvaloniaUseCompiledBindingsByDefault`），绑定写错会在编译期报错；
  但 `{StaticResource}` 的类型转换是运行期的，`ColumnDefinitions` 这类属性必须写字面量
- Avalonia 的 `ComboBox` 不像 WPF 那样支持 `IsEditable`；目标芯片用的是普通下拉框，
  若将来需要"可选可输入"，得换成 `AutoCompleteBox`
- 自升级靠外部脚本完成：运行中的可执行文件无法覆盖自己，
  `AppUpdateService` 生成 PowerShell / sh 脚本等本进程退出后再覆盖并重启。
  PowerShell 脚本里有大量字面量花括号，C# 侧必须用 `$$"""` 原始字符串（插值洞写作 `{{ }}`）
- 取当前版本号用 `typeof(AppUpdateService).Assembly` 而非 `Assembly.GetEntryAssembly()`，
  后者在被别的宿主加载时会取到宿主的版本
- 内置 OpenOCD 的版本号形如 `0.12.0+dev`，解析不出 xPack 打包修订号；
  `OpenOcdVersion.IsUpgrade()` 对开发版只比较 主.次.补丁，避免一直误报有新版本
