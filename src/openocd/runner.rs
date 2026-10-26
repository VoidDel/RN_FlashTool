//! 生成临时 cfg、启动 openocd、流式回报输出，支持取消与超时。

use std::process::Stdio;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use serde::Serialize;
use tokio::io::{AsyncBufReadExt, AsyncRead, BufReader};
use tokio_util::sync::CancellationToken;

use super::config::{FlashOptions, analyze_failure};
use super::hidden_command;
use crate::model::{FlashOperation, OperationResult};
use crate::paths;

/// 推给前端的消息。日志行与最终结论走同一条 Channel，保证结论一定排在最后一行日志之后。
#[derive(Debug, Clone, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum RunEvent {
    Line { text: String },
    Done { result: OperationResult },
}

pub type Emit<'a> = &'a (dyn Fn(RunEvent) + Send + Sync);

enum Outcome {
    Exited(std::io::Result<std::process::ExitStatus>),
    Cancelled,
    TimedOut,
}

pub async fn execute(
    operation: FlashOperation,
    options: &FlashOptions,
    output_path: Option<&str>,
    emit: Emit<'_>,
    cancel: &CancellationToken,
) -> OperationResult {
    let banner = operation.display_name();

    if let Err(problem) = options.validate(operation, output_path) {
        return OperationResult::fail(format!("{banner}失败"), problem);
    }

    let commands = options.build_commands(operation, output_path);
    let config_text = format!("{}\n{commands}\nshutdown\n", options.build_config_header());

    let config_path = match write_temp_config(&config_text) {
        Ok(path) => path,
        Err(problem) => return OperationResult::fail(format!("{banner}失败"), problem),
    };

    let line = |text: String| emit(RunEvent::Line { text });
    line(format!("[{banner}] 配置文件: {}", config_path.display()));
    line("--- OpenOCD 配置内容 ---".into());
    config_text.trim_end().lines().for_each(|l| line(l.to_string()));
    line("--- 配置结束 ---".into());

    let result = run_process(banner, options, &config_path, emit, cancel).await;
    let _ = std::fs::remove_file(&config_path); // 临时文件删不掉不影响功能
    result
}

async fn run_process(
    banner: &str,
    options: &FlashOptions,
    config_path: &std::path::Path,
    emit: Emit<'_>,
    cancel: &CancellationToken,
) -> OperationResult {
    let mut command = hidden_command(&options.openocd_path);
    if let Some(scripts) = options.scripts_dir.as_ref().filter(|dir| dir.is_dir()) {
        command.arg("-s").arg(scripts);
    }
    command.arg("-f").arg(config_path);
    if let Some(dir) = options.openocd_path.parent() {
        command.current_dir(dir);
    }
    command.stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());

    let invocation = command.as_std();
    let args: Vec<_> = invocation.get_args().map(|arg| arg.to_string_lossy()).collect();
    emit(RunEvent::Line {
        text: format!("--- 运行: {} {} ---", invocation.get_program().to_string_lossy(), args.join(" ")),
    });

    let mut child = match command.spawn() {
        Ok(child) => child,
        Err(error) => return OperationResult::fail(format!("{banner}失败"), format!("无法启动 openocd 进程: {error}")),
    };

    let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
    let output = Mutex::new(String::new());
    let timeout = Duration::from_secs(u64::from(options.timeout_seconds.max(5)));

    let outcome = tokio::select! {
        status = async {
            // 两个流要同时读完，否则其中一个写满缓冲区就会卡住；读到 EOF 再等进程退出。
            tokio::join!(pump(stdout, &output, emit), pump(stderr, &output, emit));
            child.wait().await
        } => Outcome::Exited(status),
        _ = cancel.cancelled() => Outcome::Cancelled,
        _ = tokio::time::sleep(timeout) => Outcome::TimedOut,
    };

    let status = match outcome {
        Outcome::Exited(Ok(status)) => status,
        Outcome::Exited(Err(error)) => {
            return OperationResult::fail(format!("{banner}失败"), format!("运行 openocd 出错: {error}"));
        }
        Outcome::Cancelled => {
            let _ = child.kill().await;
            return OperationResult::fail(format!("{banner}已取消"), "操作被用户取消。");
        }
        Outcome::TimedOut => {
            let _ = child.kill().await;
            return OperationResult::fail(
                format!("{banner}超时"),
                format!("OpenOCD 在 {} 秒内没有结束，已强制终止。可以调大“超时”，或检查硬件连接。", timeout.as_secs()),
            );
        }
    };

    let exit_code = status.code().unwrap_or(-1);
    emit(RunEvent::Line { text: format!("--- OpenOCD 退出码: {exit_code} ---") });

    if status.success() {
        OperationResult::ok(format!("{banner}成功"))
    } else {
        let text = output.lock().map(|o| o.clone()).unwrap_or_default();
        OperationResult::fail(format!("{banner}失败"), analyze_failure(&text, exit_code))
    }
}

/// 逐行读取输出：既推给前端，也攒下来供失败分析。
async fn pump(stream: Option<impl AsyncRead + Unpin>, output: &Mutex<String>, emit: Emit<'_>) {
    let Some(stream) = stream else { return };
    let mut reader = BufReader::new(stream);
    let mut buffer = Vec::new();

    loop {
        buffer.clear();
        match reader.read_until(b'\n', &mut buffer).await {
            Ok(0) | Err(_) => break,
            Ok(_) => {
                let text = String::from_utf8_lossy(&buffer).trim_end_matches(['\r', '\n']).to_string();
                if let Ok(mut all) = output.lock() {
                    all.push_str(&text);
                    all.push('\n');
                }
                emit(RunEvent::Line { text });
            }
        }
    }
}

fn write_temp_config(text: &str) -> Result<std::path::PathBuf, String> {
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    let dir = paths::ensure_dir(&paths::temp_dir())?;
    let path = dir.join(format!("openocd-{}-{}.cfg", std::process::id(), COUNTER.fetch_add(1, Ordering::Relaxed)));
    std::fs::write(&path, text).map_err(|e| format!("无法写入临时配置文件: {e}"))?;
    Ok(path)
}
