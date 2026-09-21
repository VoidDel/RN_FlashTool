using System.Globalization;
using Avalonia.Data.Converters;
using RnFlashTool.App.Models;

namespace RnFlashTool.App.Views;

/// <summary>把 <see cref="FirmwareFormat"/> 显示成 ELF / BIN。</summary>
public sealed class FirmwareFormatConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FirmwareFormat format ? format.ToDisplayName() : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && string.Equals(text, "BIN", StringComparison.OrdinalIgnoreCase)
            ? FirmwareFormat.Bin
            : FirmwareFormat.Elf;
}
