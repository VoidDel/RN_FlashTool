using RnFlashTool.App.Models;
using RnFlashTool.App.Services;

namespace RnFlashTool.App.ViewModels;

/// <summary>“程序更新”窗口的 ViewModel：检查、下载、替换并重启。</summary>
public sealed class AppUpdateViewModel : ObservableObject
{
    private readonly IAppUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;

    /// <summary>触发程序退出，由宿主传入；替换文件必须在进程退出之后才能进行。</summary>
    private readonly Action _requestShutdown;

    private CancellationTokenSource? _cancellation;
    private AppRelease? _available;
    private string _statusMessage = string.Empty;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private bool _isBusy;
    private bool _autoCheck;

    public AppUpdateViewModel(
        IAppUpdateService updates,
        ISettingsService settings,
        IDialogService dialogs,
        AppRelease? known,
        Action requestShutdown)
    {
        _updates = updates;
        _settings = settings;
        _dialogs = dialogs;
        _requestShutdown = requestShutdown;
        _available = known;
        _autoCheck = settings.Current.AutoCheckAppUpdate;
        _statusMessage = known is null ? "尚未检查" : $"发现新版本 {known.Version}";

        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy, ReportError);
        InstallCommand = new AsyncRelayCommand(
            InstallAsync,
            () => !IsBusy && _available is not null && _updates.CanSelfUpdate,
            ReportError);
        OpenReleasesPageCommand = new RelayCommand(() => _dialogs.OpenUrl(_updates.ReleasesPageUrl));
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
    }

    public AsyncRelayCommand CheckCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    public RelayCommand OpenReleasesPageCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string CurrentVersion => _updates.CurrentVersion;

    public string LatestVersion => _available?.Version ?? "—";

    public string ReleaseSizeText => _available?.SizeText ?? string.Empty;

    public string ReleaseDateText => _available is { } release && release.PublishedAt != DateTimeOffset.MinValue
        ? release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd")
        : string.Empty;

    public string ReleaseNotes => string.IsNullOrWhiteSpace(_available?.Notes)
        ? "（该版本没有提供发行说明）"
        : _available!.Notes.Trim();

    public bool HasUpdate => _available is not null;

    /// <summary>开发版运行或程序目录不可写时为 <c>false</c>，此时只提供手动下载。</summary>
    public bool CanSelfUpdate => _updates.CanSelfUpdate;

    public bool CannotSelfUpdate => !_updates.CanSelfUpdate;

    public string SelfUpdateBlockReason => _updates.SelfUpdateBlockReason;

    public bool AutoCheck
    {
        get => _autoCheck;
        set
        {
            if (SetProperty(ref _autoCheck, value))
            {
                _settings.Current.AutoCheckAppUpdate = value;
                _settings.Save();
            }
        }
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

    /// <summary>
    /// 窗口打开后若还没有检查过，自动查一次。
    /// 这里由 async void 的 OnOpened 调用，异常一旦逸出就会终止整个程序，所以必须自己兜住。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_available is not null)
        {
            return;
        }

        try
        {
            await CheckAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = $"检查更新失败：{exception.Message}";
        }
    }

    private async Task CheckAsync()
    {
        await RunBusyAsync("正在检查更新 …", async token =>
        {
            var release = await _updates.CheckForUpdateAsync(token).ConfigureAwait(true);

            _settings.Current.LastAppUpdateCheck = DateTimeOffset.Now;
            _settings.Save();

            SetAvailable(release);
            StatusMessage = release is null
                ? $"已是最新版本（{CurrentVersion}）"
                : $"发现新版本 {release.Version}";
        }).ConfigureAwait(true);
    }

    private async Task InstallAsync()
    {
        if (_available is not { } release)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "更新程序",
            $"将从 {CurrentVersion} 更新到 {release.Version}。\n\n" +
            "下载完成后程序会自动关闭并重启，请先结束正在进行的烧录操作。").ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        string? staging = null;

        await RunBusyAsync($"正在准备 {release.Version} …", async token =>
        {
            var progress = new Progress<InstallProgress>(report =>
            {
                StatusMessage = report.Stage;
                IsProgressIndeterminate = report.Percent is null;
                ProgressValue = report.Percent ?? 0;
            });

            staging = await _updates.PrepareAsync(release, progress, token).ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (staging is null)
        {
            return;
        }

        StatusMessage = "正在重启以完成更新 …";
        _updates.LaunchUpdater(staging);

        // 外部脚本正等着本进程退出，这里必须真的退出，否则更新不会发生。
        _requestShutdown();
    }

    private void SetAvailable(AppRelease? release)
    {
        _available = release;
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(ReleaseSizeText));
        OnPropertyChanged(nameof(ReleaseDateText));
        OnPropertyChanged(nameof(ReleaseNotes));
        OnPropertyChanged(nameof(HasUpdate));
        RaiseCommandStates();
    }

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
        StatusMessage = $"更新失败：{exception.Message}";

        // 错误回调是同步的，这里不阻塞，交给消息框自己弹。
        _ = _dialogs.ErrorAsync("程序更新", exception.Message);
    }

    private void RaiseCommandStates()
    {
        CheckCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
