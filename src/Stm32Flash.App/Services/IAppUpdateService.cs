using Stm32Flash.App.Models;

namespace Stm32Flash.App.Services;

/// <summary>程序自身的升级：检查、下载、替换并重启。</summary>
public interface IAppUpdateService
{
    /// <summary>当前运行的版本号。</summary>
    string CurrentVersion { get; }

    /// <summary>当前环境是否支持自动替换；为 <c>false</c> 时只能手动下载。</summary>
    bool CanSelfUpdate { get; }

    /// <summary>不支持自动替换的原因，<see cref="CanSelfUpdate"/> 为 <c>true</c> 时是空串。</summary>
    string SelfUpdateBlockReason { get; }

    /// <summary>发行页地址，供手动下载。</summary>
    string ReleasesPageUrl { get; }

    /// <summary>查询最新版本；没有比当前更新的版本时返回 <c>null</c>。</summary>
    Task<AppRelease?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载并解压到暂存目录，返回暂存目录路径。
    /// 此时尚未改动程序目录，随时可以放弃。
    /// </summary>
    Task<string> PrepareAsync(
        AppRelease release,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动外部脚本：等本进程退出后用暂存目录覆盖程序目录，再重新拉起程序。
    /// 调用方负责在此之后立即退出。
    /// </summary>
    void LaunchUpdater(string stagingDirectory);
}
