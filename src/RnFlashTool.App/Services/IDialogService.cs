namespace RnFlashTool.App.Services;

/// <summary>
/// 把所有弹窗、文件对话框收在一处，ViewModel 只依赖这个接口，便于替换与测试。
/// Avalonia 的文件对话框是异步的，所以这里全部返回 Task。
/// </summary>
public interface IDialogService
{
    /// <summary>选择固件文件，取消时返回 <c>null</c>。</summary>
    Task<string?> PickFirmwareFileAsync(string? initialPath);

    /// <summary>选择读取固件的保存位置，取消时返回 <c>null</c>。</summary>
    Task<string?> PickDumpTargetAsync(string suggestedFileName);

    /// <summary>选择日志导出位置，取消时返回 <c>null</c>。</summary>
    Task<string?> PickLogTargetAsync(string suggestedFileName);

    /// <summary>手动指定 openocd 可执行文件，取消时返回 <c>null</c>。</summary>
    Task<string?> PickOpenOcdExecutableAsync();

    Task<bool> ConfirmAsync(string title, string message);

    Task InfoAsync(string title, string message);

    Task WarnAsync(string title, string message);

    Task ErrorAsync(string title, string message);

    /// <summary>在系统文件管理器中打开一个目录，目录不存在时会先创建。</summary>
    void OpenFolder(string path);

    /// <summary>用系统默认浏览器打开一个网址。</summary>
    void OpenUrl(string url);

    /// <summary>打开 OpenOCD 版本管理窗口。</summary>
    Task ShowOpenOcdManagerAsync(object viewModel);

    /// <summary>打开程序更新窗口。</summary>
    Task ShowAppUpdateAsync(object viewModel);
}
