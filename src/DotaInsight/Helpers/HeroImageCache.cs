using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DotaInsight.Helpers;

/// <summary>
/// 英雄头像缓存：%LocalAppData%\DotaInsight\hero-icons\ + 内存。
/// </summary>
public static class HeroImageCache
{
    private static readonly ConcurrentDictionary<string, ImageSource> MemoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> DownloadGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Http = CreateHttpClient();

    private static string CacheDirectory
    {
        get
        {
            AppPaths.EnsureCreated();
            return AppPaths.HeroIcons;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
        return client;
    }

    /// <summary>当前头像缓存目录。</summary>
    public static string GetCacheDirectory() => CacheDirectory;

    /// <summary>
    /// 获取头像：优先内存 → 本地文件 → 外网（并后台落盘）。
    /// </summary>
    public static ImageSource? Get(string? url, int decodeWidth = 160)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var memoryKey = BuildMemoryKey(url, decodeWidth);
        if (MemoryCache.TryGetValue(memoryKey, out var cached))
        {
            return cached;
        }

        var localPath = GetLocalPath(url);
        if (File.Exists(localPath))
        {
            var fromDisk = LoadFromFile(localPath, decodeWidth);
            MemoryCache[memoryKey] = fromDisk;
            return fromDisk;
        }

        // 远程图先不进内存：失败重试时还能重新拉取；落盘成功后由下次 Get 缓存
        QueueDownload(url, localPath);
        return LoadFromUri(url, decodeWidth);
    }

    /// <summary>
    /// 后台预下载缺失头像到本地，完成后预热内存缓存。
    /// </summary>
    public static void WarmUp(IEnumerable<string> urls, int decodeWidth = 160)
    {
        var list = urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(CacheDirectory);
        TryRemoveLegacyInstallCache();

        _ = Task.Run(async () =>
        {
            foreach (var url in list)
            {
                try
                {
                    var path = GetLocalPath(url);
                    await DownloadToFileAsync(url, path).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            _ = dispatcher.BeginInvoke(() =>
            {
                foreach (var url in list)
                {
                    try
                    {
                        Get(url, decodeWidth);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }, DispatcherPriority.Background);
        });
    }

    /// <summary>统计磁盘头像缓存占用。</summary>
    public static (int FileCount, long TotalBytes) GetDiskUsage()
    {
        try
        {
            var dir = CacheDirectory;
            if (!Directory.Exists(dir))
            {
                return (0, 0);
            }

            var files = Directory.EnumerateFiles(dir)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal))
                .ToList();

            long bytes = 0;
            foreach (var file in files)
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch
                {
                    // ignore
                }
            }

            return (files.Count, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 清空内存与磁盘头像缓存，返回删除的文件数与字节数。
    /// </summary>
    public static (int DeletedFiles, long FreedBytes) Clear()
    {
        MemoryCache.Clear();
        DownloadGates.Clear();

        var deleted = 0;
        long freed = 0;
        try
        {
            var dir = CacheDirectory;
            if (!Directory.Exists(dir))
            {
                return (0, 0);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var len = new FileInfo(file).Length;
                    File.Delete(file);
                    deleted++;
                    freed += len;
                }
                catch
                {
                    // ignore locked files
                }
            }
        }
        catch
        {
            // ignore
        }

        TryRemoveLegacyInstallCache();
        return (deleted, freed);
    }

    /// <summary>清理早期写在程序目录下的旧缓存。</summary>
    public static void TryRemoveLegacyInstallCache()
    {
        try
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "cache", "hero-icons");
            if (!Directory.Exists(legacy))
            {
                return;
            }

            Directory.Delete(legacy, recursive: true);
            var parent = Path.Combine(AppContext.BaseDirectory, "cache");
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void QueueDownload(string url, string localPath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadToFileAsync(url, localPath).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        });
    }

    private static async Task DownloadToFileAsync(string url, string localPath)
    {
        if (File.Exists(localPath))
        {
            return;
        }

        if (!DownloadGates.TryAdd(localPath, 0))
        {
            // 同一文件已在下载，短暂等待落盘
            for (var i = 0; i < 50 && !File.Exists(localPath) && DownloadGates.ContainsKey(localPath); i++)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }

            return;
        }

        try
        {
            if (File.Exists(localPath))
            {
                return;
            }

            Directory.CreateDirectory(CacheDirectory);

            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return;
            }

            var tempPath = localPath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);

            if (File.Exists(localPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
                return;
            }

            File.Move(tempPath, localPath);
        }
        finally
        {
            DownloadGates.TryRemove(localPath, out _);
        }
    }

    private static ImageSource LoadFromFile(string path, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static ImageSource LoadFromUri(string url, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(url, UriKind.Absolute);
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
        bitmap.CacheOption = BitmapCacheOption.OnDemand;
        bitmap.EndInit();
        return bitmap;
    }

    private static string GetLocalPath(string url)
        => Path.Combine(CacheDirectory, CacheFileNaming.FromUrl(url));

    private static string BuildMemoryKey(string url, int decodeWidth)
        => $"{decodeWidth}|{url}";
}
