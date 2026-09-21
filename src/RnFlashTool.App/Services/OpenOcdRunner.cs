using System.Diagnostics;
using System.Text;
using RnFlashTool.App.Models;

namespace RnFlashTool.App.Services;

/// <summary>
/// 生成临时 OpenOCD 配置并驱动 openocd 进程完成烧录/验证/读取/复位。
/// 相比旧的 Python 实现，这里是流式输出而不是等进程结束再一次性回显，并且支持随时取消。
/// </summary>
public sealed class OpenOcdRunner : IOpenOcdRunner
{
    public async Task<OperationResult> ExecuteAsync(
        FlashOperation operation,
        FlashOptions options,
        string? outputPath,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var banner = operation.ToDisplayName();

        var validation = Validate(operation, options, outputPath);
        if (validation is not null)
        {
            return OperationResult.Fail($"{banner}失败", validation);
        }

        var commands = BuildCommands(operation, options, outputPath);
        return await RunAsync(commands, banner, options, log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>操作前的前置检查，返回错误说明；通过检查时返回 <c>null</c>。</summary>
    private static string? Validate(FlashOperation operation, FlashOptions options, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(options.OpenOcdPath) || !File.Exists(options.OpenOcdPath))
        {
            return "未找到 openocd 可执行文件。请在“OpenOCD 版本”中下载或指定一个可用版本。";
        }

        // 例如拿只支持 SWD 的 ST-Link 去接 ESP32（jtag），与其让 OpenOCD 报一串晦涩错误，不如先说清楚。
        if (!options.Interface.Supports(options.Transport))
        {
            return $"编程器“{options.Interface.DisplayName}”不支持 {options.Transport} 传输方式，" +
                   $"而 {options.Chip.DisplayName} 需要 {options.Transport}。请改选其它编程器。";
        }

        if (operation is FlashOperation.Flash or FlashOperation.Verify)
        {
            if (string.IsNullOrWhiteSpace(options.FirmwarePath))
            {
                return "固件路径为空";
            }

            if (!File.Exists(options.FirmwarePath))
            {
                return $"固件文件不存在: {options.FirmwarePath}";
            }

            if (!options.Format.CarriesAddress() && string.IsNullOrWhiteSpace(options.Address))
            {
                return "BIN 格式必须指定烧录地址";
            }
        }

        if (operation == FlashOperation.Read && string.IsNullOrWhiteSpace(outputPath))
        {
            return "未指定保存路径";
        }

        return null;
    }

    /// <summary>按操作类型拼出要执行的 OpenOCD 命令。</summary>
    private static string BuildCommands(FlashOperation operation, FlashOptions options, string? outputPath)
    {
        switch (operation)
        {
            case FlashOperation.Flash:
            {
                var firmware = ToOpenOcdPath(options.FirmwarePath);
                var commands = new List<string> { "reset halt" };
                if (options.FullErase)
                {
                    commands.Add("flash erase_sector 0 0 last");
                }

                // ELF / HEX / S19 不写 type，OpenOCD 会按文件内容自己认
                // （日志里的 "IHEX image detected." 就是它打的）；只有裸二进制要补地址。
                commands.Add(options.Format.CarriesAddress()
                    ? $"flash write_image erase \"{firmware}\""
                    : $"flash write_image erase \"{firmware}\" {options.Address} bin");
                return string.Join("\n", commands);
            }

            case FlashOperation.Verify:
            {
                var firmware = ToOpenOcdPath(options.FirmwarePath);
                return options.Format.CarriesAddress()
                    ? $"verify_image \"{firmware}\""
                    : $"verify_image \"{firmware}\" {options.Address} bin";
            }

            case FlashOperation.Read:
            {
                var target = ToOpenOcdPath(outputPath!);
                var size = string.IsNullOrWhiteSpace(options.ReadSize)
                    ? FlashOptions.DefaultReadSize
                    : options.ReadSize.Trim();
                return $"dump_image \"{target}\" {options.Address} {size}";
            }

            case FlashOperation.Reset:
                return "reset run";

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "未知操作");
        }
    }

    private static async Task<OperationResult> RunAsync(
        string commands,
        string banner,
        FlashOptions options,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var header = options.BuildConfigHeader();
        if (!header.EndsWith('\n'))
        {
            header += "\n";
        }

        var configText = header + commands + "\nshutdown\n";

        string configPath;
        try
        {
            AppPaths.EnsureDirectory(AppPaths.TempDirectory);
            configPath = Path.Combine(AppPaths.TempDirectory, $"openocd-{Guid.NewGuid():N}.cfg");
            await File.WriteAllTextAsync(configPath, configText, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return OperationResult.Fail($"{banner}失败", $"无法写入临时配置文件: {exception.Message}");
        }

        log.Report($"[{banner}] 配置文件: {configPath}");
        log.Report("--- OpenOCD 配置内容 ---");
        log.Report(configText.TrimEnd());
        log.Report("--- 配置结束 ---");

        var startInfo = new ProcessStartInfo
        {
            FileName = options.OpenOcdPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(options.OpenOcdPath) ?? AppPaths.AppDirectory,
        };

        if (!string.IsNullOrWhiteSpace(options.ScriptsDirectory) && Directory.Exists(options.ScriptsDirectory))
        {
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(options.ScriptsDirectory);
        }

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(configPath);

        var output = new StringBuilder();
        var timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            void OnOutput(object sender, DataReceivedEventArgs args)
            {
                if (args.Data is null)
                {
                    return;
                }

                lock (output)
                {
                    output.AppendLine(args.Data);
                }

                log.Report(args.Data);
            }

            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;

            log.Report($"--- 运行: {startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)} ---");

            if (!process.Start())
            {
                return OperationResult.Fail($"{banner}失败", "无法启动 openocd 进程");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);

                return timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? OperationResult.Fail(
                        $"{banner}超时",
                        $"OpenOCD 在 {timeout.TotalSeconds:F0} 秒内没有结束，已强制终止。可以调大“超时”，或检查硬件连接。")
                    : OperationResult.Fail($"{banner}已取消", "操作被用户取消。");
            }

            string text;
            lock (output)
            {
                text = output.ToString();
            }

            log.Report($"--- OpenOCD 退出码: {process.ExitCode} ---");

            return process.ExitCode == 0
                ? OperationResult.Ok($"{banner}成功")
                : OperationResult.Fail($"{banner}失败", AnalyzeFailure(text, process.ExitCode));
        }
        catch (Exception exception)
        {
            return OperationResult.Fail($"{banner}失败", $"运行 openocd 出错: {exception.Message}");
        }
        finally
        {
            TryDelete(configPath);
        }
    }

    /// <summary>把 OpenOCD 的常见报错翻译成可操作的排查建议。</summary>
    private static string AnalyzeFailure(string output, int exitCode)
    {
        var lowered = output.ToLowerInvariant();

        var hint = string.Empty;
        if (lowered.Contains("cannot read idr"))
        {
            hint = "连接问题：\n• 检查调试器与目标板的 SWD 接线（SWDIO / SWCLK / GND）\n• 确认目标芯片已上电\n• 尝试降低调试速度（在芯片 YAML 里设置 adapterSpeed）";
        }
        else if (lowered.Contains("unknown chip") || lowered.Contains("unable to identify target"))
        {
            hint = "芯片识别失败：\n• 确认选择的目标芯片型号正确\n• 芯片可能处于读保护/锁定状态\n• 尝试接上 NRST 复位引脚";
        }
        else if (lowered.Contains("no device found") || lowered.Contains("unable to open"))
        {
            hint = "找不到调试器：\n• 确认调试器已插好并被系统识别\n• 检查“编程器”选择是否与实际硬件一致\n• Windows 下可能需要用 Zadig 安装 WinUSB 驱动";
        }
        else if (lowered.Contains("libusb_open failed") || lowered.Contains("open failed"))
        {
            hint = "打开调试器失败：\n• 设备可能被其它程序占用（如 ST-Link Utility、Keil）\n• 关闭占用程序后重试";
        }
        else if (lowered.Contains("can't find") || lowered.Contains("no such file"))
        {
            hint = "找不到配置文件：\n• 检查芯片 YAML 中的 targetConfig 在当前 OpenOCD 版本里是否存在\n• 或切换到另一个 OpenOCD 版本";
        }
        else if (lowered.Contains("timed out") || lowered.Contains("timeout"))
        {
            hint = "通信超时：\n• 检查硬件连接与供电\n• 尝试降低调试速度";
        }

        var summary = $"OpenOCD 以退出码 {exitCode} 结束，详见日志。";
        return string.IsNullOrEmpty(hint) ? summary : $"{summary}\n\n{hint}";
    }

    /// <summary>OpenOCD 的 Tcl 配置里统一用正斜杠，避免 Windows 反斜杠被当成转义符。</summary>
    private static string ToOpenOcdPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能刚好自己退出了，忽略。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响功能。
        }
    }
}
