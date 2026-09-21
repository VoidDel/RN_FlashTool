using Stm32Flash.App.Models;

namespace Stm32Flash.App.Services;

public interface IOpenOcdRunner
{
    /// <summary>执行一次 OpenOCD 操作，过程中通过 <paramref name="log"/> 实时回报输出。</summary>
    /// <param name="operation">要执行的操作。</param>
    /// <param name="options">本次操作的参数快照。</param>
    /// <param name="outputPath">读取固件时的保存路径，其它操作传 <c>null</c>。</param>
    /// <param name="log">逐行输出回调，由调用方负责切回 UI 线程。</param>
    /// <param name="cancellationToken">用于中止操作。</param>
    Task<OperationResult> ExecuteAsync(
        FlashOperation operation,
        FlashOptions options,
        string? outputPath,
        IProgress<string> log,
        CancellationToken cancellationToken);
}
