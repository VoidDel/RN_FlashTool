using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace RnFlashTool.App.Views;

/// <summary>消息框的类型，决定图标与配色。</summary>
public enum MessageDialogKind
{
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>
/// Avalonia 没有内置 MessageBox，这里自己做一个，
/// 免得为了一个弹窗再引入第三方依赖。
/// </summary>
public partial class MessageDialog : Window
{
    private bool _result;

    public MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>显示一个消息框；<paramref name="confirm"/> 为真时带“取消”按钮并返回用户选择。</summary>
    public static async Task<bool> ShowAsync(
        Window? owner,
        string title,
        string message,
        MessageDialogKind kind,
        bool confirm)
    {
        var dialog = new MessageDialog();
        dialog.Apply(title, message, kind, confirm);

        if (owner is null)
        {
            // 没有父窗口时退化成普通窗口，至少不会丢失消息。
            dialog.Show();
            return false;
        }

        return await dialog.ShowDialog<bool>(owner);
    }

    private void Apply(string title, string message, MessageDialogKind kind, bool confirm)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.IsVisible = confirm;
        OkButton.Content = confirm ? "确定" : "知道了";

        var (glyph, foreground, background) = kind switch
        {
            MessageDialogKind.Warning => ("!", "#B45309", "#FEF3C7"),
            MessageDialogKind.Error => ("×", "#B91C1C", "#FEE2E2"),
            MessageDialogKind.Question => ("?", "#1D4ED8", "#DBEAFE"),
            _ => ("i", "#1D4ED8", "#DBEAFE"),
        };

        IconGlyph.Text = glyph;
        IconGlyph.Foreground = SolidColorBrush.Parse(foreground);
        IconBadge.Background = SolidColorBrush.Parse(background);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close(_result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = false;
        Close(_result);
    }
}
