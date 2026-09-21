namespace RnFlashTool.App.Services;

/// <summary>程序使用的各个目录，统一放在用户目录下，避免往安装目录写文件。</summary>
public static class AppPaths
{
    private const string FolderName = "RN_FlashTool";

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

    /// <summary>项目改名前（STM32 时期）使用的目录名。</summary>
    private const string LegacyFolderName = "Stm32FlashTool";

    /// <summary>改名前的本地数据目录，用于改写设置里残留的绝对路径。</summary>
    public static string LegacyDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);

    /// <summary>
    /// 把指向旧数据目录的绝对路径改写到新目录。
    /// 设置里存的是 openocd 的完整路径，光搬目录不改路径的话，
    /// 升级后会找不到用户之前下载并选中的那个版本。
    /// </summary>
    public static string RemapLegacyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith(LegacyDataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return path ?? string.Empty;
        }

        return DataDirectory + path[LegacyDataDirectory.Length..];
    }

    /// <summary>
    /// 把旧目录下的配置与数据搬到新目录。
    /// 改名后若不迁移，用户已下载的 OpenOCD 和已保存的设置会凭空消失。
    /// 只在新目录尚不存在时执行，因此重复调用是安全的。
    /// </summary>
    public static void MigrateLegacyDirectories()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        TryMove(Path.Combine(appData, LegacyFolderName), ConfigDirectory);
        TryMove(Path.Combine(localAppData, LegacyFolderName), DataDirectory);
    }

    private static void TryMove(string legacy, string target)
    {
        try
        {
            if (Directory.Exists(legacy) && !Directory.Exists(target))
            {
                Directory.Move(legacy, target);
            }
        }
        catch (Exception)
        {
            // 迁移失败就当作全新安装，不影响程序启动。
        }
    }
}
