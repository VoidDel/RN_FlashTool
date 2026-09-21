namespace RnFlashTool.App.Models;

/// <summary>
/// 固件文件格式。除 <see cref="Bin"/> 外都把地址写在文件里，
/// 界面上不再让用户选，一律按扩展名判断。
/// </summary>
public enum FirmwareFormat
{
    /// <summary>ELF：地址信息包含在文件里，无需显式指定烧录地址。</summary>
    Elf,

    /// <summary>BIN：裸二进制，必须指定烧录地址。</summary>
    Bin,

    /// <summary>Intel HEX：每条记录自带地址。</summary>
    Ihex,

    /// <summary>Motorola S-record：每条记录自带地址。</summary>
    S19,
}

public static class FirmwareFormatExtensions
{
    public static string ToDisplayName(this FirmwareFormat format) => format switch
    {
        FirmwareFormat.Bin => "BIN",
        FirmwareFormat.Ihex => "Intel HEX",
        FirmwareFormat.S19 => "Motorola S19",
        _ => "ELF",
    };

    /// <summary>该格式是否把地址写在文件里，烧录 / 验证时不用再问。</summary>
    public static bool CarriesAddress(this FirmwareFormat format) => format != FirmwareFormat.Bin;

    /// <summary>
    /// 根据文件扩展名推断格式，认不出来时返回 <c>null</c>。
    /// 这些扩展名都是 OpenOCD <c>image_open</c> 认的那几类。
    /// </summary>
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
            ".hex" or ".ihex" => FirmwareFormat.Ihex,
            ".s19" or ".s28" or ".s37" or ".srec" or ".mot" => FirmwareFormat.S19,
            _ => null,
        };
    }
}
