# STM32 固件烧录工具

跨平台的 STM32 烧录上位机，基于 Avalonia (.NET 10)，底层调用 OpenOCD。
Windows / Linux / macOS 共用同一套代码。

## 功能

- **烧录 / 验证 / 读取 / 复位**：支持 ELF 与 BIN，可设置起始地址、读取长度、烧录前全片擦除
- **实时日志**：OpenOCD 的输出逐行回显，不必等进程结束；操作可随时取消，也可设置超时
- **芯片列表可配置**：受支持的芯片由 YAML 文件定义，增删芯片不需要改代码、不需要重新编译
- **OpenOCD 版本管理**：自动发现本机已有的 OpenOCD，可在线下载新版本、一键切换、删除
- **失败原因提示**：把 OpenOCD 的常见报错翻译成可操作的排查建议

## 运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project src/Stm32Flash.App
```

## 打包

```bash
# Windows
dotnet publish src/Stm32Flash.App -c Release -r win-x64   --self-contained false
# Linux
dotnet publish src/Stm32Flash.App -c Release -r linux-x64 --self-contained false
# macOS (Apple Silicon)
dotnet publish src/Stm32Flash.App -c Release -r osx-arm64 --self-contained false
```

想让目标机器不装 .NET 也能跑，把 `--self-contained false` 换成 `--self-contained true`。

## OpenOCD

程序按以下顺序查找 OpenOCD，任选其一即可：

1. 自己下载安装的版本（`<LocalAppData>/Stm32FlashTool/openocd/`）
2. 程序目录或其上级目录中的 `openocd/bin/openocd[.exe]`
3. 系统 `PATH`
4. 在界面上手动指定的路径

点击窗口右上角的 **管理…**（或底部状态栏的版本号）打开版本管理：可以查看本机已有版本、
从 [xPack OpenOCD](https://github.com/xpack-dev-tools/openocd-xpack) 拉取发行版列表、下载安装、
设为当前或删除。访问 GitHub 受限时，可在"下载选项"里填写加速前缀。

> Linux 下使用 USB 调试器通常还需要安装 udev 规则，并把当前用户加入相应用户组。

## 添加自己的芯片

每个芯片一个 YAML 文件。内置定义在 `src/Stm32Flash.App/chips/`，
用户自定义放在 `<AppData>/Stm32FlashTool/chips/`（**同 `id` 会覆盖内置定义**）。
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

## 项目结构

```
Stm32Flash.sln
src/Stm32Flash.App/
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
| 配置 | `%APPDATA%\Stm32FlashTool\settings.json` | `~/.config/Stm32FlashTool/settings.json` |
| 自定义芯片 | `%APPDATA%\Stm32FlashTool\chips\` | `~/.config/Stm32FlashTool/chips/` |
| 下载的 OpenOCD | `%LOCALAPPDATA%\Stm32FlashTool\openocd\` | `~/.local/share/Stm32FlashTool/openocd/` |
