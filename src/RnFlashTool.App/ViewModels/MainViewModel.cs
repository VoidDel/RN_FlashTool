using System.Collections.ObjectModel;
using System.Text;
using RnFlashTool.App.Models;
using RnFlashTool.App.Services;

namespace RnFlashTool.App.ViewModels;

/// <summary>主窗口的 ViewModel，负责界面状态与 OpenOCD 操作的编排。</summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>日志上限，超出后丢弃开头部分，避免长时间运行后占用过多内存。</summary>
    private const int MaxLogLength = 400_000;

    private readonly IOpenOcdRunner _runner;
    private readonly IOpenOcdVersionService _versions;
    private readonly IAppUpdateService _appUpdates;
    private readonly IChipCatalogService _catalog;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;

    /// <summary>触发程序退出，由宿主传入，供“更新后重启”使用。</summary>
    private readonly Action _requestShutdown;

    private readonly StringBuilder _log = new();
    private readonly IProgress<string> _logProgress;

    private CancellationTokenSource? _cancellation;

    private string _firmwarePath = string.Empty;
    private FirmwareFormat _selectedFormat = FirmwareFormat.Elf;
    // 地址与长度不再出现在主界面上：烧录用芯片 YAML 里声明的地址，
    // 读取 / 验证按下去时才弹窗询问，这两个字段只是记住上次填过的值。
    private string _lastAddress = FlashOptions.DefaultAddress;
    private string _lastReadSize = FlashOptions.DefaultReadSize;
    private bool _fullErase = true;
    private int _timeoutSeconds = FlashOptions.DefaultTimeoutSeconds;
    private ProgrammerInterface _selectedInterface = ProgrammerInterface.CmsisDap;
    private ChipDefinition? _selectedChip;
    private OpenOcdInstallation? _selectedOpenOcd;
    private string _updateNotice = string.Empty;
    private string _appUpdateNotice = string.Empty;
    private AppRelease? _appUpdate;
    private string _statusMessage = "就绪";
    private bool _isBusy;
    private string _logText = string.Empty;

    public MainViewModel(
        IOpenOcdRunner runner,
        IOpenOcdVersionService versions,
        IAppUpdateService appUpdates,
        IChipCatalogService catalog,
        ISettingsService settings,
        IDialogService dialogs,
        Action requestShutdown)
    {
        _runner = runner;
        _versions = versions;
        _appUpdates = appUpdates;
        _catalog = catalog;
        _settings = settings;
        _dialogs = dialogs;
        _requestShutdown = requestShutdown;

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
        ShowAppUpdateCommand = new AsyncRelayCommand(ShowAppUpdateAsync, () => !IsBusy, ReportError);
        ReloadChipsCommand = new RelayCommand(ReloadChips, () => !IsBusy);
        OpenChipFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_catalog.UserDirectory));
    }

    public ObservableCollection<ChipDefinition> Chips { get; } = [];

    public ObservableCollection<OpenOcdInstallation> OpenOcdInstallations { get; } = [];

    public IReadOnlyList<ProgrammerInterface> Interfaces => ProgrammerInterface.All;

    public IReadOnlyList<FirmwareFormat> Formats { get; } =
        [FirmwareFormat.Elf, FirmwareFormat.Bin, FirmwareFormat.Ihex, FirmwareFormat.S19];

    public AsyncRelayCommand BrowseFirmwareCommand { get; }

    public AsyncRelayCommand FlashCommand { get; }

    public AsyncRelayCommand VerifyCommand { get; }

    public AsyncRelayCommand ReadCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public AsyncRelayCommand SaveLogCommand { get; }

    public AsyncRelayCommand ManageOpenOcdCommand { get; }

    public AsyncRelayCommand ShowAppUpdateCommand { get; }

    public RelayCommand ReloadChipsCommand { get; }

    public RelayCommand OpenChipFolderCommand { get; }

    public string FirmwarePath
    {
        get => _firmwarePath;
        set
        {
            if (SetProperty(ref _firmwarePath, value))
            {
                OnPropertyChanged(nameof(ShowFormatPicker));
                OnPropertyChanged(nameof(HasAutoFormat));
                OnPropertyChanged(nameof(FirmwareFormatSummary));
            }
        }
    }

    /// <summary>下拉框里选的格式，只有扩展名认不出来时才起作用。</summary>
    public FirmwareFormat SelectedFormat
    {
        get => _selectedFormat;
        set => SetProperty(ref _selectedFormat, value);
    }

    /// <summary>本次操作真正使用的格式：扩展名认得出来就听扩展名的，认不出来才看下拉框。</summary>
    public FirmwareFormat EffectiveFormat =>
        FirmwareFormatExtensions.FromFileName(FirmwarePath) ?? SelectedFormat;

    /// <summary>扩展名认不出格式时（例如没有扩展名的编译产物）才把下拉框放出来。</summary>
    public bool ShowFormatPicker =>
        !string.IsNullOrWhiteSpace(FirmwarePath) && FirmwareFormatExtensions.FromFileName(FirmwarePath) is null;

    public bool HasAutoFormat => !ShowFormatPicker;

    /// <summary>自动识别成功时显示的一行提示，代替下拉框。</summary>
    public string FirmwareFormatSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FirmwarePath))
            {
                return "支持 .elf / .axf / .out、.hex、.s19 / .srec / .mot 与 .bin，格式按扩展名自动识别";
            }

            return FirmwareFormatExtensions.FromFileName(FirmwarePath) is { } format
                ? format == FirmwareFormat.Bin
                    ? "格式: BIN　烧录用芯片定义里的地址，验证时会询问地址"
                    : $"格式: {format.ToDisplayName()}　地址由文件自带"
                : string.Empty;
        }
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

    /// <summary>
    /// 烧录用的起始地址：取芯片 YAML 里的 <c>flashAddress</c>，没写时退回 0x08000000。
    /// 界面上不再让改，所以这里不能受“读取 / 验证”弹窗里填过的值影响。
    /// </summary>
    private string FlashAddress => EffectiveChip?.FlashAddress is { } configured && !string.IsNullOrWhiteSpace(configured)
        ? configured.Trim()
        : FlashOptions.DefaultAddress;

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

    /// <summary>状态栏上显示的程序版本，点击即可打开更新窗口。</summary>
    public string AppVersionText => $"v{_appUpdates.CurrentVersion}";

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

    /// <summary>程序自身有新版本时显示的提示，无更新时为空。</summary>
    public string AppUpdateNotice
    {
        get => _appUpdateNotice;
        private set
        {
            if (SetProperty(ref _appUpdateNotice, value))
            {
                OnPropertyChanged(nameof(HasAppUpdateNotice));
            }
        }
    }

    public bool HasAppUpdateNotice => !string.IsNullOrEmpty(AppUpdateNotice);

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
        await CheckForAppUpdateIfDueAsync().ConfigureAwait(true);
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
        _lastAddress = string.IsNullOrWhiteSpace(settings.Address) ? FlashOptions.DefaultAddress : settings.Address;
        _lastReadSize = string.IsNullOrWhiteSpace(settings.ReadSize) ? FlashOptions.DefaultReadSize : settings.ReadSize;
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
        settings.Address = _lastAddress;
        settings.ReadSize = _lastReadSize;
        settings.FullErase = FullErase;
        settings.TimeoutSeconds = TimeoutSeconds;
        settings.InterfaceId = SelectedInterface.Id;
        settings.TargetChip = EffectiveChip?.Id ?? settings.TargetChip;
        settings.SelectedOpenOcdPath = SelectedOpenOcd?.ExecutablePath ?? string.Empty;

        _settings.Save();
    }

    /// <summary>换芯片时把 YAML 里声明的地址与容量作为下次弹窗的默认值。</summary>
    private void ApplyChipDefaults(ChipDefinition chip)
    {
        if (!string.IsNullOrWhiteSpace(chip.FlashAddress))
        {
            _lastAddress = chip.FlashAddress.Trim();
        }

        if (!string.IsNullOrWhiteSpace(chip.FlashSize))
        {
            _lastReadSize = chip.FlashSize.Trim();
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

        // 格式由 EffectiveFormat 按扩展名现算，这里只是顺手把下拉框的值也对齐，
        // 下次选到没有扩展名的文件时它就是个合理的起点。
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

        // 烧录固定用芯片声明的地址；读取每次都问，验证只在 BIN 这种不带地址的格式下问。
        var address = FlashAddress;
        var readSize = _lastReadSize;

        if (operation == FlashOperation.Flash)
        {
            var target = !EffectiveFormat.CarriesAddress()
                ? $"{chip.DisplayName} 的 {address}"
                : chip.DisplayName;

            var confirmed = await _dialogs.ConfirmAsync(
                "确认烧录",
                $"确定要把 {Path.GetFileName(FirmwarePath)} 烧录到 {target} 吗？" +
                (FullErase ? "\n\n烧录前会执行全片擦除。" : string.Empty)).ConfigureAwait(true);

            if (!confirmed)
            {
                return;
            }
        }
        else if (operation == FlashOperation.Verify && !EffectiveFormat.CarriesAddress())
        {
            // ELF 的段地址和长度都在文件里，verify_image 自己会读，不必打扰用户；
            // BIN 是裸二进制，不问就不知道该跟哪一段比。
            if (await _dialogs.AskMemoryRangeAsync(
                    "验证固件",
                    $"将 {Path.GetFileName(FirmwarePath)} 与该地址开始的内容逐字节比对。",
                    _lastAddress,
                    null).ConfigureAwait(true) is not { } input)
            {
                return;
            }

            _lastAddress = input.Address;
            address = input.Address;
        }
        else if (operation == FlashOperation.Read)
        {
            if (await _dialogs.AskMemoryRangeAsync(
                    "读取固件",
                    $"从 {chip.DisplayName} 读回一段内容并存成 .bin 文件。",
                    _lastAddress,
                    _lastReadSize).ConfigureAwait(true) is not { } input)
            {
                return;
            }

            _lastAddress = input.Address;
            _lastReadSize = input.ReadSize;
            address = input.Address;
            readSize = input.ReadSize;

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
            Format = EffectiveFormat,
            Address = address.Trim(),
            ReadSize = readSize.Trim(),
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

    private async Task ShowAppUpdateAsync()
    {
        var viewModel = new AppUpdateViewModel(_appUpdates, _settings, _dialogs, _appUpdate, _requestShutdown);

        await _dialogs.ShowAppUpdateAsync(viewModel).ConfigureAwait(true);

        // 窗口里可能刚装完或刚确认已是最新，提示条跟着消掉。
        if (!_appUpdates.CanSelfUpdate || _appUpdate is null)
        {
            return;
        }

        AppUpdateNotice = string.Empty;
    }

    /// <summary>每天最多在后台检查一次程序更新，失败时静默处理。</summary>
    private async Task CheckForAppUpdateIfDueAsync()
    {
        if (!_settings.Current.AutoCheckAppUpdate)
        {
            return;
        }

        var last = _settings.Current.LastAppUpdateCheck;
        if (last is not null && DateTimeOffset.Now - last.Value < TimeSpan.FromDays(1))
        {
            return;
        }

        try
        {
            _appUpdate = await _appUpdates.CheckForUpdateAsync().ConfigureAwait(true);
            _settings.Current.LastAppUpdateCheck = DateTimeOffset.Now;
            _settings.Save();

            if (_appUpdate is not null)
            {
                AppUpdateNotice = $"程序 {_appUpdate.Version} 可用，点击查看";
                AppendSystem($"检测到程序新版本 {_appUpdate.Version}（当前 {_appUpdates.CurrentVersion}）");
            }
        }
        catch (Exception exception)
        {
            AppendSystem($"检查程序更新失败（可忽略）: {exception.Message}");
        }
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
        ShowAppUpdateCommand.RaiseCanExecuteChanged();
        ReloadChipsCommand.RaiseCanExecuteChanged();
    }
}
