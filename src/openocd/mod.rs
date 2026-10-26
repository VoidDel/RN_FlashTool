//! OpenOCD 相关：配置生成、进程驱动、本机发现与在线安装。

pub mod config;
pub mod install;
pub mod runner;

use std::ffi::OsStr;

/// openocd 可执行文件名随平台而变。
pub const EXECUTABLE_NAME: &str = if cfg!(windows) { "openocd.exe" } else { "openocd" };

/// 启动控制台程序且不弹出黑窗口。本程序是 GUI 子系统，Windows 下不加这个标志每次都会闪一个控制台。
pub fn hidden_command(program: impl AsRef<OsStr>) -> tokio::process::Command {
    #[allow(unused_mut)]
    let mut command = tokio::process::Command::new(program);
    #[cfg(windows)]
    {
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    command.kill_on_drop(true);
    command
}
