using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DotaInsight.Helpers;

/// <summary>
/// 远程图片磁盘缓存：%LocalAppData%\DotaInsight\image-cache\ + 内存。
/// 超大素材（如 1440×1440 头图）按 storeWidth 降采样后落盘，避免几十 MB 的无效占用。
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
            return AppPaths.ImageCache;
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

    /// <summary>
    /// 获取图片：优先内存 → 本地文件 → 外网（并后台落盘）。
    /// </summary>
    /// <param name="url">远程地址。</param>
    /// <param name="decodeWidth">内存解码宽度（按实际显示尺寸给，避免无谓的大位图）。</param>
    /// <param name="storeWidth">
    /// 落盘宽度：源图宽于此值时降采样后再存（保留 alpha 的 PNG）。
    /// 0 表示原样存（适合本身就小的头像/图标）。
    /// </param>
    public static ImageSource? Get(string? url, int decodeWidth = 160, int storeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var memoryKey = BuildMemoryKey(url, decodeWidth, storeWidth);
        if (MemoryCache.TryGetValue(memoryKey, out var cached))
        {
            return cached;
        }

        var localPath = GetLocalPath(url, storeWidth);
        if (File.Exists(localPath))
        {
            var fromDisk = LoadFromFile(localPath, decodeWidth);
            MemoryCache[memoryKey] = fromDisk;
            return fromDisk;
        }

        // 远程图先不进磁盘路径：失败重试时还能重新拉取；落盘成功后由下次 Get 走文件路径。
        // WPF 网络栈的直载位图同样进内存缓存 —— 首屏多个控件绑同一 URL 时复用同一
        // BitmapImage 实例（WPF 内部按 Uri 去重下载），不重复分配解码对象。
        QueueDownload(url, localPath, storeWidth);
        var direct = LoadFromUri(url, decodeWidth);
        MemoryCache[memoryKey] = direct;
        return direct;
    }

    /// <summary>
    /// 后台预下载缺失图片到本地，完成后预热内存缓存。
    /// </summary>
    public static void WarmUp(IEnumerable<string> urls, int decodeWidth = 160, int storeWidth = 0)
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
                    var path = GetLocalPath(url, storeWidth);
                    await DownloadToFileAsync(url, path, storeWidth).ConfigureAwait(false);
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
                        Get(url, decodeWidth, storeWidth);
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

    /// <summary>
    /// 清理早期版本的图片缓存：程序目录下的 cache/hero-icons 与
    /// %LocalAppData% 下不降采样的 hero-icons（单张可达 1.5 MB），升级后一次性回收磁盘。
    /// </summary>
    public static void TryRemoveLegacyInstallCache()
    {
        TryDeleteDirectory(Path.Combine(AppContext.BaseDirectory, "cache", "hero-icons"));
        TryDeleteDirectory(AppPaths.LegacyImageCache);

        try
        {
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignore：文件被占用时留到下次启动再清
        }
    }

    private static void QueueDownload(string url, string localPath, int storeWidth = 0)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadToFileAsync(url, localPath, storeWidth).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        });
    }

    private static async Task DownloadToFileAsync(string url, string localPath, int storeWidth = 0)
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

            if (storeWidth > 0)
            {
                // 后台线程解码 + 缩放；失败或反而更大时保留原图
                var scaled = DownscaleToPng(bytes, storeWidth);
                if (scaled is { Length: > 0 } && scaled.Length < bytes.Length)
                {
                    bytes = scaled;
                }
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

    /// <summary>
    /// 按目标宽度降采样并重新编码为 PNG（保留 alpha）。
    /// 源图不超过目标宽度、或处理失败时返回 null，表示维持原文件。
    /// RenderTargetBitmap 要求 STA 线程，故在专用线程上执行。
    /// </summary>
    private static byte[]? DownscaleToPng(byte[] source, int targetWidth)
    {
        if (targetWidth <= 0 || source.Length == 0)
        {
            return null;
        }

        byte[]? result = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var input = new MemoryStream(source);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = input;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze();

                if (bitmap.PixelWidth <= targetWidth)
                {
                    return;
                }

                var scale = targetWidth / (double)bitmap.PixelWidth;
                var width = Math.Max(1, (int)Math.Round(bitmap.PixelWidth * scale));
                var height = Math.Max(1, (int)Math.Round(bitmap.PixelHeight * scale));

                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                using (var context = visual.RenderOpen())
                {
                    context.DrawImage(bitmap, new Rect(0, 0, width, height));
                }

                var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(target));
                using var output = new MemoryStream();
                encoder.Save(output);
                result = output.ToArray();
            }
            catch
            {
                result = null;
            }
        })
        {
            IsBackground = true
        };

        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        return result;
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

    private static string GetLocalPath(string url, int storeWidth = 0)
        => Path.Combine(CacheDirectory, CacheFileNaming.FromUrl(url, storeWidth));

    private static string BuildMemoryKey(string url, int decodeWidth, int storeWidth)
        => $"{decodeWidth}|{storeWidth}|{url}";
}
