# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

跨平台的 STM32 固件烧录工具，基于 **Avalonia (.NET 10)**，底层调用 **OpenOCD**。功能：

- 烧录 / 验证 / 读取固件，复位目标设备
- 支持 ST-Link、J-Link、CMSIS-DAP 三种编程器接口
- 受支持芯片由 `chips/` 目录下的 YAML 文件定义，增删芯片无需改代码、无需重新编译
- OpenOCD 可在线升级与多版本切换（来源为 xPack OpenOCD 发行版）

## Build & Run

```bash
dotnet build Stm32Flash.sln
dotnet run --project src/Stm32Flash.App
```

发布到其它平台（框架依赖）：

```bash
dotnet publish src/Stm32Flash.App -c Release -r win-x64   --self-contained false
dotnet publish src/Stm32Flash.App -c Release -r linux-x64 --self-contained false
dotnet publish src/Stm32Flash.App -c Release -r osx-arm64 --self-contained false
```

## Architecture

`src/Stm32Flash.App/` 按 MVVM 分层，Models / Services / ViewModels 不引用任何 UI 类型
（只有 `OpenOcdManagerViewModel` 为修正列表选中时机用到了 `Dispatcher`）。

### Models

- `ChipDefinition` — 一个芯片定义，对应 `chips/` 下的一个 YAML 文件
- `FlashOptions` — 一次操作的参数快照，`BuildConfigHeader()` 负责拼 OpenOCD 配置头部
- `ProgrammerInterface` — 编程器接口及其 OpenOCD 配置片段（静态定义，不走 YAML）
- `OpenOcdInstallation` / `OpenOcdRelease` / `OpenOcdVersion` — 版本管理用的数据与版本号比较

### Services

- `ChipCatalogService` — 扫描并解析 `chips/*.yaml`，提供按 id / 名称 / 前缀的解析
- `OpenOcdRunner` — 生成临时 cfg、启动 openocd、**流式**回报输出、支持取消与超时
- `OpenOcdVersionService` — 发现本机 OpenOCD、拉取 xPack 发行版、下载安装、卸载
- `SettingsService` — 用户选择持久化到 `settings.json`
- `DialogService` — 文件对话框（Avalonia `StorageProvider`）与自绘消息框，**全部异步**

### 目录约定（`AppPaths`）

| 用途 | 位置 |
| --- | --- |
| 内置芯片定义 | `<程序目录>/chips/` |
| 用户芯片定义（同 id 覆盖内置） | `<AppData>/Stm32FlashTool/chips/` |
| 配置 | `<AppData>/Stm32FlashTool/settings.json` |
| 下载的 OpenOCD | `<LocalAppData>/Stm32FlashTool/openocd/<版本>/` |
| 临时 cfg | `<LocalAppData>/Stm32FlashTool/temp/` |

## Common Tasks

### 新增受支持的芯片

复制 `src/Stm32Flash.App/chips/` 下任一 `.yaml`，改 `id` / `name` / `series` / `targetConfig` 即可。
可选字段：`matchPrefixes`（按前缀解析型号，用于 settings.json 里写成完整型号的情形）、
`flashAddress`、`flashSize`、`adapterSpeed`、`extraConfigLines`。
**不需要改任何 C# 代码。**

### 修改 OpenOCD 命令

在 `OpenOcdRunner.BuildCommands()` 里按操作类型（`FlashOperation`）调整 Tcl 命令。
配置文件头部（接口、target、复位方式）在 `FlashOptions.BuildConfigHeader()`。

### 新增编程器接口

在 `ProgrammerInterface` 里加一个静态实例并加入 `All`，界面下拉框会自动出现该项。

## Implementation Notes

- 目标框架是 `net10.0`（不带 `-windows`）；平台差异只在
  `OpenOcdVersionService`（可执行名、安装包架构与格式、Unix 执行权限）和
  `DialogService.OpenFolder`（explorer / open / xdg-open）里分支处理
- OpenOCD 的 Tcl 配置里路径统一用正斜杠，避免 Windows 反斜杠被当成转义符
- XAML 启用了编译绑定（`AvaloniaUseCompiledBindingsByDefault`），绑定写错会在编译期报错；
  但 `{StaticResource}` 的类型转换是运行期的，`ColumnDefinitions` 这类属性必须写字面量
- Avalonia 的 `ComboBox` 不像 WPF 那样支持 `IsEditable`；目标芯片用的是普通下拉框，
  若将来需要"可选可输入"，得换成 `AutoCompleteBox`
- 内置 OpenOCD 的版本号形如 `0.12.0+dev`，解析不出 xPack 打包修订号；
  `OpenOcdVersion.IsUpgrade()` 对开发版只比较 主.次.补丁，避免一直误报有新版本
