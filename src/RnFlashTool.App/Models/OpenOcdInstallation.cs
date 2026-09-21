namespace RnFlashTool.App.Models;

/// <summary>一个 OpenOCD 安装的来源。</summary>
public enum OpenOcdSource
{
    /// <summary>随程序一起分发的 openocd 目录。</summary>
    Bundled,

    /// <summary>本工具下载并安装到用户目录的版本。</summary>
    Managed,

    /// <summary>系统 PATH 中找到的版本。</summary>
    SystemPath,

    /// <summary>用户手动指定的可执行文件。</summary>
    Custom,
}

/// <summary>一个可用的 OpenOCD 安装。</summary>
/// <param name="ExecutablePath">openocd 可执行文件的完整路径，同时用作唯一标识。</param>
/// <param name="Source">该安装的来源。</param>
/// <param name="Version">版本号；未探测或探测失败时为 <c>null</c>。</param>
/// <param name="InstallDirectory">安装根目录；仅 <see cref="OpenOcdSource.Managed"/> 可被卸载。</param>
public sealed record OpenOcdInstallation(
    string ExecutablePath,
    OpenOcdSource Source,
    string? Version = null,
    string? InstallDirectory = null)
{
    /// <summary>只有自己下载安装的版本才允许删除。</summary>
    public bool CanUninstall => Source == OpenOcdSource.Managed && !string.IsNullOrEmpty(InstallDirectory);

    public string DisplayName => Version ?? "未知版本";

    /// <summary>OpenOCD 的 scripts 目录，找不到时返回 <c>null</c>，由 OpenOCD 自行推断。</summary>
    public string? ScriptsDirectory => ResolveScriptsDirectory(ExecutablePath);

    /// <summary>在可执行文件附近按常见发行布局查找 scripts 目录。</summary>
    public static string? ResolveScriptsDirectory(string executablePath)
    {
        var binDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        var root = Path.GetDirectoryName(binDirectory);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        string[] candidates =
        [
            Path.Combine(root, "openocd", "scripts"),        // xPack 布局
            Path.Combine(root, "share", "openocd", "scripts"), // 标准 autotools 布局
            Path.Combine(root, "scripts"),                   // 部分 Windows 绿色包
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    public override string ToString() => DisplayName;
}
