using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RnFlashTool.App.Models;

namespace RnFlashTool.App.Services;

/// <summary>
/// 基于 xPack 发行版实现 OpenOCD 的自动升级与多版本切换。
/// 选择 xPack 的原因：它就是项目里内置的那个构建，目录结构一致（bin/openocd.exe + openocd/scripts），
/// 并且提供官方 Windows 压缩包，解压即用、不需要安装程序。
/// </summary>
public sealed class OpenOcdVersionService : IOpenOcdVersionService
{
    private const string Repository = "xpack-dev-tools/openocd-xpack";

    /// <summary>可执行文件名随平台而变。</summary>
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "openocd.exe" : "openocd";

    private readonly ISettingsService _settings;
    private readonly GitHubClient _github;

    public OpenOcdVersionService(ISettingsService settings, GitHubClient github)
    {
        _settings = settings;
        _github = github;
    }

    public string InstallRoot => AppPaths.OpenOcdInstallRoot;

    public async Task<IReadOnlyList<OpenOcdInstallation>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var found = new List<OpenOcdInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, OpenOcdSource source, string? installDirectory = null)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                found.Add(new OpenOcdInstallation(full, source, null, installDirectory));
            }
        }

        // 1) 自己下载安装的版本
        if (Directory.Exists(InstallRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(InstallRoot))
            {
                var executable = FindExecutable(directory);
                if (executable is not null)
                {
                    Add(executable, OpenOcdSource.Managed, directory);
                }
            }
        }

        // 2) 随程序分发的 openocd 目录：程序目录自身及其若干级父目录（兼容 bin/Debug/... 的调试布局）
        foreach (var root in EnumerateAppRoots())
        {
            Add(Path.Combine(root, "openocd", "bin", ExecutableName), OpenOcdSource.Bundled);
        }

        // 3) 系统 PATH
        foreach (var path in EnumeratePathCandidates())
        {
            Add(path, OpenOcdSource.SystemPath);
        }

        // 4) 用户手动指定的路径
        var custom = _settings.Current.SelectedOpenOcdPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
        {
            Add(custom, OpenOcdSource.Custom);
        }

        // 并发读取版本号，避免逐个启动进程拖慢界面。
        var withVersions = await Task.WhenAll(found.Select(async installation =>
        {
            // 自己下载安装的版本用安装目录名（即发行版本号，如 0.12.0-7）。
            // openocd --version 报的是上游版本（如 0.12.0+dev），区分不出 xPack 的打包修订，
            // 会让下载下来的新版本和内置版本在列表里长得一模一样。
            if (installation is { Source: OpenOcdSource.Managed, InstallDirectory: { } directory })
            {
                return installation with { Version = Path.GetFileName(directory) };
            }

            var version = await QueryVersionAsync(installation.ExecutablePath, cancellationToken).ConfigureAwait(false);
            return installation with { Version = version };
        })).ConfigureAwait(false);

        return [.. withVersions
            .OrderBy(item => item.Source)
            .ThenByDescending(item => item.Version, Comparer<string?>.Create(LooseVersion.Compare))];
    }

    public async Task<string?> QueryVersionAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            // 两个流要同时读，否则其中一个写满缓冲区就会卡住。
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            // openocd 把版本信息写在 stderr 上，两边都看一下。
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return LooseVersion.ParseFromVersionOutput(string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<OpenOcdRelease>> FetchReleasesAsync(CancellationToken cancellationToken = default)
    {
        var json = await _github.GetReleasesJsonAsync(Repository, 30, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var suffix = GetAssetSuffix();
        var releases = new List<OpenOcdRelease>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
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
                continue;
            }

            var tag = element.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() ?? "" : "";
            var version = LooseVersion.Parse(tag) ?? tag;
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            releases.Add(new OpenOcdRelease(
                Version: version,
                TagName: tag,
                AssetName: asset.GetProperty("name").GetString() ?? "",
                DownloadUrl: asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() ?? "" : "",
                SizeBytes: asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                PublishedAt: element.TryGetProperty("published_at", out var published) &&
                             published.TryGetDateTimeOffset(out var date)
                    ? date
                    : DateTimeOffset.MinValue,
                IsPrerelease: element.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()));
        }

        return [.. releases
            .Where(release => !string.IsNullOrWhiteSpace(release.DownloadUrl))
            .OrderByDescending(release => release.Version, Comparer<string>.Create(LooseVersion.Compare))];
    }

    public async Task<OpenOcdInstallation> InstallAsync(
        OpenOcdRelease release,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureDirectory(InstallRoot);

        var targetDirectory = Path.Combine(InstallRoot, SanitizeFolderName(release.Version));
        var archivePath = Path.Combine(AppPaths.EnsureDirectory(AppPaths.TempDirectory), release.AssetName);

        try
        {
            progress.Report(new InstallProgress($"正在下载 {release.AssetName} …", 0));
            await _github.DownloadAsync(release.DownloadUrl, archivePath, release.SizeBytes, progress, cancellationToken)
                .ConfigureAwait(false);

            progress.Report(new InstallProgress("正在解压 …", null));
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }

            Directory.CreateDirectory(targetDirectory);
            await ArchiveExtractor.ExtractAsync(archivePath, targetDirectory, cancellationToken).ConfigureAwait(false);

            var executable = FindExecutable(targetDirectory)
                ?? throw new InvalidOperationException($"压缩包里没有找到 {ExecutableName}");

            ArchiveExtractor.EnsureExecutable(executable);

            progress.Report(new InstallProgress($"已安装 {release.Version}", 100));

            return new OpenOcdInstallation(executable, OpenOcdSource.Managed, release.Version, targetDirectory);
        }
        catch (Exception)
        {
            // 安装失败时不要留下半个目录，否则下次会被当成可用版本。
            TryDeleteDirectory(targetDirectory);
            throw;
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    public void Uninstall(OpenOcdInstallation installation)
    {
        if (!installation.CanUninstall || string.IsNullOrEmpty(installation.InstallDirectory))
        {
            throw new InvalidOperationException("只能删除通过本工具下载安装的版本。");
        }

        Directory.Delete(installation.InstallDirectory, recursive: true);
    }

    public async Task<OpenOcdRelease?> CheckForUpdateAsync(string? current, CancellationToken cancellationToken = default)
    {
        var releases = await FetchReleasesAsync(cancellationToken).ConfigureAwait(false);
        var latest = releases.FirstOrDefault(release => !release.IsPrerelease) ?? releases.FirstOrDefault();

        if (latest is null)
        {
            return null;
        }

        return LooseVersion.IsUpgrade(latest.Version, current, LooseVersion.IsDevBuild(current)) ? latest : null;
    }

    public async Task<OpenOcdInstallation?> CreateCustomAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            return null;
        }

        var full = Path.GetFullPath(executablePath);
        var version = await QueryVersionAsync(full, cancellationToken).ConfigureAwait(false);
        return new OpenOcdInstallation(full, OpenOcdSource.Custom, version);
    }

    /// <summary>在目录里按常见布局查找 openocd 可执行文件。</summary>
    private static string? FindExecutable(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var direct = Path.Combine(directory, "bin", ExecutableName);
        if (File.Exists(direct))
        {
            return direct;
        }

        // xPack 压缩包解压后会多一层 xpack-openocd-<version>/ 目录。
        return Directory
            .EnumerateFiles(directory, ExecutableName, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
    }

    /// <summary>程序目录及其父目录，用于在开发时也能找到仓库根部的 openocd/。</summary>
    private static IEnumerable<string> EnumerateAppRoots()
    {
        var directory = new DirectoryInfo(AppPaths.AppDirectory);
        for (var depth = 0; depth < 6 && directory is not null; depth++)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private static IEnumerable<string> EnumeratePathCandidates()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(entry.Trim().Trim('"'), ExecutableName);
            }
            catch (ArgumentException)
            {
                continue; // PATH 里有非法字符的项，跳过。
            }

            yield return candidate;
        }
    }

    /// <summary>
    /// 当前系统对应的 xPack 安装包文件名后缀。
    /// xPack 的命名规则是 <c>&lt;平台&gt;-&lt;架构&gt;.&lt;扩展名&gt;</c>，Windows 用 zip，Linux/macOS 用 tar.gz。
    /// </summary>
    private static string GetAssetSuffix()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "ia32",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows())
        {
            return $"win32-{architecture}.zip";
        }

        var platform = OperatingSystem.IsMacOS() ? "darwin" : "linux";
        return $"{platform}-{architecture}.tar.gz";
    }

    private static string SanitizeFolderName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

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
