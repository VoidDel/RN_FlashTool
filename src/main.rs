//! RN FlashTool：基于 OpenOCD 的跨平台 MCU 烧录工具。
//!
//! Rust 侧负责一切与系统打交道的事（启动 openocd、下载安装、读写配置、文件对话框），
//! 界面在 `ui/` 下，用纯 HTML / CSS / JS 编写，通过 Tauri 命令调用这里。

// 发布版不要控制台窗口；调试版保留，println! 还能看到。
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app_update;
mod archive;
mod chips;
mod commands;
mod github;
mod model;
mod openocd;
mod paths;
mod settings;
mod version;

use tauri::Manager;

fn main() {
    // 必须在读取设置之前完成，否则会按新目录当作全新安装。
    paths::migrate_legacy_dirs();

    let app = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .manage(commands::AppState::new())
        .invoke_handler(tauri::generate_handler![
            commands::bootstrap,
            commands::reload_chips,
            commands::update_settings,
            commands::discover_openocd,
            commands::run_operation,
            commands::cancel_operation,
            commands::cancel_task,
            commands::check_openocd_update,
            commands::fetch_openocd_releases,
            commands::install_openocd,
            commands::uninstall_openocd,
            commands::custom_openocd,
            commands::check_app_update,
            commands::install_app_update,
            commands::pick_file,
            commands::pick_save_file,
            commands::write_text_file,
            commands::open_location,
        ])
        .build(tauri::generate_context!())
        .expect("初始化窗口失败");

    app.run(|handle, event| {
        if let tauri::RunEvent::Exit = event {
            handle.state::<commands::AppState>().cancel_all();
        }
    });
}
