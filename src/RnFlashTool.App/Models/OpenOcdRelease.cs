namespace RnFlashTool.App.Models;

/// <summary>远端可供下载的一个 OpenOCD 发行版本。</summary>
/// <param name="Version">规范化后的版本号，例如 “0.12.0-6”。</param>
/// <param name="TagName">发行标签，例如 “v0.12.0-6”。</param>
/// <param name="AssetName">与当前系统架构匹配的压缩包文件名。</param>
/// <param name="DownloadUrl">压缩包下载地址。</param>
/// <param name="SizeBytes">压缩包大小。</param>
/// <param name="PublishedAt">发布时间。</param>
/// <param name="IsPrerelease">是否为预发布版本。</param>
public sealed record OpenOcdRelease(
    string Version,
    string TagName,
    string AssetName,
    string DownloadUrl,
    long SizeBytes,
    DateTimeOffset PublishedAt,
    bool IsPrerelease)
{
    public string SizeText => SizeBytes > 0 ? $"{SizeBytes / 1024d / 1024d:F1} MB" : "未知大小";

    public string DisplayName => IsPrerelease ? $"{Version}（预发布）" : Version;

    public override string ToString() => DisplayName;
}
