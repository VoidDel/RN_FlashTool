using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using RnFlashTool.App.Views;

namespace RnFlashTool.App.Services;

/// <inheritdoc cref="IDialogService"/>
public sealed class DialogService : IDialogService
{
    private static readonly FilePickerFileType FirmwareFiles = new("固件文件")
    {
        Patterns = ["*.elf", "*.bin", "*.hex", "*.axf"],
    };

    private static readonly FilePickerFileType BinFiles = new("BIN 文件") { Patterns = ["*.bin"] };

    private static readonly FilePickerFileType LogFiles = new("日志文件") { Patterns = ["*.log", "*.txt"] };

    private static readonly FilePickerFileType AllFiles = new("所有文件") { Patterns = ["*"] };

    public async Task<string?> PickFirmwareFileAsync(string? initialPath)
    {
        if (await GetStorageAsync().ConfigureAwait(true) is not { } storage)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = "选择固件文件",
            AllowMultiple = false,
            FileTypeFilter = [FirmwareFiles, AllFiles],
        };

        // 从上次选择的目录继续，省得每次都从头找。
        var directory = Path.GetDirectoryName(initialPath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(directory).ConfigureAwait(true);
        }

        var files = await storage.OpenFilePickerAsync(options).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public Task<string?> PickDumpTargetAsync(string suggestedFileName) =>
        SaveAsync("保存读取的固件", suggestedFileName, "bin", [BinFiles, AllFiles]);

    public Task<string?> PickLogTargetAsync(string suggestedFileName) =>
        SaveAsync("保存操作日志", suggestedFileName, "log", [LogFiles, AllFiles]);

    public async Task<string?> PickOpenOcdExecutableAsync()
    {
        if (await GetStorageAsync().ConfigureAwait(true) is not { } storage)
        {
            return null;
        }

        // Windows 上过滤 .exe，其它平台的可执行文件没有固定扩展名，放开筛选。
        var filters = OperatingSystem.IsWindows()
            ? new List<FilePickerFileType> { new("可执行文件") { Patterns = ["*.exe"] }, AllFiles }
            : [AllFiles];

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 openocd 可执行文件",
            AllowMultiple = false,
            FileTypeFilter = filters,
        }).ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        MessageDialog.ShowAsync(Owner, title, message, MessageDialogKind.Question, confirm: true);

    public Task InfoAsync(string title, string message) =>
        MessageDialog.ShowAsync(Owner, title, message, MessageDialogKind.Information, confirm: false);

    public Task WarnAsync(string title, string message) =>
        MessageDialog.ShowAsync(Owner, title, message, MessageDialogKind.Warning, confirm: false);

    public Task ErrorAsync(string title, string message) =>
        MessageDialog.ShowAsync(Owner, title, message, MessageDialogKind.Error, confirm: false);

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            // 各平台打开文件管理器的命令不同。
            var (fileName, argument) = OperatingSystem.IsWindows()
                ? ("explorer.exe", path)
                : OperatingSystem.IsMacOS()
                    ? ("open", path)
                    : ("xdg-open", path);

            Process.Start(new ProcessStartInfo(fileName, argument) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception)
        {
            _ = ErrorAsync("打开目录失败", $"{path}\n\n{exception.Message}");
        }
    }

    public void OpenUrl(string url)
    {
        try
        {
            // UseShellExecute 让系统按协议关联去开默认浏览器，三个平台都适用。
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception)
        {
            _ = ErrorAsync("打开链接失败", $"{url}\n\n{exception.Message}");
        }
    }

    public Task ShowOpenOcdManagerAsync(object viewModel) =>
        ShowDialogAsync(new OpenOcdManagerWindow { DataContext = viewModel });

    public Task ShowAppUpdateAsync(object viewModel) =>
        ShowDialogAsync(new AppUpdateWindow { DataContext = viewModel });

    private static async Task ShowDialogAsync(Window window)
    {
        if (Owner is { } owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>当前活动窗口，用作对话框的父窗口。</summary>
    private static Window? Owner
    {
        get
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            {
                return null;
            }

            return desktop.Windows.FirstOrDefault(window => window.IsActive) ?? desktop.MainWindow;
        }
    }

    private static Task<IStorageProvider?> GetStorageAsync() =>
        Task.FromResult(Owner?.StorageProvider);

    private static async Task<string?> SaveAsync(
        string title,
        string suggestedFileName,
        string defaultExtension,
        IReadOnlyList<FilePickerFileType> filters)
    {
        if (await GetStorageAsync().ConfigureAwait(true) is not { } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [.. filters],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }
}
