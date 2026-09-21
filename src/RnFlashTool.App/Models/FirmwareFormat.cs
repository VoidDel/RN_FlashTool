namespace RnFlashTool.App.Models;

/// <summary>固件文件格式。</summary>
public enum FirmwareFormat
{
    /// <summary>ELF：地址信息包含在文件里，无需显式指定烧录地址。</summary>
    Elf,

    /// <summary>BIN：裸二进制，必须指定烧录地址。</summary>
    Bin,
}

public static class FirmwareFormatExtensions
{
    public static string ToOpenOcdToken(this FirmwareFormat format) =>
        format == FirmwareFormat.Bin ? "bin" : "elf";

    public static string ToDisplayName(this FirmwareFormat format) =>
        format == FirmwareFormat.Bin ? "BIN" : "ELF";

    /// <summary>根据文件扩展名推断格式，无法判断时返回 <c>null</c>。</summary>
    public static FirmwareFormat? FromFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bin" => FirmwareFormat.Bin,
            ".elf" or ".axf" or ".out" => FirmwareFormat.Elf,
            _ => null,
        };
    }
}
