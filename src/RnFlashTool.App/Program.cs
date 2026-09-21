using Avalonia;
using Avalonia.Threading;

namespace RnFlashTool.App;

internal static class Program
{
    /// <summary>
    /// 入口点。初始化 Avalonia 之前不要使用任何 Avalonia 类型、也不要碰可视化树。
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // 界面里有不少 async void 的事件处理（OnOpened 之类），
        // 其中逸出的异常默认会直接终止进程。兜住它们，弹窗告知而不是闪退。
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        try
        {
            var window = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            _ = Views.MessageDialog.ShowAsync(
                window,
                "发生未处理的异常",
                $"{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                Views.MessageDialogKind.Error,
                confirm: false);
        }
        catch (Exception)
        {
            // 连弹窗都失败就只能放弃了，但至少进程还活着。
        }
    }

    /// <summary>可视化设计器也会调用这个方法，签名不能改。</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
