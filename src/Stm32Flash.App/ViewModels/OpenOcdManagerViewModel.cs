using System.Collections.ObjectModel;
using Avalonia.Threading;
using Stm32Flash.App.Models;
using Stm32Flash.App.Services;

namespace Stm32Flash.App.ViewModels;

/// <summary>“OpenOCD 版本管理”窗口的 ViewModel：列出已安装版本、拉取可下载版本、安装/切换/删除。</summary>
public sealed class OpenOcdManagerViewModel : ObservableObject
{
    private readonly IOpenOcdVersionService _versions;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly Action<OpenOcdInstallation> _onActivated;

    private CancellationTokenSource? _cancellation;
    private OpenOcdInstallation? _selectedInstallation;
    private OpenOcdRelease? _selectedRelease;
    private string _activePath;
    private string _statusMessage = "就绪";
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private bool _isBusy;
    private string _downloadMirror;
    private bool _autoCheckUpdate;

    public OpenOcdManagerViewModel(
        IOpenOcdVersionService versions,
        ISettingsService settings,
        IDialogService dialogs,
        OpenOcdInstallation? active,
        Action<OpenOcdInstallation> onActivated)
    {
        _versions = versions;
        _settings = settings;
        _dialogs = dialogs;
        _onActivated = onActivated;
        _activePath = active?.ExecutablePath ?? string.Empty;
        _downloadMirror = settings.Current.DownloadMirror;
        _autoCheckUpdate = settings.Current.AutoCheckOpenOcdUpdate;

        RefreshCommand = new AsyncRelayCommand(RefreshInstalledAsync, () => !IsBusy, ReportError);
        FetchReleasesCommand = new AsyncRelayCommand(FetchReleasesAsync, () => !IsBusy, ReportError);
        InstallCommand = new AsyncRelayCommand(InstallSelectedAsync, () => !IsBusy && SelectedRelease is not null, ReportError);
        ActivateCommand = new RelayCommand(ActivateSelected, () => !IsBusy && SelectedInstallation is not null);
        UninstallCommand = new AsyncRelayCommand(UninstallSelectedAsync, () => !IsBusy && SelectedInstallation?.CanUninstall == true, ReportError);
        BrowseCommand = new AsyncRelayCommand(BrowseCustomAsync, () => !IsBusy, ReportError);
        OpenInstallFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_versions.InstallRoot));
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
    }

    public ObservableCollection<OpenOcdInstallation> Installations { get; } = [];

    public ObservableCollection<OpenOcdRelease> Releases { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand FetchReleasesCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    public RelayCommand ActivateCommand { get; }

    public AsyncRelayCommand UninstallCommand { get; }

    public AsyncRelayCommand BrowseCommand { get; }

    public RelayCommand OpenInstallFolderCommand { get; }

    public RelayCommand CancelCommand { get; }

    public OpenOcdInstallation? SelectedInstallation
    {
        get => _selectedInstallation;
        set
        {
            if (SetProperty(ref _selectedInstallation, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public OpenOcdRelease? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (SetProperty(ref _selectedRelease, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>当前生效的 openocd 路径，用于在列表里高亮。</summary>
    public string ActivePath
    {
        get => _activePath;
        private set => SetProperty(ref _activePath, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RaiseCommandStates();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>下载加速前缀，留空表示直连 GitHub。</summary>
    public string DownloadMirror
    {
        get => _downloadMirror;
        set
        {
            if (SetProperty(ref _downloadMirror, value))
            {
                _settings.Current.DownloadMirror = value?.Trim() ?? string.Empty;
                _settings.Save();
            }
        }
    }

    public bool AutoCheckUpdate
    {
        get => _autoCheckUpdate;
        set
        {
            if (SetProperty(ref _autoCheckUpdate, value))
            {
                _settings.Current.AutoCheckOpenOcdUpdate = value;
                _settings.Save();
            }
        }
    }

    /// <summary>窗口打开后调用：先列出已安装版本，再尝试拉取远端列表（失败不影响使用）。</summary>
    public async Task InitializeAsync()
    {
        await RefreshInstalledAsync().ConfigureAwait(true);

        try
        {
            await FetchReleasesAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = $"获取在线版本失败：{exception.Message}";
        }
    }

    private Task RefreshInstalledAsync() =>
        RunBusyAsync("正在扫描本机 OpenOCD …", RefreshInstalledCoreAsync);

    private Task FetchReleasesAsync() =>
        RunBusyAsync("正在获取在线版本列表 …", FetchReleasesCoreAsync);

    private async Task RefreshInstalledCoreAsync(CancellationToken token)
    {
        var found = await _versions.DiscoverAsync(token).ConfigureAwait(true);

        Installations.Clear();
        foreach (var installation in found)
        {
            Installations.Add(installation);
        }

        var active = Installations.FirstOrDefault(item =>
                         string.Equals(item.ExecutablePath, ActivePath, StringComparison.OrdinalIgnoreCase))
                     ?? Installations.FirstOrDefault();

        // 列表是先 Clear 再 Add 的，ListBox 会在集合变更过程中把选中项重置掉，
        // 所以放到消息队列末尾再设一次，保证高亮停在当前使用的版本上。
        SelectedInstallation = active;
        Dispatcher.UIThread.Post(() => SelectedInstallation = active, DispatcherPriority.Background);

        StatusMessage = Installations.Count > 0
            ? $"找到 {Installations.Count} 个 OpenOCD"
            : "没有找到任何 OpenOCD，可在下方下载一个版本";
    }

    private async Task FetchReleasesCoreAsync(CancellationToken token)
    {
        var releases = await _versions.FetchReleasesAsync(token).ConfigureAwait(true);

        Releases.Clear();
        foreach (var release in releases)
        {
            Releases.Add(release);
        }

        SelectedRelease = Releases.FirstOrDefault(release => !release.IsPrerelease) ?? Releases.FirstOrDefault();

        var newest = Releases.FirstOrDefault()?.Version;
        var current = SelectedInstallation?.Version;
        StatusMessage = OpenOcdVersion.IsUpgrade(newest, current, OpenOcdVersion.IsDevBuild(current))
            ? $"有新版本可用：{newest}（当前 {current ?? "未知"}）"
            : $"在线版本 {Releases.Count} 个，当前已是较新版本";

        _settings.Current.LastUpdateCheck = DateTimeOffset.Now;
        _settings.Save();
    }

    private async Task InstallSelectedAsync()
    {
        if (SelectedRelease is not { } release)
        {
            return;
        }

        await RunBusyAsync($"正在安装 {release.Version} …", async token =>
        {
            var progress = new Progress<InstallProgress>(report =>
            {
                StatusMessage = report.Stage;
                IsProgressIndeterminate = report.Percent is null;
                ProgressValue = report.Percent ?? 0;
            });

            var installed = await _versions.InstallAsync(release, progress, token).ConfigureAwait(true);

            // 重新扫描以带上新装版本，注意这里要调内部方法，避免嵌套进入忙碌状态。
            await RefreshInstalledCoreAsync(token).ConfigureAwait(true);

            SelectedInstallation = Installations.FirstOrDefault(item =>
                string.Equals(item.ExecutablePath, installed.ExecutablePath, StringComparison.OrdinalIgnoreCase));

            ActivateSelected();
            StatusMessage = $"已安装并切换到 {installed.Version ?? release.Version}";
        }).ConfigureAwait(true);
    }

    private void ActivateSelected()
    {
        if (SelectedInstallation is not { } installation)
        {
            return;
        }

        ActivePath = installation.ExecutablePath;
        _settings.Current.SelectedOpenOcdPath = installation.ExecutablePath;
        _settings.Save();
        _onActivated(installation);
        StatusMessage = $"当前使用 {installation.DisplayName}";
    }

    private async Task UninstallSelectedAsync()
    {
        if (SelectedInstallation is not { CanUninstall: true } installation)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("删除版本", $"确定要删除 {installation.DisplayName} 吗？\n\n{installation.InstallDirectory}"))
        {
            return;
        }

        try
        {
            _versions.Uninstall(installation);
            Installations.Remove(installation);
            SelectedInstallation = Installations.FirstOrDefault();
            StatusMessage = "已删除";

            // 删掉的正好是当前使用的版本时，退回到列表里的第一个。
            if (string.Equals(ActivePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                ActivePath = string.Empty;
                _settings.Current.SelectedOpenOcdPath = string.Empty;
                _settings.Save();
                ActivateSelected();
            }
        }
        catch (Exception exception)
        {
            await _dialogs.ErrorAsync("删除失败", exception.Message).ConfigureAwait(true);
        }
    }

    private async Task BrowseCustomAsync()
    {
        var path = await _dialogs.PickOpenOcdExecutableAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var installation = await _versions.CreateCustomAsync(path).ConfigureAwait(true);
        if (installation is null)
        {
            await _dialogs.WarnAsync("无效的路径", "指定的文件不存在。").ConfigureAwait(true);
            return;
        }

        var existing = Installations.FirstOrDefault(item =>
            string.Equals(item.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            Installations.Add(installation);
            existing = installation;
        }

        SelectedInstallation = existing;
        ActivateSelected();
    }

    /// <summary>统一处理忙碌状态、取消令牌和进度条复位。</summary>
    private async Task RunBusyAsync(string startMessage, Func<CancellationToken, Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        StatusMessage = startMessage;

        try
        {
            await work(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
        }
    }

    private void ReportError(Exception exception)
    {
        StatusMessage = $"操作失败：{exception.Message}";

        // 错误回调是同步的，这里不阻塞，交给消息框自己弹。
        _ = _dialogs.ErrorAsync("OpenOCD 版本管理", exception.Message);
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        FetchReleasesCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        ActivateCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
