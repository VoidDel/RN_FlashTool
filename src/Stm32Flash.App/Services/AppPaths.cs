namespace Stm32Flash.App.Services;

/// <summary>程序使用的各个目录，统一放在用户目录下，避免往安装目录写文件。</summary>
public static class AppPaths
{
    private const string FolderName = "Stm32FlashTool";

    /// <summary>可执行文件所在目录。</summary>
    public static string AppDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>漫游配置目录，存放 settings.json。</summary>
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    /// <summary>本地数据目录，存放下载的 OpenOCD 与临时文件。</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    public static string SettingsFile { get; } = Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>下载安装的 OpenOCD 各版本的根目录。</summary>
    public static string OpenOcdInstallRoot { get; } = Path.Combine(DataDirectory, "openocd");

    /// <summary>生成的临时 OpenOCD 配置文件目录。</summary>
    public static string TempDirectory { get; } = Path.Combine(DataDirectory, "temp");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
