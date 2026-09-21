using RnFlashTool.App.Models;

namespace RnFlashTool.App.Services;

/// <summary>安装过程中的进度回报。</summary>
/// <param name="Stage">当前阶段的描述文字。</param>
/// <param name="Percent">0–100 的百分比；无法计算时为 <c>null</c>。</param>
public sealed record InstallProgress(string Stage, double? Percent = null);

/// <summary>OpenOCD 的多版本管理：发现、下载、切换、卸载。</summary>
public interface IOpenOcdVersionService
{
    /// <summary>下载安装的版本所在的根目录。</summary>
    string InstallRoot { get; }

    /// <summary>扫描内置、已下载、系统 PATH 中的全部 OpenOCD，并读取各自版本号。</summary>
    Task<IReadOnlyList<OpenOcdInstallation>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>读取指定可执行文件的版本号，失败时返回 <c>null</c>。</summary>
    Task<string?> QueryVersionAsync(string executablePath, CancellationToken cancellationToken = default);

    /// <summary>从 xPack 发行页拉取可下载的版本列表，按版本号从新到旧排序。</summary>
    Task<IReadOnlyList<OpenOcdRelease>> FetchReleasesAsync(CancellationToken cancellationToken = default);

    /// <summary>下载并安装一个版本，返回安装好的条目。</summary>
    Task<OpenOcdInstallation> InstallAsync(
        OpenOcdRelease release,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken = default);

    /// <summary>删除一个已下载的版本。</summary>
    void Uninstall(OpenOcdInstallation installation);

    /// <summary>在远端版本中找出比 <paramref name="current"/> 更新的最新版本；没有则返回 <c>null</c>。</summary>
    Task<OpenOcdRelease?> CheckForUpdateAsync(string? current, CancellationToken cancellationToken = default);

    /// <summary>把用户手动指定的可执行文件包装成一个安装条目。</summary>
    Task<OpenOcdInstallation?> CreateCustomAsync(string executablePath, CancellationToken cancellationToken = default);
}
