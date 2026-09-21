using System.Net.Http;
using System.Net.Http.Headers;

namespace RnFlashTool.App.Services;

/// <summary>
/// 访问 GitHub Releases 的共用客户端：OpenOCD 版本管理和程序自升级都走这里，
/// 下载加速前缀之类的设置只需在一处生效。
/// </summary>
public sealed class GitHubClient
{
    private readonly ISettingsService _settings;
    private readonly HttpClient _http;

    public GitHubClient(ISettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RN_FlashTool", "2.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>拉取某个仓库的发行版列表（原始 JSON）。</summary>
    public Task<string> GetReleasesJsonAsync(string repository, int count, CancellationToken cancellationToken) =>
        _http.GetStringAsync(
            $"https://api.github.com/repos/{repository}/releases?per_page={count}",
            cancellationToken);

    /// <summary>下载文件到指定路径，按字节数回报进度。</summary>
    public async Task DownloadAsync(
        string url,
        string destination,
        long expectedSize,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(ApplyMirror(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        var lastReported = -1;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;

            if (total <= 0)
            {
                continue;
            }

            var percent = (int)(downloaded * 100 / total);
            if (percent != lastReported)
            {
                lastReported = percent;
                progress.Report(new InstallProgress(
                    $"正在下载 … {downloaded / 1024d / 1024d:F1} / {total / 1024d / 1024d:F1} MB",
                    percent));
            }
        }
    }

    /// <summary>应用下载加速前缀，方便在访问 GitHub 受限的网络里下载。</summary>
    private string ApplyMirror(string url)
    {
        var mirror = _settings.Current.DownloadMirror?.Trim();
        if (string.IsNullOrEmpty(mirror))
        {
            return url;
        }

        return mirror.EndsWith('/') ? mirror + url : $"{mirror}/{url}";
    }
}
