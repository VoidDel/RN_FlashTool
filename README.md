# RN FlashTool

跨平台的 MCU 烧录上位机，基于 Avalonia (.NET 10)，底层调用 OpenOCD。
Windows / Linux / macOS 共用同一套代码。

目标芯片由 YAML 配置驱动，目前内置 STM32（28 款）与 ESP32（3 款）的定义，
增加新目标只需往 `chips/` 里放一个文件。

## 功能

- **烧录 / 验证 / 读取 / 复位**：支持 ELF 与 BIN，可设置起始地址、读取长度、烧录前全片擦除
- **实时日志**：OpenOCD 的输出逐行回显，不必等进程结束；操作可随时取消，也可设置超时
- **芯片列表可配置**：受支持的芯片由 YAML 文件定义，增删芯片不需要改代码、不需要重新编译
- **SWD / JTAG 均支持**：传输方式写在芯片定义里，Cortex-M 走 SWD、ESP32 走 JTAG
- **OpenOCD 版本管理**：自动发现本机已有的 OpenOCD，可在线下载新版本、一键切换、删除
- **失败原因提示**：把 OpenOCD 的常见报错翻译成可操作的排查建议
- **程序自升级**：可检查、下载新版本并自动替换重启

## 运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project src/RnFlashTool.App
```

## 打包

```bash
# Windows
dotnet publish src/RnFlashTool.App -c Release -r win-x64   --self-contained false
# Linux
dotnet publish src/RnFlashTool.App -c Release -r linux-x64 --self-contained false
# macOS (Apple Silicon)
dotnet publish src/RnFlashTool.App -c Release -r osx-arm64 --self-contained false
```

想让目标机器不装 .NET 也能跑，把 `--self-contained false` 换成 `--self-contained true`。

## OpenOCD

程序按以下顺序查找 OpenOCD，任选其一即可：

1. 自己下载安装的版本（`<LocalAppData>/RN_FlashTool/openocd/`）
2. 程序目录或其上级目录中的 `openocd/bin/openocd[.exe]`
3. 系统 `PATH`
4. 在界面上手动指定的路径

点击窗口右上角的 **管理…**（或底部状态栏的版本号）打开版本管理：可以查看本机已有版本、
从 [xPack OpenOCD](https://github.com/xpack-dev-tools/openocd-xpack) 拉取发行版列表、下载安装、
设为当前或删除。访问 GitHub 受限时，可在"下载选项"里填写加速前缀。

> Linux 下使用 USB 调试器通常还需要安装 udev 规则，并把当前用户加入相应用户组。

## 程序自升级

点击状态栏左下角的版本号（如 `v2.0.0`）打开更新窗口，可检查并安装新版本。

更新流程：下载对应平台的发行包 → 解压到暂存目录 → 启动一个外部脚本 → 程序退出 →
脚本用暂存目录覆盖程序目录并重新拉起程序。正在运行的可执行文件无法覆盖自己，所以必须绕这一圈。

以下两种情况不提供自动替换，只会给出「前往下载页」：

- 从构建输出目录运行（`dotnet run` 或 IDE 启动的开发版本）
- 程序目录不可写（例如安装在 `Program Files` 且未以管理员身份运行）

更新前请先结束正在进行的烧录操作。

## 添加自己的芯片

每个芯片一个 YAML 文件。内置定义在 `src/RnFlashTool.App/chips/`，
用户自定义放在 `<AppData>/RN_FlashTool/chips/`（**同 `id` 会覆盖内置定义**）。
界面上点"打开自定义芯片目录"可直接跳转到该目录，改完点"重新加载"即可生效。

```yaml
# 唯一标识，小写
id: stm32f103

# 下拉列表中显示的名称
name: STM32F103

# 所属系列，用于分组
series: STM32F1

# OpenOCD target 配置文件，相对于 OpenOCD 的 scripts 目录
targetConfig: target/stm32f1x.cfg

# 传输方式，留空按 swd 处理；ESP32 这类目标需要写 jtag
# transport: swd

# 手动输入完整型号（如 stm32f103vgt6）时用于匹配的前缀，按最长前缀命中
matchPrefixes:
  - stm32f103

# 默认烧录起始地址
flashAddress: "0x08000000"

# 可选：Flash 容量，设置后“读取固件”会用它作为默认读取大小
# flashSize: "0x20000"

# 可选：调试器速度（kHz），覆盖编程器接口的默认速度
# adapterSpeed: 4000

# 可选：追加到 OpenOCD 配置末尾的自定义命令
# extraConfigLines:
#   - "reset_config srst_only srst_nogate"

description: STM32F103（Cortex-M3）
```

## 支持的目标与编程器

内置芯片定义：

| 系列 | 数量 | 传输方式 |
| --- | --- | --- |
| STM32（F0/F1/F2/F3/F4/F7/H7/L0/L1/L4/G0/G4） | 28 | SWD |
| ESP32 / ESP32-S2 / ESP32-S3 | 3 | JTAG |

可选编程器：ST-Link、J-Link、CMSIS-DAP、ESP USB-JTAG（内置）、ESP-PROG（FTDI）。

选择的编程器若不支持目标芯片所需的传输方式，程序会在执行前直接提示，
而不是把晦涩的 OpenOCD 报错丢给你。

> ESP32 的芯片定义指向内置 OpenOCD 自带的 `target/esp32*.cfg`，
> 连接与识别部分可用；但 ESP32 的烧录命令与 Cortex-M 有差异，
> **这三个定义尚未在真机上验证过**，必要时请用 `extraConfigLines` 调整。

## 项目结构

```
RN_FlashTool.sln
src/RnFlashTool.App/
├── Models/       芯片定义、烧录参数、版本号比较等纯数据类型
├── Services/     芯片目录加载、OpenOCD 调用与版本管理、配置持久化、对话框
├── ViewModels/   MVVM 层，不依赖任何 UI 类型
├── Views/        Avalonia 窗口与转换器
├── Themes/       配色、字号与控件样式
└── chips/        内置芯片定义（YAML，每芯片一个文件）
```

## 数据存放位置

| 用途 | Windows | Linux / macOS |
| --- | --- | --- |
| 配置 | `%APPDATA%\RN_FlashTool\settings.json` | `~/.config/RN_FlashTool/settings.json` |
| 自定义芯片 | `%APPDATA%\RN_FlashTool\chips\` | `~/.config/RN_FlashTool/chips/` |
| 下载的 OpenOCD | `%LOCALAPPDATA%\RN_FlashTool\openocd\` | `~/.local/share/RN_FlashTool/openocd/` |

## 许可证

本项目以 [MIT License](LICENSE) 发布。

发行包内附带的 OpenOCD 为 xPack 构建，以 GPL-2.0-or-later 发布，
程序通过启动独立进程的方式调用它，不与其链接。
各组件的许可证与源码地址见 [第三方组件声明](THIRD-PARTY-NOTICES.md)。
