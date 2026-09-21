using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RnFlashTool.App.Services;

namespace RnFlashTool.App.Views;

/// <summary>
/// “读取固件 / 验证固件”点下去之后弹出的地址输入框。
/// 这两个操作的地址不常改，放在主界面上只是白占高度，所以改成用时再问。
/// </summary>
public partial class MemoryRangeDialog : Window
{
    private bool _askSize;

    public MemoryRangeDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 显示输入框；<paramref name="readSize"/> 为 <c>null</c> 时只问起始地址（验证用不到长度）。
    /// 用户取消时返回 <c>null</c>。
    /// </summary>
    public static async Task<MemoryRangeInput?> ShowAsync(
        Window? owner,
        string title,
        string message,
        string address,
        string? readSize)
    {
        var dialog = new MemoryRangeDialog();
        dialog.Apply(title, message, address, readSize);

        if (owner is null)
        {
            // 没有父窗口时拿不到模态结果，宁可不做操作也不要拿着默认值去动芯片。
            return null;
        }

        return await dialog.ShowDialog<MemoryRangeInput?>(owner);
    }

    private void Apply(string title, string message, string address, string? readSize)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrWhiteSpace(message);

        AddressBox.Text = address;

        _askSize = readSize is not null;
        SizeLabel.IsVisible = _askSize;
        SizeBox.IsVisible = _askSize;
        SizeBox.Text = readSize ?? string.Empty;

        // 打开就能直接改地址，省一次点击。
        Opened += (_, _) =>
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        };
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text?.Trim() ?? string.Empty;
        if (!TryParseNumber(address, out _))
        {
            Fail("起始地址无法识别，请填十六进制（如 0x08000000）或十进制数字。");
            return;
        }

        var size = SizeBox.Text?.Trim() ?? string.Empty;
        if (_askSize)
        {
            if (!TryParseNumber(size, out var bytes) || bytes == 0)
            {
                Fail("读取大小无法识别，请填大于 0 的十六进制（如 0x10000）或十进制数字。");
                return;
            }
        }

        Close(new MemoryRangeInput(address, size));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    /// <summary>接受 <c>0x</c> 前缀的十六进制与普通十进制，跟 OpenOCD 自己的写法保持一致。</summary>
    private static bool TryParseNumber(string text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
