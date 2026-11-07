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
            if (TryLoadFromDisk(localPath, decodeWidth, out var fromDisk) && fromDisk is not null)
            {
                MemoryCache[memoryKey] = fromDisk;
                return fromDisk;
            }

            // 落盘文件损坏（断电残留 / 杀软截断 / 磁盘错误）时删掉坏文件，
            // 走下面的网络路径重新拉取 —— 否则坏文件会永久挡住这张图，
            // 每次进入相关页面都重复解码失败。
            TryDeleteCorruptedFile(localPath);
        }

        // 冷路径（内存与磁盘都没有）：
        // 先后台排队下载落盘；UI 线程上绝不返回 WPF 懒下载位图——那会在首次渲染时
        // 同步走网络，弱网下每张冷图都是一次界面冻结。UI 线程直接返回 null 占位，
        // 由调用方的预热/重新求值补图；非 UI 线程（IsAsync 绑定、预热流程）短暂
        // 等待落盘后走磁盘同步解码，首次就能出图。
        QueueDownload(url, localPath, storeWidth);

        if (IsOnUiThread())
        {
            return null;
        }

        // 非 UI 线程：等待后台下载落盘（上限约 3 秒），拿到文件即同步解码
        for (var i = 0; i < 30 && !File.Exists(localPath); i++)
        {
            Thread.Sleep(100);
        }

        if (TryLoadFromDisk(localPath, decodeWidth, out var downloaded) && downloaded is not null)
        {
            MemoryCache[memoryKey] = downloaded;
            return downloaded;
        }

        return null;
    }

    /// <summary>当前是否在 UI（Dispatcher）线程上。</summary>
    private static bool IsOnUiThread()
        => Application.Current?.Dispatcher.CheckAccess() ?? true;

    /// <summary>
    /// 确保 URL 的原始文件已落盘并返回本地路径（供 APNG 等需要原始字节的场景）。
    /// 已存在直接返回；否则排队下载并等待落盘（上限约 3 秒），失败返回 null。
    /// </summary>
    public static string? EnsureDownloaded(string url, int storeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var localPath = GetLocalPath(url, storeWidth);
        if (File.Exists(localPath))
        {
            return localPath;
        }

        QueueDownload(url, localPath, storeWidth);
        for (var i = 0; i < 30 && !File.Exists(localPath); i++)
        {
            Thread.Sleep(100);
        }

        return File.Exists(localPath) ? localPath : null;
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

    /// <summary>
    /// 可等待的预热：下载缺失文件（整体限时，超时放行不再等）→ 清掉内存里可能
    /// 已经中毒的懒加载位图 → 在 UI 线程把文件解码进内存缓存。
    /// 背景：冷路径原先返回 WPF 懒下载位图（DelayCreation），这条链路不可控——
    /// 失败后实例还留在 <see cref="MemoryCache"/> 里永不自愈，表现为「文件已落盘、UI 仍空白」。
    /// 调用方在下载完成后让相关绑定重新求值，即可保证走磁盘同步解码。
    /// </summary>
    public static async Task WarmUpAsync(
        IEnumerable<string> urls,
        int decodeWidth = 160,
        TimeSpan? maxWait = null)
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

        try
        {
            await Task.WhenAll(list.Select(url => DownloadToFileAsync(url, GetLocalPath(url, 0), 0)))
                .WaitAsync(maxWait ?? TimeSpan.FromSeconds(4))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 个别慢文件不阻塞调用方；已完成的文件仍然有效
        }
        catch
        {
            // 下载失败交由后续重试
        }

        EvictMemoryForUrls(list);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            foreach (var url in list)
            {
                try
                {
                    Get(url, decodeWidth, 0);
                }
                catch
                {
                    // ignore
                }
            }
        }).Task.ConfigureAwait(false);
    }

    /// <summary>按 URL 清掉内存缓存条目（键形如 "{decodeWidth}|{storeWidth}|{url}"）。</summary>
    internal static void EvictMemoryForUrls(IEnumerable<string> urls)
    {
        var urlSet = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (urlSet.Count == 0)
        {
            return;
        }

        foreach (var key in MemoryCache.Keys)
        {
            var separator = key.LastIndexOf('|');
            if (separator >= 0 && urlSet.Contains(key[(separator + 1)..]))
            {
                MemoryCache.TryRemove(key, out _);
            }
        }
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
    /// <param name="onProgress">
    /// 每删除若干文件回调一次 (已完成, 总数)。总数在执行前一次性枚举得到，
    /// 因此回调方可以据此算出稳定的百分比。
    /// </param>
    public static (int DeletedFiles, long FreedBytes) Clear(Action<int, int>? onProgress = null)
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

            // 先枚举出总数：边删边枚举拿不到稳定的分母，进度条会一直停在 0%
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
            }
            catch
            {
                return (0, 0);
            }

            onProgress?.Invoke(0, files.Count);

            foreach (var file in files)
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

                onProgress?.Invoke(deleted, files.Count);
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

    /// <summary>
    /// 尝试从磁盘文件解码位图。文件损坏（断电残留 / 杀软截断 / 磁盘错误）时返回 false 而不抛出，
    /// 由调用方删除坏文件后走网络路径重新拉取。拆成独立方法便于对「损坏文件」做单元测试。
    /// </summary>
    internal static bool TryLoadFromDisk(string path, int decodeWidth, out ImageSource? image)
    {
        try
        {
            image = LoadFromFile(path, decodeWidth);
            return true;
        }
        catch
        {
            image = null;
            return false;
        }
    }

    private static void TryDeleteCorruptedFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignore：被占用时删不掉，下次 Get 再试
        }
    }

    private static string GetLocalPath(string url, int storeWidth = 0)
        => Path.Combine(CacheDirectory, CacheFileNaming.FromUrl(url, storeWidth));

    private static string BuildMemoryKey(string url, int decodeWidth, int storeWidth)
        => $"{decodeWidth}|{storeWidth}|{url}";
}
