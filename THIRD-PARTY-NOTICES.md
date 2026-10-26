# 第三方组件声明

本项目自身以 [MIT License](LICENSE) 发布。以下组件由各自的许可证约束。

## 编译进可执行文件的依赖

主要的 Rust 依赖如下，完整清单见 `Cargo.lock`（可用 `cargo tree` 查看）。

| 组件 | 许可证 | 来源 |
| --- | --- | --- |
| Tauri（含 tauri-plugin-dialog / tauri-plugin-opener） | MIT 或 Apache-2.0 | <https://github.com/tauri-apps/tauri> |
| Tokio / tokio-util | MIT | <https://github.com/tokio-rs/tokio> |
| reqwest | MIT 或 Apache-2.0 | <https://github.com/seanmonstar/reqwest> |
| rustls | MIT、Apache-2.0 或 ISC | <https://github.com/rustls/rustls> |
| serde / serde_json | MIT 或 Apache-2.0 | <https://github.com/serde-rs/serde> |
| serde_yaml | MIT 或 Apache-2.0 | <https://github.com/dtolnay/serde-yaml> |
| regex | MIT 或 Apache-2.0 | <https://github.com/rust-lang/regex> |
| chrono | MIT 或 Apache-2.0 | <https://github.com/chronotope/chrono> |
| sha2 | MIT 或 Apache-2.0 | <https://github.com/RustCrypto/hashes> |
| zip | MIT | <https://github.com/zip-rs/zip2> |
| tar / flate2 | MIT 或 Apache-2.0 | <https://github.com/alexcrichton/tar-rs> |

界面运行在系统自带的 WebView 上（Windows 的 WebView2、macOS 的 WKWebView、Linux 的 WebKitGTK），
这些不随发行包分发。

## OpenOCD

仓库内的 `openocd/` 目录，以及程序在「OpenOCD 版本管理」中下载安装的版本，
为 [xPack OpenOCD](https://github.com/xpack-dev-tools/openocd-xpack) 或
[Espressif openocd-esp32](https://github.com/espressif/openocd-esp32) 构建，
均以 **GPL-2.0-or-later** 发布。

- 完整的许可证文本位于 `openocd/distro-info/licenses/`，涵盖 OpenOCD 本身
  及其依赖（libusb、libftdi、hidapi、libiconv 等）
- OpenOCD 上游源码：<https://sourceforge.net/p/openocd/code/>
- xPack 打包脚本与源码：<https://github.com/xpack-dev-tools/openocd-xpack>
- Espressif 分支源码：<https://github.com/espressif/openocd-esp32>

### 与本项目的关系

本程序通过**启动独立进程**的方式调用 `openocd`，不与其链接、不嵌入其代码。
因此 OpenOCD 的 GPL 不影响本项目源码的许可证选择，两者在发行包中属于聚合关系
（mere aggregation）。

需要注意的是，**再分发这些二进制文件时仍需遵守 GPL-2.0 的相应义务**
（保留许可证文本、提供或指明对应源码）。上面给出的源码地址即为此目的。
如果你打算以其它形式重新分发本项目的发行包，请自行确认是否满足这些条件。
