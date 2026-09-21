namespace Stm32Flash.App.Models;

/// <summary>一次 OpenOCD 操作的结果。</summary>
/// <param name="Success">OpenOCD 是否以 0 退出。</param>
/// <param name="Summary">用于状态栏/弹窗的一句话结论。</param>
/// <param name="Detail">补充说明，例如失败原因分析，可能为空。</param>
public sealed record OperationResult(bool Success, string Summary, string Detail = "")
{
    public static OperationResult Ok(string summary, string detail = "") => new(true, summary, detail);

    public static OperationResult Fail(string summary, string detail = "") => new(false, summary, detail);
}
