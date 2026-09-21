using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stm32Flash.App.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    void Save();
}

/// <summary>把用户选择读写到 <c>%APPDATA%\Stm32FlashTool\settings.json</c>。</summary>
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
