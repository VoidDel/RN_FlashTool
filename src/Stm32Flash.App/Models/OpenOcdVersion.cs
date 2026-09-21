using System.Text.RegularExpressions;

namespace Stm32Flash.App.Models;

/// <summary>
/// OpenOCD 版本号的宽松解析与比较。
/// 能处理 “0.12.0-6”、“v0.12.0-6”、“0.12.0+dev-02228-ge5888bda3” 这几类写法。
/// </summary>
public static partial class OpenOcdVersion
{
    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?(?:[-.](\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>从任意字符串中提取版本号；提取不到时返回 <c>null</c>。</summary>
    public static string? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = VersionPattern().Match(text);
        return match.Success ? match.Value.TrimStart('v', 'V') : null;
    }

    /// <summary>从 <c>openocd --version</c> 的输出中提取版本号。</summary>
    public static string? ParseFromVersionOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        var version = Parse(firstLine) ?? Parse(output);
        if (version is null)
        {
            return null;
        }

        // 保留 “+dev” 标记，后面判断是否值得升级时要用到。
        var source = firstLine ?? output;
        return source.Contains("+dev", StringComparison.OrdinalIgnoreCase) ? version + "+dev" : version;
    }

    /// <summary>比较两个版本号；无法解析的一方视为更旧。</summary>
    public static int Compare(string? left, string? right)
    {
        var leftParts = ToParts(left);
        var rightParts = ToParts(right);

        for (var i = 0; i < 4; i++)
        {
            var result = leftParts[i].CompareTo(rightParts[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    /// <summary>
    /// 判断 <paramref name="candidate"/> 是否值得升级。
    /// 内置的 xPack 构建版本形如 “0.12.0+dev-02228”，解析不出打包修订号，
    /// 直接按修订号比较会一直提示“有新版本”，所以对开发版只比较 主.次.补丁。
    /// </summary>
    public static bool IsUpgrade(string? candidate, string? current, bool currentIsDevBuild)
    {
        if (!currentIsDevBuild)
        {
            return IsNewer(candidate, current);
        }

        var left = ToParts(candidate);
        var right = ToParts(current);
        for (var i = 0; i < 3; i++)
        {
            var result = left[i].CompareTo(right[i]);
            if (result != 0)
            {
                return result > 0;
            }
        }

        return false;
    }

    /// <summary>xPack 的开发快照带 “+dev” 后缀。</summary>
    public static bool IsDevBuild(string? versionOutput) =>
        versionOutput?.Contains("dev", StringComparison.OrdinalIgnoreCase) == true;

    private static int[] ToParts(string? version)
    {
        var parts = new[] { -1, -1, -1, -1 };
        if (string.IsNullOrWhiteSpace(version))
        {
            return parts;
        }

        var match = VersionPattern().Match(version);
        if (!match.Success)
        {
            return parts;
        }

        for (var i = 0; i < 4; i++)
        {
            var group = match.Groups[i + 1];
            parts[i] = group.Success && int.TryParse(group.Value, out var value) ? value : 0;
        }

        return parts;
    }
}
