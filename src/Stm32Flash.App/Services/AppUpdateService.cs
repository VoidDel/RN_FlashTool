using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Stm32Flash.App.Models;

namespace Stm32Flash.App.Services;

/// <summary>
/// 从本仓库的 GitHub Releases 自升级。
/// 正在运行的可执行文件没法自己覆盖自己，所以实际替换动作交给一个外部脚本：
/// 它等本进程退出后把暂存目录覆盖到程序目录，再重新拉起程序。
/// </summary>
public sealed class AppUpdateService : IAppUpdateService
{
    private const string Repository = "VoidDel/STM32_FLASH";

    private readonly GitHubClient _github;

    public AppUpdateService(GitHubClient github)
    {
        _github = github;

        CurrentVersion = ResolveCurrentVersion();
        SelfUpdateBlockReason = DetectBlockReason();
    }

    public string CurrentVersion { get; }

    public bool CanSelfUpdate => SelfUpdateBlockReason.Length == 0;

    public string SelfUpdateBlockReason { get; }

    public string ReleasesPageUrl => $"https://github.com/{Repository}/releases";

    public async Task<AppRelease?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var json = await _github.GetReleasesJsonAsync(Repository, 10, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var suffix = GetAssetSuffix();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            if (element.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            {
                continue;
            }

            var tag = element.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() ?? "" : "";
            var version = LooseVersion.Parse(tag);
            if (version is null || !LooseVersion.IsNewer(version, CurrentVersion))
            {
                continue;
            }

            if (!element.TryGetProperty("assets", out var assets))
            {
                continue;
            }

            var asset = assets.EnumerateArray().FirstOrDefault(item =>
                item.TryGetProperty("name", out var name) &&
                name.GetString() is { } text &&
                text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            if (asset.ValueKind != JsonValueKind.Object)
            {
                // 该版本没有当前平台的安装包，继续往下找。
                continue;
            }

            return new AppRelease(
                Version: version,
                TagName: tag,
                AssetName: asset.GetProperty("name").GetString() ?? "",
                DownloadUrl: asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() ?? "" : "",
                SizeBytes: asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                PublishedAt: element.TryGetProperty("published_at", out var published) &&
                             published.TryGetDateTimeOffset(out var date)
                    ? date
                    : DateTimeOffset.MinValue,
                Notes: element.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
        }

        return null;
    }

    public async Task<string> PrepareAsync(
        AppRelease release,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var temp = AppPaths.EnsureDirectory(AppPaths.TempDirectory);
        var archivePath = Path.Combine(temp, release.AssetName);
        var staging = Path.Combine(temp, $"update-{release.Version}");

        try
        {
            progress.Report(new InstallProgress($"正在下载 {release.AssetName} …", 0));
            await _github
                .DownloadAsync(release.DownloadUrl, archivePath, release.SizeBytes, progress, cancellationToken)
                .ConfigureAwait(false);

            progress.Report(new InstallProgress("正在解压 …"));
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            await ArchiveExtractor.ExtractAsync(archivePath, staging, cancellationToken).ConfigureAwait(false);

            // 包里必须有可执行文件，否则覆盖过去会把程序弄坏。
            var executable = Path.Combine(staging, ExecutableName);
            if (!File.Exists(executable))
            {
                throw new InvalidOperationException($"安装包里没有找到 {ExecutableName}，已放弃升级。");
            }

            ArchiveExtractor.EnsureExecutable(executable);
            progress.Report(new InstallProgress($"{release.Version} 已就绪", 100));
            return staging;
        }
        catch (Exception)
        {
            TryDeleteDirectory(staging);
            throw;
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    public void LaunchUpdater(string stagingDirectory)
    {
        var appDirectory = Path.GetFullPath(AppPaths.AppDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var executable = Environment.ProcessPath ?? Path.Combine(appDirectory, ExecutableName);
        var scriptPath = Path.Combine(
            AppPaths.EnsureDirectory(AppPaths.TempDirectory),
            OperatingSystem.IsWindows() ? "update.ps1" : "update.sh");

        var script = OperatingSystem.IsWindows()
            ? BuildWindowsScript(Environment.ProcessId, stagingDirectory, appDirectory, executable)
            : BuildUnixScript(Environment.ProcessId, stagingDirectory, appDirectory, executable);

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ArchiveExtractor.EnsureExecutable(scriptPath);

        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath },
                UseShellExecute = false,
                CreateNoWindow = true,
            }
            : new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { scriptPath },
                UseShellExecute = false,
                CreateNoWindow = true,
            };

        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>
    /// 等旧进程退出后覆盖文件并重启。
    /// 脚本里有大量 PowerShell 的字面量花括号，所以用 $$ 原始字符串：
    /// 此时插值洞写作 {{ }}，单个 { } 原样输出。
    /// PowerShell 单引号字符串内只需把 ' 转义成 ''。
    /// </summary>
    private static string BuildWindowsScript(int processId, string staging, string appDirectory, string executable) =>
        $$"""
          $ErrorActionPreference = 'Stop'
          $targetPid = {{processId}}
          $staging   = '{{Escape(staging)}}'
          $appDir    = '{{Escape(appDirectory)}}'
          $exe       = '{{Escape(executable)}}'

          for ($i = 0; $i -lt 200; $i++) {
              if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }
              Start-Sleep -Milliseconds 300
          }

          Copy-Item -Path (Join-Path $staging '*') -Destination $appDir -Recurse -Force
          Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
          Start-Process -FilePath $exe -WorkingDirectory $appDir

          """;

    private static string BuildUnixScript(int processId, string staging, string appDirectory, string executable) =>
        $"""
         #!/bin/sh
         target_pid={processId}
         staging='{Escape(staging)}'
         app_dir='{Escape(appDirectory)}'
         exe='{Escape(executable)}'

         i=0
         while [ $i -lt 200 ] && kill -0 "$target_pid" 2>/dev/null; do
             sleep 0.3
             i=$((i + 1))
         done

         cp -Rf "$staging/." "$app_dir/"
         chmod +x "$exe" 2>/dev/null
         rm -rf "$staging"
         "$exe" &

         """;

    private static string Escape(string value) => value.Replace("'", "''");

    /// <summary>可执行文件名随平台而变。</summary>
    private static string ExecutableName => OperatingSystem.IsWindows() ? "Stm32Flash.exe" : "Stm32Flash";

    /// <summary>
    /// 发行包的命名规则见 .github/workflows/release.yml：
    /// <c>Stm32Flash-&lt;版本&gt;-&lt;RID&gt;.zip|tar.gz</c>。
    /// </summary>
    private static string GetAssetSuffix()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows())
        {
            return $"win-{architecture}.zip";
        }

        var platform = OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{platform}-{architecture}.tar.gz";
    }

    private static string ResolveCurrentVersion()
    {
        // 用本类所在程序集而不是 GetEntryAssembly()：后者在被别的宿主加载时会取到宿主的版本。
        var assembly = typeof(AppUpdateService).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return LooseVersion.Parse(informational)
               ?? assembly.GetName().Version?.ToString(3)
               ?? "0.0.0";
    }

    /// <summary>判断当前环境能否原地替换；不能的话界面上要退化成“打开发行页手动下载”。</summary>
    private static string DetectBlockReason()
    {
        var directory = Path.GetFullPath(AppPaths.AppDirectory);

        // 从 IDE 或 dotnet run 启动时目录是构建输出，覆盖它没有意义。
        var separator = Path.DirectorySeparatorChar;
        if (directory.Contains($"{separator}bin{separator}Debug{separator}", StringComparison.OrdinalIgnoreCase) ||
            directory.Contains($"{separator}bin{separator}Release{separator}", StringComparison.OrdinalIgnoreCase))
        {
            return "当前是从构建输出目录运行的开发版本，不支持自动升级。";
        }

        try
        {
            var probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception)
        {
            return $"程序目录不可写，无法就地升级：{directory}";
        }

        return string.Empty;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 清理失败不影响主流程。
        }
    }
}
