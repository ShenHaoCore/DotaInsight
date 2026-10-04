using System.Diagnostics;
using System.IO;
using DotaInsight.Helpers;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 清理头像磁盘缓存与 LiteDB 数据缓存。
/// </summary>
public sealed class AppCacheService : IAppCacheService
{
    // 进度分段预算：统计 2→4%、头像删除 4→90%、数据条目 90→96%、收尾 100%。
    // 头像删除是耗时主体（动辄上千个文件），所以给它最大的一段。
    private const double ScanPercent = 4;
    private const double ImageStartPercent = 4;
    private const double ImageEndPercent = 90;
    private const double DatabasePercent = 96;

    private readonly ILiteDbCacheService _liteDb;
    private readonly ILogger _logger;

    public AppCacheService(ILiteDbCacheService liteDb, ILogger logger)
    {
        _liteDb = liteDb;
        _logger = logger.ForContext<AppCacheService>();
    }

    public AppCacheStats GetStats()
    {
        var (files, iconBytes) = HeroImageCache.GetDiskUsage();

        long dbBytes = 0;
        try
        {
            if (File.Exists(AppPaths.Database))
            {
                dbBytes = new FileInfo(AppPaths.Database).Length;
            }
        }
        catch
        {
            // ignore
        }

        return new AppCacheStats
        {
            HeroIconFiles = files,
            HeroIconBytes = iconBytes,
            DatabaseBytes = dbBytes
        };
    }

    public Task<AppCacheClearResult> ClearAllAsync(
        IProgress<AppCacheClearProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ClearCore(progress, cancellationToken), cancellationToken);

    private AppCacheClearResult ClearCore(
        IProgress<AppCacheClearProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        progress?.Report(new AppCacheClearProgress
        {
            Stage = AppCacheClearStage.Scanning,
            Percent = 2,
            Message = "正在统计缓存…"
        });

        var stats = GetStats();

        progress?.Report(new AppCacheClearProgress
        {
            Stage = AppCacheClearStage.Scanning,
            Percent = ScanPercent,
            Message = "正在统计缓存…",
            Done = stats.HeroIconFiles,
            Total = stats.HeroIconFiles
        });

        cancellationToken.ThrowIfCancellationRequested();

        // 上千个文件逐个上报会把 UI 线程的消息队列灌满，按 1% 粒度合并（末次不合并）
        var lastReported = -1;
        var (deletedIcons, freedIcons) = HeroImageCache.Clear((done, total) =>
        {
            var percent = ImageStartPercent
                          + (ImageEndPercent - ImageStartPercent) * (total <= 0 ? 1 : done / (double)total);
            var bucket = (int)percent;
            if (bucket == lastReported && done < total)
            {
                return;
            }

            lastReported = bucket;
            progress?.Report(new AppCacheClearProgress
            {
                Stage = AppCacheClearStage.Images,
                Percent = percent,
                Message = "正在清理头像缓存…",
                Done = done,
                Total = total
            });
        });

        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new AppCacheClearProgress
        {
            Stage = AppCacheClearStage.Database,
            Percent = DatabasePercent,
            Message = "正在清理数据缓存…"
        });

        var deletedEntries = _liteDb.ClearAll();

        stopwatch.Stop();

        progress?.Report(new AppCacheClearProgress
        {
            Stage = AppCacheClearStage.Done,
            Percent = 100,
            Message = "清理完成"
        });

        _logger.Information(
            "已清理本地缓存 Icons={Icons} Freed={Freed} DbEntries={DbEntries} Elapsed={Elapsed}ms",
            deletedIcons,
            freedIcons,
            deletedEntries,
            stopwatch.ElapsedMilliseconds);

        return new AppCacheClearResult
        {
            DeletedIconFiles = deletedIcons,
            FreedIconBytes = freedIcons,
            DeletedDbEntries = deletedEntries,
            Elapsed = stopwatch.Elapsed
        };
    }
}
