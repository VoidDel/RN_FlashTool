# 第三方组件声明

本项目自身以 [MIT License](LICENSE) 发布。以下组件由各自的许可证约束。

## 运行时依赖（随发行包一起分发）

| 组件 | 许可证 | 来源 |
| --- | --- | --- |
| Avalonia | MIT | <https://github.com/AvaloniaUI/Avalonia> |
| YamlDotNet | MIT | <https://github.com/aaubry/YamlDotNet> |
| .NET Runtime | MIT | <https://github.com/dotnet/runtime> |
| Inter 字体 | SIL Open Font License 1.1 | <https://github.com/rsms/inter> |

## OpenOCD

仓库内的 `openocd/` 目录，以及程序在「OpenOCD 版本管理」中下载安装的版本，
均为 [xPack OpenOCD](https://github.com/xpack-dev-tools/openocd-xpack) 构建，
以 **GPL-2.0-or-later** 发布。

- 完整的许可证文本位于 `openocd/distro-info/licenses/`，涵盖 OpenOCD 本身
  及其依赖（libusb、libftdi、hidapi、libiconv 等）
- OpenOCD 上游源码：<https://sourceforge.net/p/openocd/code/>
- xPack 打包脚本与源码：<https://github.com/xpack-dev-tools/openocd-xpack>

### 与本项目的关系

本程序通过**启动独立进程**的方式调用 `openocd`，不与其链接、不嵌入其代码。
因此 OpenOCD 的 GPL 不影响本项目源码的许可证选择，两者在发行包中属于聚合关系
（mere aggregation）。

需要注意的是，**再分发这些二进制文件时仍需遵守 GPL-2.0 的相应义务**
（保留许可证文本、提供或指明对应源码）。上面给出的源码地址即为此目的。
如果你打算以其它形式重新分发本项目的发行包，请自行确认是否满足这些条件。
