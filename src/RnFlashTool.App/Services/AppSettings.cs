using RnFlashTool.App.Models;

namespace RnFlashTool.App.Services;

/// <summary>持久化到 settings.json 的用户选择。</summary>
public sealed class AppSettings
{
    public string FirmwarePath { get; set; } = string.Empty;

    public string Format { get; set; } = FirmwareFormat.Elf.ToString();

    public string Address { get; set; } = FlashOptions.DefaultAddress;

    public string ReadSize { get; set; } = FlashOptions.DefaultReadSize;

    public bool FullErase { get; set; } = true;

    public string InterfaceId { get; set; } = ProgrammerInterface.CmsisDap.Id;

    public string TargetChip { get; set; } = FlashOptions.DefaultChipId;

    public int TimeoutSeconds { get; set; } = FlashOptions.DefaultTimeoutSeconds;

    /// <summary>当前选中的 OpenOCD 可执行文件路径；为空表示自动选择。</summary>
    public string SelectedOpenOcdPath { get; set; } = string.Empty;

    /// <summary>启动时自动检查 OpenOCD 更新。</summary>
    public bool AutoCheckOpenOcdUpdate { get; set; } = true;

    /// <summary>下载加速前缀，例如 “https://ghfast.top/”，留空表示直连 GitHub。</summary>
    public string DownloadMirror { get; set; } = string.Empty;

    /// <summary>上次检查 OpenOCD 更新的时间，用于限制检查频率。</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>启动时自动检查程序自身的更新。</summary>
    public bool AutoCheckAppUpdate { get; set; } = true;

    /// <summary>上次检查程序更新的时间。</summary>
    public DateTimeOffset? LastAppUpdateCheck { get; set; }
}
