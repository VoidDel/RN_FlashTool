using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Stm32Flash.App.Services;
using Stm32Flash.App.ViewModels;
using Stm32Flash.App.Views;

namespace Stm32Flash.App;

/// <summary>应用入口，同时充当组合根：在这里把各个服务装配起来。</summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new SettingsService();
            var dialogs = new DialogService();
            var catalog = new ChipCatalogService();
            var github = new GitHubClient(settings);
            var versions = new OpenOcdVersionService(settings, github);
            var appUpdates = new AppUpdateService(github);
            var runner = new OpenOcdRunner();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(
                    runner,
                    versions,
                    appUpdates,
                    catalog,
                    settings,
                    dialogs,
                    // 自升级需要先退出本进程，外部脚本才能覆盖程序目录
                    requestShutdown: () => desktop.Shutdown()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
