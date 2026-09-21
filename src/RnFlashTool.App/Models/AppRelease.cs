namespace RnFlashTool.App.Models;

/// <summary>本程序的一个可下载版本。</summary>
/// <param name="Version">版本号，例如 “2.1.0”。</param>
/// <param name="TagName">发行标签，例如 “v2.1.0”。</param>
/// <param name="AssetName">与当前系统匹配的安装包文件名。</param>
/// <param name="DownloadUrl">安装包下载地址。</param>
/// <param name="SizeBytes">安装包大小。</param>
/// <param name="PublishedAt">发布时间。</param>
/// <param name="Notes">发行说明。</param>
public sealed record AppRelease(
    string Version,
    string TagName,
    string AssetName,
    string DownloadUrl,
    long SizeBytes,
    DateTimeOffset PublishedAt,
    string Notes)
{
    public string SizeText => SizeBytes > 0 ? $"{SizeBytes / 1024d / 1024d:F1} MB" : "未知大小";

    public override string ToString() => Version;
}
