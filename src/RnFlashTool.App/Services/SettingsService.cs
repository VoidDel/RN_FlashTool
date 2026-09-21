using System.Text.Json;
using System.Text.Json.Serialization;

namespace RnFlashTool.App.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    void Save();
}

/// <summary>把用户选择读写到 <c>%APPDATA%\RN_FlashTool\settings.json</c>。</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SettingsService()
    {
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDirectory(AppPaths.ConfigDirectory);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception)
        {
            // 配置保存失败不应该影响使用，忽略即可。
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    // 改名前保存的设置里存着指向旧数据目录的绝对路径，这里一并改写
                    settings.SelectedOpenOcdPath = AppPaths.RemapLegacyPath(settings.SelectedOpenOcdPath);
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // 配置损坏时退回默认值。
        }

        return new AppSettings();
    }
}
