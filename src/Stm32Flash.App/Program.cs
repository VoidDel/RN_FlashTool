using Avalonia;

namespace Stm32Flash.App;

internal static class Program
{
    /// <summary>
    /// 入口点。初始化 Avalonia 之前不要使用任何 Avalonia 类型、也不要碰可视化树。
    /// </summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>可视化设计器也会调用这个方法，签名不能改。</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
