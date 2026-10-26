//! 前端可调用的命令。这一层只做参数转换与状态管理，具体逻辑都在各个模块里。

use std::path::Path;
use std::sync::Mutex;

use chrono::{Local, TimeDelta};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::ipc::Channel;
use tauri::{AppHandle, State, WebviewWindow};
use tauri_plugin_dialog::DialogExt;
use tauri_plugin_opener::OpenerExt;
use tokio_util::sync::CancellationToken;

use crate::app_update::{self, AppRelease};
use crate::chips::{ChipCatalog, ChipDefinition};
use crate::github::GitHub;
use crate::model::{
    DEFAULT_CHIP_ID, FirmwareFormat, FlashOperation, INTERFACES, OperationResult, ProgrammerInterface, Progress,
    ResetMode,
};
use crate::openocd::config::FlashOptions;
use crate::openocd::install::{self, Installation, Provider, Release};
use crate::openocd::runner::{self, RunEvent};
use crate::paths;
use crate::settings::{Settings, SettingsStore};

pub struct AppState {
    pub settings: SettingsStore,
    pub catalog: Mutex<ChipCatalog>,
    pub github: GitHub,
    /// 正在进行的烧录类操作。
    pub operation: Mutex<Option<CancellationToken>>,
    /// 正在进行的下载类任务（OpenOCD 安装、程序更新）。
    pub task: Mutex<Option<CancellationToken>>,
}

impl AppState {
    pub fn new() -> Self {
        Self {
            settings: SettingsStore::load(),
            catalog: Mutex::new(ChipCatalog::load()),
            github: GitHub::new(app_update::current_version()),
            operation: Mutex::new(None),
            task: Mutex::new(None),
        }
    }

    /// 程序退出时中止一切进行中的工作，openocd 进程随之被杀掉。
    pub fn cancel_all(&self) {
        for slot in [&self.operation, &self.task] {
            if let Some(token) = lock(slot).as_ref() {
                token.cancel();
            }
        }
    }

    /// `preferred` 是想选中的芯片：可以是 id、名称或完整型号（按最长前缀解析）。
    fn chip_list(&self, preferred: &str) -> ChipList {
        let catalog = lock(&self.catalog);
        let selected = catalog
            .resolve(preferred)
            .or_else(|| catalog.resolve(DEFAULT_CHIP_ID))
            .or(catalog.chips.first())
            .map(|chip| chip.id.clone());

        ChipList {
            chips: catalog.chips.clone(),
            errors: catalog.errors.clone(),
            selected,
            builtin_dir: paths::display(&catalog.builtin_dir),
            user_dir: paths::display(&catalog.user_dir),
        }
    }
}

fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// 占住一个任务槽，结束时自动归还；同一时间只允许一个同类任务。
struct Slot<'a> {
    slot: &'a Mutex<Option<CancellationToken>>,
    token: CancellationToken,
}

impl<'a> Slot<'a> {
    fn acquire(slot: &'a Mutex<Option<CancellationToken>>) -> Result<Self, String> {
        let mut guard = lock(slot);
        if guard.is_some() {
            return Err("已有操作在进行，请等待完成或先取消。".into());
        }
        let token = CancellationToken::new();
        *guard = Some(token.clone());
        Ok(Self { slot, token })
    }
}

impl Drop for Slot<'_> {
    fn drop(&mut self) {
        *lock(self.slot) = None;
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ChipList {
    chips: Vec<ChipDefinition>,
    errors: Vec<String>,
    /// 应当选中的芯片 id；列表为空时为 `None`。
    selected: Option<String>,
    builtin_dir: String,
    user_dir: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Bootstrap {
    version: &'static str,
    settings: Settings,
    interfaces: &'static [ProgrammerInterface],
    chips: ChipList,
    /// 不能就地升级的原因；为空表示可以。
    self_update_block_reason: Option<String>,
}

#[tauri::command]
pub fn bootstrap(state: State<'_, AppState>) -> Bootstrap {
    let settings = state.settings.get();
    Bootstrap {
        version: app_update::current_version(),
        chips: state.chip_list(&settings.target_chip),
        settings,
        interfaces: &INTERFACES,
        self_update_block_reason: app_update::block_reason(),
    }
}

/// 重新扫描芯片目录；对象换了一批，按 `current` 找回原来的选择。
#[tauri::command]
pub fn reload_chips(state: State<'_, AppState>, current: String) -> ChipList {
    *lock(&state.catalog) = ChipCatalog::load();
    state.chip_list(&current)
}

/// 前端只传改动过的字段。
#[tauri::command]
pub fn update_settings(state: State<'_, AppState>, patch: Value) -> Result<Settings, String> {
    state.settings.patch(patch)
}

#[tauri::command]
pub async fn discover_openocd(state: State<'_, AppState>) -> Result<Vec<Installation>, String> {
    let custom = state.settings.get().selected_open_ocd_path;
    Ok(install::discover(&custom).await)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OperationRequest {
    operation: FlashOperation,
    open_ocd_path: String,
    chip_id: String,
    interface_id: String,
    reset_mode: ResetMode,
    firmware_path: String,
    format: FirmwareFormat,
    address: String,
    read_size: String,
    full_erase: bool,
    verify_after_flash: bool,
    run_after_flash: bool,
    timeout_seconds: u32,
    output_path: Option<String>,
}

/// 执行一次 OpenOCD 操作。日志与结论都从 `on_event` 推送，最后一条一定是 `done`。
#[tauri::command]
pub async fn run_operation(
    state: State<'_, AppState>,
    request: OperationRequest,
    on_event: Channel<RunEvent>,
) -> Result<(), String> {
    let send = |event: RunEvent| {
        let _ = on_event.send(event);
    };

    let chip = lock(&state.catalog).chips.iter().find(|c| c.id.eq_ignore_ascii_case(&request.chip_id)).cloned();
    let Some(chip) = chip else {
        send(RunEvent::Done {
            result: OperationResult::fail("无效的目标芯片", format!("芯片列表里没有 {}", request.chip_id)),
        });
        return Ok(());
    };

    let slot = Slot::acquire(&state.operation)?;
    let openocd = std::path::PathBuf::from(&request.open_ocd_path);

    let options = FlashOptions {
        scripts_dir: install::resolve_scripts_dir(&openocd),
        openocd_path: openocd,
        chip,
        interface: ProgrammerInterface::from_id(&request.interface_id),
        reset_mode: request.reset_mode,
        // 扩展名认得出来就听扩展名的，认不出来才用下拉框里选的。
        format: FirmwareFormat::from_file_name(&request.firmware_path).unwrap_or(request.format),
        firmware_path: request.firmware_path,
        address: request.address.trim().to_string(),
        read_size: request.read_size.trim().to_string(),
        full_erase: request.full_erase,
        verify_after_flash: request.verify_after_flash,
        run_after_flash: request.run_after_flash,
        timeout_seconds: request.timeout_seconds.clamp(5, 3600),
    };

    let result = runner::execute(request.operation, &options, request.output_path.as_deref(), &send, &slot.token).await;
    send(RunEvent::Done { result });
    Ok(())
}

#[tauri::command]
pub fn cancel_operation(state: State<'_, AppState>) {
    if let Some(token) = lock(&state.operation).as_ref() {
        token.cancel();
    }
}

#[tauri::command]
pub fn cancel_task(state: State<'_, AppState>) {
    if let Some(token) = lock(&state.task).as_ref() {
        token.cancel();
    }
}

/// 每天最多自动检查一次；`force` 为真时（用户手动点的）不受限制。
fn is_due(enabled: bool, last: Option<chrono::DateTime<chrono::FixedOffset>>, force: bool) -> bool {
    force || (enabled && last.is_none_or(|last| Local::now().fixed_offset() - last >= TimeDelta::days(1)))
}

#[tauri::command]
pub async fn check_openocd_update(
    state: State<'_, AppState>,
    current: Option<String>,
    force: bool,
) -> Result<Option<Release>, String> {
    let settings = state.settings.get();
    if !is_due(settings.auto_check_open_ocd_update, settings.last_update_check, force) {
        return Ok(None);
    }

    let update = install::check_for_update(&state.github, current.as_deref()).await?;
    state.settings.update(|s| s.last_update_check = Some(Local::now().fixed_offset()));
    Ok(update)
}

#[tauri::command]
pub async fn fetch_openocd_releases(state: State<'_, AppState>, provider: Provider) -> Result<Vec<Release>, String> {
    let releases = install::fetch_releases(&state.github, provider).await?;
    if provider == Provider::Xpack {
        state.settings.update(|s| s.last_update_check = Some(Local::now().fixed_offset()));
    }
    Ok(releases)
}

#[tauri::command]
pub async fn install_openocd(
    state: State<'_, AppState>,
    release: Release,
    on_progress: Channel<Progress>,
) -> Result<Installation, String> {
    let slot = Slot::acquire(&state.task)?;
    let mirror = state.settings.get().download_mirror;
    let report = |progress: Progress| {
        let _ = on_progress.send(progress);
    };

    let installed = install::install(&state.github, &release, &mirror, &report, &slot.token).await?;
    state.settings.update(|s| s.selected_open_ocd_path = installed.executable_path.clone());
    Ok(installed)
}

#[tauri::command]
pub fn uninstall_openocd(
    state: State<'_, AppState>,
    install_directory: String,
    executable_path: String,
) -> Result<(), String> {
    install::uninstall(&install_directory)?;

    // 删掉的正好是当前使用的版本时，清空选择，让前端退回列表里的第一个。
    state.settings.update(|s| {
        if s.selected_open_ocd_path.eq_ignore_ascii_case(&executable_path) {
            s.selected_open_ocd_path.clear();
        }
    });
    Ok(())
}

#[tauri::command]
pub async fn custom_openocd(path: String) -> Option<Installation> {
    install::create_custom(&path).await
}

#[tauri::command]
pub async fn check_app_update(state: State<'_, AppState>, force: bool) -> Result<Option<AppRelease>, String> {
    let settings = state.settings.get();
    if !is_due(settings.auto_check_app_update, settings.last_app_update_check, force) {
        return Ok(None);
    }

    let release = app_update::check(&state.github).await?;
    state.settings.update(|s| s.last_app_update_check = Some(Local::now().fixed_offset()));
    Ok(release)
}

/// 下载、校验、解压，然后启动替换脚本并退出程序。
#[tauri::command]
pub async fn install_app_update(
    app: AppHandle,
    state: State<'_, AppState>,
    release: AppRelease,
    on_progress: Channel<Progress>,
) -> Result<(), String> {
    if let Some(reason) = app_update::block_reason() {
        return Err(reason);
    }

    let staging = {
        let slot = Slot::acquire(&state.task)?;
        let mirror = state.settings.get().download_mirror;
        let report = |progress: Progress| {
            let _ = on_progress.send(progress);
        };
        app_update::prepare(&state.github, &release, &mirror, &report, &slot.token).await?
    };

    let _ = on_progress.send(Progress::new("正在重启以完成更新 …", None));
    app_update::launch_updater(&staging)?;

    // 外部脚本正等着本进程退出，这里必须真的退出，否则更新不会发生。
    state.cancel_all();
    app.exit(0);
    Ok(())
}

#[derive(Deserialize, Clone, Copy)]
#[serde(rename_all = "camelCase")]
pub enum FileKind {
    Firmware,
    OpenOcd,
    Dump,
    Log,
}

/// 打开文件对话框，返回本地路径；用户取消时返回 `None`。
#[tauri::command]
pub async fn pick_file(
    app: AppHandle,
    window: WebviewWindow,
    kind: FileKind,
    initial: Option<String>,
) -> Option<String> {
    let mut dialog = app.dialog().file().set_parent(&window);

    dialog = match kind {
        FileKind::OpenOcd => {
            let dialog = dialog.set_title("选择 openocd 可执行文件");
            // 其它平台的可执行文件没有固定扩展名，不加筛选。
            if cfg!(windows) { dialog.add_filter("可执行文件", &["exe"]) } else { dialog }
        }
        _ => with_filters(
            dialog.set_title("选择固件文件"),
            "固件文件",
            &["elf", "axf", "out", "hex", "ihex", "s19", "s28", "s37", "srec", "mot", "bin"],
        ),
    };

    // 从上次选择的目录继续，省得每次都从头找。
    if let Some(dir) = initial.as_deref().and_then(|p| Path::new(p).parent()).filter(|d| d.is_dir()) {
        dialog = dialog.set_directory(dir);
    }

    let (sender, receiver) = tokio::sync::oneshot::channel();
    dialog.pick_file(move |path| {
        let _ = sender.send(path);
    });
    receiver.await.ok().flatten()?.into_path().ok().map(|p| paths::display(&p))
}

#[tauri::command]
pub async fn pick_save_file(
    app: AppHandle,
    window: WebviewWindow,
    kind: FileKind,
    suggested_name: String,
) -> Option<String> {
    let dialog = app.dialog().file().set_parent(&window).set_file_name(&suggested_name);
    let dialog = match kind {
        FileKind::Log => with_filters(dialog.set_title("保存操作日志"), "日志文件", &["log", "txt"]),
        _ => with_filters(dialog.set_title("保存读取的固件"), "BIN 文件", &["bin"]),
    };

    let (sender, receiver) = tokio::sync::oneshot::channel();
    dialog.save_file(move |path| {
        let _ = sender.send(path);
    });
    receiver.await.ok().flatten()?.into_path().ok().map(|p| paths::display(&p))
}

/// macOS 的对话框不认 “*” 这种通配，加了反而什么都选不了，所以那里只给具体扩展名。
fn with_filters<R: tauri::Runtime>(
    dialog: tauri_plugin_dialog::FileDialogBuilder<R>,
    name: &str,
    extensions: &[&str],
) -> tauri_plugin_dialog::FileDialogBuilder<R> {
    let dialog = dialog.add_filter(name, extensions);
    if cfg!(target_os = "macos") { dialog } else { dialog.add_filter("所有文件", &["*"]) }
}

#[tauri::command]
pub fn write_text_file(path: String, contents: String) -> Result<(), String> {
    std::fs::write(&path, contents).map_err(|e| format!("写入 {path} 失败: {e}"))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Location {
    UserChips,
    OpenOcdInstallRoot,
    ReleasesPage,
}

#[tauri::command]
pub fn open_location(app: AppHandle, target: Location) -> Result<(), String> {
    let opener = app.opener();
    let directory = match target {
        Location::ReleasesPage => {
            return opener.open_url(app_update::RELEASES_PAGE, None::<&str>).map_err(|e| e.to_string());
        }
        Location::UserChips => paths::user_chips_dir(),
        Location::OpenOcdInstallRoot => paths::openocd_install_root(),
    };

    paths::ensure_dir(&directory)?;
    opener
        .open_path(paths::display(&directory), None::<&str>)
        .map_err(|e| format!("打开目录失败: {}\n\n{e}", directory.display()))
}
