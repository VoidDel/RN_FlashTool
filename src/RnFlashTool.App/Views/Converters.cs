using System.Globalization;
using Avalonia.Data.Converters;
using RnFlashTool.App.Models;

namespace RnFlashTool.App.Views;

/// <summary>把 <see cref="FirmwareFormat"/> 显示成 ELF / BIN / Intel HEX / Motorola S19。</summary>
public sealed class FirmwareFormatConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FirmwareFormat format ? format.ToDisplayName() : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text)
        {
            return FirmwareFormat.Elf;
        }

        // 下拉框绑的是枚举本身，这里只是兜底，按显示名反查。
        foreach (var format in Enum.GetValues<FirmwareFormat>())
        {
            if (string.Equals(format.ToDisplayName(), text, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return FirmwareFormat.Elf;
    }
}
