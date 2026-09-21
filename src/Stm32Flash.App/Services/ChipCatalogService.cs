using Stm32Flash.App.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Stm32Flash.App.Services;

public interface IChipCatalogService
{
    /// <summary>当前已加载的全部芯片定义，按系列和名称排序。</summary>
    IReadOnlyList<ChipDefinition> Chips { get; }

    /// <summary>加载过程中出现的问题（文件解析失败、字段缺失等）。</summary>
    IReadOnlyList<string> LoadErrors { get; }

    /// <summary>内置芯片配置目录。</summary>
    string BuiltInDirectory { get; }

    /// <summary>用户芯片配置目录，同 id 会覆盖内置定义。</summary>
    string UserDirectory { get; }

    /// <summary>重新扫描两个目录。</summary>
    void Reload();

    /// <summary>按 id / 名称精确查找，找不到再按前缀匹配。</summary>
    ChipDefinition? Resolve(string? chipText);
}

/// <summary>从 <c>chips/</c> 目录里的 YAML 文件加载受支持的芯片列表。</summary>
public sealed class ChipCatalogService : IChipCatalogService
{
    private static readonly string[] YamlPatterns = ["*.yaml", "*.yml"];

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private List<ChipDefinition> _chips = [];
    private List<string> _errors = [];

    public ChipCatalogService()
    {
        Reload();
    }

    public IReadOnlyList<ChipDefinition> Chips => _chips;

    public IReadOnlyList<string> LoadErrors => _errors;

    public string BuiltInDirectory { get; } = Path.Combine(AppPaths.AppDirectory, "chips");

    public string UserDirectory { get; } = Path.Combine(AppPaths.ConfigDirectory, "chips");

    public void Reload()
    {
        var errors = new List<string>();
        var byId = new Dictionary<string, ChipDefinition>(StringComparer.OrdinalIgnoreCase);

        LoadDirectory(BuiltInDirectory, isUserDefined: false, byId, errors);
        LoadDirectory(UserDirectory, isUserDefined: true, byId, errors);

        if (byId.Count == 0)
        {
            errors.Add($"未找到任何芯片配置文件，请检查目录: {BuiltInDirectory}");
        }

        _chips = [.. byId.Values
            .OrderBy(chip => chip.Series, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chip => chip.DisplayName, StringComparer.OrdinalIgnoreCase)];
        _errors = errors;
    }

    public ChipDefinition? Resolve(string? chipText)
    {
        if (string.IsNullOrWhiteSpace(chipText))
        {
            return null;
        }

        var text = chipText.Trim();

        var exact = _chips.FirstOrDefault(chip =>
            string.Equals(chip.Id, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(chip.DisplayName, text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // 手动输入完整型号（例如 stm32f407vgt6）时按最长前缀匹配。
        return _chips
            .SelectMany(chip => chip.EffectivePrefixes.Select(prefix => (chip, prefix)))
            .Where(item => text.StartsWith(item.prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.prefix.Length)
            .Select(item => item.chip)
            .FirstOrDefault();
    }

    private void LoadDirectory(
        string directory,
        bool isUserDefined,
        Dictionary<string, ChipDefinition> byId,
        List<string> errors)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = YamlPatterns
            .SelectMany(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                var chip = _deserializer.Deserialize<ChipDefinition>(File.ReadAllText(file));
                if (chip is null)
                {
                    errors.Add($"{Path.GetFileName(file)}: 文件为空");
                    continue;
                }

                var problem = chip.Validate();
                if (problem is not null)
                {
                    errors.Add($"{Path.GetFileName(file)}: {problem}");
                    continue;
                }

                chip.SourceFile = file;
                chip.IsUserDefined = isUserDefined;
                byId[chip.Id] = chip;
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(file)}: 解析失败 - {exception.Message}");
            }
        }
    }
}
