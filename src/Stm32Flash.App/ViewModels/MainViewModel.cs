using System.Collections.ObjectModel;
using System.Text;
using Stm32Flash.App.Models;
using Stm32Flash.App.Services;

namespace Stm32Flash.App.ViewModels;

/// <summary>主窗口的 ViewModel，负责界面状态与 OpenOCD 操作的编排。</summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>日志上限，超出后丢弃开头部分，避免长时间运行后占用过多内存。</summary>
    private const int MaxLogLength = 400_000;

    private readonly IOpenOcdRunner _runner;
    private readonly IOpenOcdVersionService _versions;
    private readonly IChipCatalogService _catalog;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;

    private readonly StringBuilder _log = new();
    private readonly IProgress<string> _logProgress;

    private CancellationTokenSource? _cancellation;

    private string _firmwarePath = string.Empty;
    private FirmwareFormat _selectedFormat = FirmwareFormat.Elf;
    private string _address = FlashOptions.DefaultAddress;
    private string _readSize = FlashOptions.DefaultReadSize;
    private bool _fullErase = true;
    private int _timeoutSeconds = FlashOptions.DefaultTimeoutSeconds;
    private ProgrammerInterface _selectedInterface = ProgrammerInterface.CmsisDap;
    private ChipDefinition? _selectedChip;
    private OpenOcdInstallation? _selectedOpenOcd;
    private string _updateNotice = string.Empty;
    private string _statusMessage = "就绪";
    private bool _isBusy;
    private string _logText = string.Empty;

    public MainViewModel(
        IOpenOcdRunner runner,
        IOpenOcdVersionService versions,
        IChipCatalogService catalog,
        ISettingsService settings,
        IDialogService dialogs)
    {
        _runner = runner;
        _versions = versions;
        _catalog = catalog;
        _settings = settings;
        _dialogs = dialogs;

        // 在 UI 线程构造，Progress 会把回调切回 UI 线程，OpenOCD 输出可以直接写进日志。
        _logProgress = new Progress<string>(AppendRaw);

        BrowseFirmwareCommand = new AsyncRelayCommand(BrowseFirmwareAsync, () => !IsBusy, ReportError);
        FlashCommand = new AsyncRelayCommand(() => ExecuteAsync(FlashOperation.Flash), () => !IsBusy, ReportError);
        VerifyCommand = new AsyncRelayCommand(() => ExecuteAsync(FlashOperation.Verify), () => !IsBusy, ReportError);
        ReadCommand = new AsyncRelayCommand(() => ExecuteAsync(FlashOperation.Read), () => !IsBusy, ReportError);
        ResetCommand = new AsyncRelayCommand(() => ExecuteAsync(FlashOperation.Reset), () => !IsBusy, ReportError);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearLogCommand = new RelayCommand(ClearLog);
        SaveLogCommand = new AsyncRelayCommand(SaveLogAsync, () => _log.Length > 0, ReportError);
        ManageOpenOcdCommand = new AsyncRelayCommand(ManageOpenOcdAsync, () => !IsBusy, ReportError);
        ReloadChipsCommand = new RelayCommand(ReloadChips, () => !IsBusy);
        OpenChipFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_catalog.UserDirectory));
    }

    public ObservableCollection<ChipDefinition> Chips { get; } = [];

    public ObservableCollection<OpenOcdInstallation> OpenOcdInstallations { get; } = [];

    public IReadOnlyList<ProgrammerInterface> Interfaces => ProgrammerInterface.All;

    public IReadOnlyList<FirmwareFormat> Formats { get; } = [FirmwareFormat.Elf, FirmwareFormat.Bin];

    public AsyncRelayCommand BrowseFirmwareCommand { get; }

    public AsyncRelayCommand FlashCommand { get; }

    public AsyncRelayCommand VerifyCommand { get; }

    public AsyncRelayCommand ReadCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public AsyncRelayCommand SaveLogCommand { get; }

    public AsyncRelayCommand ManageOpenOcdCommand { get; }

    public RelayCommand ReloadChipsCommand { get; }

    public RelayCommand OpenChipFolderCommand { get; }

    public string FirmwarePath
    {
        get => _firmwarePath;
        set => SetProperty(ref _firmwarePath, value);
    }

    public FirmwareFormat SelectedFormat
    {
        get => _selectedFormat;
        set => SetProperty(ref _selectedFormat, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string ReadSize
    {
        get => _readSize;
        set => SetProperty(ref _readSize, value);
    }

    public bool FullErase
    {
        get => _fullErase;
        set => SetProperty(ref _fullErase, value);
    }

    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => SetProperty(ref _timeoutSeconds, Math.Clamp(value, 5, 3600));
    }

    public ProgrammerInterface SelectedInterface
    {
        get => _selectedInterface;
        set => SetProperty(ref _selectedInterface, value ?? ProgrammerInterface.CmsisDap);
    }

    /// <summary>下拉列表中选中的芯片。</summary>
    public ChipDefinition? SelectedChip
    {
        get => _selectedChip;
        set
        {
            if (SetProperty(ref _selectedChip, value) && value is not null)
            {
                ApplyChipDefaults(value);
                OnPropertyChanged(nameof(TargetSummary));
            }
        }
    }

    /// <summary>当前生效的芯片，芯片列表为空时才会是 <c>null</c>。</summary>
    public ChipDefinition? EffectiveChip => SelectedChip;

    /// <summary>目标配置摘要，显示在芯片下拉框下方。</summary>
    public string TargetSummary => EffectiveChip is { } chip
        ? $"配置文件: {chip.TargetConfig}　来源: {Path.GetFileName(chip.SourceFile)}"
        : $"无法识别的型号，请从列表中选择，或在 chips/ 目录添加对应的 YAML";

    public OpenOcdInstallation? SelectedOpenOcd
    {
        get => _selectedOpenOcd;
        set
        {
            if (!SetProperty(ref _selectedOpenOcd, value))
            {
                return;
            }

            OnPropertyChanged(nameof(OpenOcdSummary));
            OnPropertyChanged(nameof(OpenOcdStatusText));
            if (value is not null)
            {
                _settings.Current.SelectedOpenOcdPath = value.ExecutablePath;
                _settings.Save();
            }
        }
    }

    /// <summary>状态栏上显示的版本号，点击即可打开版本管理。</summary>
    public string OpenOcdStatusText => SelectedOpenOcd is { } installation
        ? $"OpenOCD {installation.DisplayName}"
        : "未找到 OpenOCD，点击安装";

    /// <summary>鼠标悬停时显示的完整路径。</summary>
    public string OpenOcdSummary => SelectedOpenOcd is { } installation
        ? installation.ExecutablePath
        : "未找到 OpenOCD，请点击“管理…”下载一个版本";

    /// <summary>有新版本可用时显示的提示，无更新时为空。</summary>
    public string UpdateNotice
    {
        get => _updateNotice;
        private set
        {
            if (SetProperty(ref _updateNotice, value))
            {
                OnPropertyChanged(nameof(HasUpdateNotice));
            }
        }
    }

    public bool HasUpdateNotice => !string.IsNullOrEmpty(UpdateNotice);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
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

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    /// <summary>窗口加载后执行：读芯片目录、恢复上次的选择、找 OpenOCD、按需检查更新。</summary>
    public async Task InitializeAsync()
    {
        LoadChips(announce: false);
        RestoreSettings();

        await RefreshOpenOcdAsync().ConfigureAwait(true);

        if (SelectedOpenOcd is null)
        {
            AppendSystem("未找到 OpenOCD。点击“管理…”可以直接下载一个版本。");
            await _dialogs.WarnAsync(
                "未找到 OpenOCD",
                "没有检测到可用的 OpenOCD。\n\n可以在“OpenOCD 版本 → 管理…”中在线下载，或手动指定一个已有的 openocd。")
                .ConfigureAwait(true);
        }

        await CheckForUpdatesIfDueAsync().ConfigureAwait(true);
    }

    /// <summary>窗口关闭时调用：中止进行中的操作并保存设置。</summary>
    public void Shutdown()
    {
        _cancellation?.Cancel();
        PersistSettings();
    }

    private void LoadChips(bool announce)
    {
        _catalog.Reload();

        Chips.Clear();
        foreach (var chip in _catalog.Chips)
        {
            Chips.Add(chip);
        }

        foreach (var error in _catalog.LoadErrors)
        {
            AppendSystem($"芯片配置警告: {error}");
        }

        if (announce)
        {
            AppendSystem($"已重新加载芯片配置，共 {Chips.Count} 项（目录: {_catalog.BuiltInDirectory}）");
        }

        // 重新加载后对象已经换了一批，按 id 找回原来的选择。
        var previousId = SelectedChip?.Id ?? _settings.Current.TargetChip;
        SelectedChip = _catalog.Resolve(previousId)
                       ?? _catalog.Resolve(FlashOptions.DefaultChipId)
                       ?? Chips.FirstOrDefault();
    }

    private void RestoreSettings()
    {
        var settings = _settings.Current;

        FirmwarePath = settings.FirmwarePath;
        SelectedFormat = Enum.TryParse<FirmwareFormat>(settings.Format, ignoreCase: true, out var format)
            ? format
            : FirmwareFormat.Elf;
        Address = string.IsNullOrWhiteSpace(settings.Address) ? FlashOptions.DefaultAddress : settings.Address;
        ReadSize = string.IsNullOrWhiteSpace(settings.ReadSize) ? FlashOptions.DefaultReadSize : settings.ReadSize;
        FullErase = settings.FullErase;
        TimeoutSeconds = settings.TimeoutSeconds;
        SelectedInterface = ProgrammerInterface.FromId(settings.InterfaceId);

        // Resolve 支持前缀匹配，手工改过 settings.json 写成完整型号时也能落到对应芯片上。
        if (_catalog.Resolve(settings.TargetChip) is { } chip)
        {
            SelectedChip = chip;
        }
    }

    private void PersistSettings()
    {
        var settings = _settings.Current;

        settings.FirmwarePath = FirmwarePath;
        settings.Format = SelectedFormat.ToString();
        settings.Address = Address;
        settings.ReadSize = ReadSize;
        settings.FullErase = FullErase;
        settings.TimeoutSeconds = TimeoutSeconds;
        settings.InterfaceId = SelectedInterface.Id;
        settings.TargetChip = EffectiveChip?.Id ?? settings.TargetChip;
        settings.SelectedOpenOcdPath = SelectedOpenOcd?.ExecutablePath ?? string.Empty;

        _settings.Save();
    }

    /// <summary>选中芯片时套用它在 YAML 里声明的默认地址与容量。</summary>
    private void ApplyChipDefaults(ChipDefinition chip)
    {
        if (!string.IsNullOrWhiteSpace(chip.FlashAddress))
        {
            Address = chip.FlashAddress.Trim();
        }

        if (!string.IsNullOrWhiteSpace(chip.FlashSize))
        {
            ReadSize = chip.FlashSize.Trim();
        }
    }

    private async Task BrowseFirmwareAsync()
    {
        var path = await _dialogs.PickFirmwareFileAsync(FirmwarePath).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FirmwarePath = path;

        // 按扩展名自动切换格式，省得手动改。
        if (FirmwareFormatExtensions.FromFileName(path) is { } format)
        {
            SelectedFormat = format;
        }
    }

    private async Task ExecuteAsync(FlashOperation operation)
    {
        if (SelectedOpenOcd is not { } openOcd)
        {
            await _dialogs.WarnAsync("未找到 OpenOCD", "请先在“OpenOCD 版本 → 管理…”中选择或下载一个版本。").ConfigureAwait(true);
            return;
        }

        if (EffectiveChip is not { } chip)
        {
            await _dialogs.WarnAsync("无效的目标芯片", TargetSummary).ConfigureAwait(true);
            return;
        }

        if (operation is FlashOperation.Flash or FlashOperation.Verify && string.IsNullOrWhiteSpace(FirmwarePath))
        {
            await _dialogs.WarnAsync("缺少固件", "请先选择固件文件。").ConfigureAwait(true);
            return;
        }

        string? outputPath = null;

        if (operation == FlashOperation.Flash)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "确认烧录",
                $"确定要把 {Path.GetFileName(FirmwarePath)} 烧录到 {chip.DisplayName} 吗？" +
                (FullErase ? "\n\n烧录前会执行全片擦除。" : string.Empty)).ConfigureAwait(true);

            if (!confirmed)
            {
                return;
            }
        }
        else if (operation == FlashOperation.Read)
        {
            outputPath = await _dialogs.PickDumpTargetAsync($"{chip.Id}-dump.bin").ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }
        }

        var options = new FlashOptions
        {
            OpenOcdPath = openOcd.ExecutablePath,
            ScriptsDirectory = openOcd.ScriptsDirectory,
            Chip = chip,
            FirmwarePath = FirmwarePath,
            Format = SelectedFormat,
            Address = Address.Trim(),
            ReadSize = ReadSize.Trim(),
            FullErase = FullErase,
            Interface = SelectedInterface,
            TimeoutSeconds = TimeoutSeconds,
        };

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        IsBusy = true;
        StatusMessage = $"正在{operation.ToDisplayName()}…";
        AppendSystem($"开始{operation.ToDisplayName()}（{chip.DisplayName} / {SelectedInterface.DisplayName}）");

        try
        {
            var result = await _runner
                .ExecuteAsync(operation, options, outputPath, _logProgress, cancellation.Token)
                .ConfigureAwait(true);

            AppendSystem(result.Success ? $"✅ {result.Summary}" : $"❌ {result.Summary}");
            if (!string.IsNullOrWhiteSpace(result.Detail))
            {
                AppendRaw(result.Detail);
            }

            StatusMessage = result.Summary;

            if (result.Success)
            {
                var extra = operation == FlashOperation.Read && outputPath is not null
                    ? $"\n\n已保存到:\n{outputPath}"
                    : string.Empty;
                await _dialogs.InfoAsync("操作完成", result.Summary + extra).ConfigureAwait(true);
            }
            else
            {
                await _dialogs.ErrorAsync("操作失败", $"{result.Summary}\n\n{result.Detail}".TrimEnd()).ConfigureAwait(true);
            }
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
            SaveLogCommand.RaiseCanExecuteChanged();
            if (StatusMessage.StartsWith("正在", StringComparison.Ordinal))
            {
                StatusMessage = "就绪";
            }
        }
    }

    private void Cancel()
    {
        _cancellation?.Cancel();
        AppendSystem("正在取消当前操作…");
        StatusMessage = "正在取消…";
    }

    private async Task ManageOpenOcdAsync()
    {
        var viewModel = new OpenOcdManagerViewModel(
            _versions,
            _settings,
            _dialogs,
            SelectedOpenOcd,
            OnOpenOcdActivated);

        // 窗口自己在 Opened 时加载列表，这里先弹出来，避免点了“管理…”半天没反应。
        await _dialogs.ShowOpenOcdManagerAsync(viewModel).ConfigureAwait(true);

        await RefreshOpenOcdAsync().ConfigureAwait(true);
    }

    private void OnOpenOcdActivated(OpenOcdInstallation installation)
    {
        AppendSystem($"已切换 OpenOCD: {installation.DisplayName} → {installation.ExecutablePath}");
        UpdateNotice = string.Empty;
    }

    private async Task RefreshOpenOcdAsync()
    {
        var found = await _versions.DiscoverAsync().ConfigureAwait(true);

        OpenOcdInstallations.Clear();
        foreach (var installation in found)
        {
            OpenOcdInstallations.Add(installation);
        }

        var preferred = _settings.Current.SelectedOpenOcdPath;
        SelectedOpenOcd = OpenOcdInstallations.FirstOrDefault(item =>
                              string.Equals(item.ExecutablePath, preferred, StringComparison.OrdinalIgnoreCase))
                          ?? OpenOcdInstallations.FirstOrDefault();

        if (SelectedOpenOcd is not null)
        {
            AppendSystem($"使用 OpenOCD {SelectedOpenOcd.DisplayName}: {SelectedOpenOcd.ExecutablePath}");
        }
    }

    /// <summary>每天最多在后台检查一次更新，失败时静默处理，不打扰烧录流程。</summary>
    private async Task CheckForUpdatesIfDueAsync()
    {
        if (!_settings.Current.AutoCheckOpenOcdUpdate)
        {
            return;
        }

        var last = _settings.Current.LastUpdateCheck;
        if (last is not null && DateTimeOffset.Now - last.Value < TimeSpan.FromDays(1))
        {
            return;
        }

        try
        {
            var update = await _versions.CheckForUpdateAsync(SelectedOpenOcd?.Version).ConfigureAwait(true);
            _settings.Current.LastUpdateCheck = DateTimeOffset.Now;
            _settings.Save();

            if (update is not null)
            {
                UpdateNotice = $"OpenOCD {update.Version} 可用，点击“管理…”升级";
                AppendSystem($"检测到新版本 OpenOCD {update.Version}（当前 {SelectedOpenOcd?.Version ?? "未知"}）");
            }
        }
        catch (Exception exception)
        {
            AppendSystem($"检查更新失败（可忽略）: {exception.Message}");
        }
    }

    private void ReloadChips()
    {
        LoadChips(announce: true);
        OnPropertyChanged(nameof(TargetSummary));
    }

    private void ClearLog()
    {
        _log.Clear();
        LogText = string.Empty;
        SaveLogCommand.RaiseCanExecuteChanged();
    }

    private async Task SaveLogAsync()
    {
        var path = await _dialogs.PickLogTargetAsync($"stm32-flash-{DateTime.Now:yyyyMMdd-HHmmss}.log").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, _log.ToString()).ConfigureAwait(true);
            StatusMessage = $"日志已保存到 {path}";
        }
        catch (Exception exception)
        {
            await _dialogs.ErrorAsync("保存日志失败", exception.Message).ConfigureAwait(true);
        }
    }

    private void AppendSystem(string message) => AppendRaw($"[{DateTime.Now:HH:mm:ss}] {message}");

    private void AppendRaw(string text)
    {
        var wasEmpty = _log.Length == 0;
        _log.AppendLine(text.TrimEnd());

        if (_log.Length > MaxLogLength)
        {
            _log.Remove(0, _log.Length - MaxLogLength);
        }

        LogText = _log.ToString();

        if (wasEmpty)
        {
            SaveLogCommand.RaiseCanExecuteChanged();
        }
    }

    private void ReportError(Exception exception)
    {
        AppendSystem($"发生异常: {exception.Message}");

        // 错误回调是同步的，这里不阻塞，交给消息框自己弹。
        _ = _dialogs.ErrorAsync("发生异常", exception.ToString());
    }

    private void RaiseCommandStates()
    {
        BrowseFirmwareCommand.RaiseCanExecuteChanged();
        FlashCommand.RaiseCanExecuteChanged();
        VerifyCommand.RaiseCanExecuteChanged();
        ReadCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ManageOpenOcdCommand.RaiseCanExecuteChanged();
        ReloadChipsCommand.RaiseCanExecuteChanged();
    }
}
