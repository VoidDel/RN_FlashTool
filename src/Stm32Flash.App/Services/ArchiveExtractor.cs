using System.Formats.Tar;
using System.IO.Compression;

namespace Stm32Flash.App.Services;

/// <summary>解压发行包：Windows 用 zip，Linux/macOS 用 tar.gz。</summary>
public static class ArchiveExtractor
{
    public static Task ExtractAsync(string archivePath, string targetDirectory, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                Directory.CreateDirectory(targetDirectory);

                if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(archivePath, targetDirectory, overwriteFiles: true);
                    return;
                }

                using var file = File.OpenRead(archivePath);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, targetDirectory, overwriteFiles: true);
            },
            cancellationToken);

    /// <summary>
    /// zip 不保留权限信息，tar.gz 也可能因打包方式丢失；
    /// 在类 Unix 系统上补一次 chmod +x，避免解压后跑不起来。
    /// </summary>
    public static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // 权限设置失败时交给用户自己处理，不中断流程。
        }
    }
}
