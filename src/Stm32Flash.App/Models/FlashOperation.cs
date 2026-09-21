namespace Stm32Flash.App.Models;

/// <summary>可以对目标芯片执行的操作。</summary>
public enum FlashOperation
{
    Flash,
    Verify,
    Read,
    Reset,
}

public static class FlashOperationExtensions
{
    public static string ToDisplayName(this FlashOperation operation) => operation switch
    {
        FlashOperation.Flash => "烧录",
        FlashOperation.Verify => "验证",
        FlashOperation.Read => "读取",
        FlashOperation.Reset => "复位",
        _ => operation.ToString(),
    };
}
