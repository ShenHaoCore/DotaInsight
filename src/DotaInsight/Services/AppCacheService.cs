using System.IO;
using DotaInsight.Helpers;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 清理头像磁盘缓存与 LiteDB 数据缓存。
/// </summary>
public sealed class AppCacheService : IAppCacheService
{
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

    public AppCacheClearResult ClearAll()
    {
        var (deletedIcons, freedIcons) = HeroImageCache.Clear();
        var deletedEntries = _liteDb.ClearAll();

        _logger.Information(
            "已清理本地缓存 Icons={Icons} Freed={Freed} DbEntries={DbEntries}",
            deletedIcons,
            freedIcons,
            deletedEntries);

        return new AppCacheClearResult
        {
            DeletedIconFiles = deletedIcons,
            FreedIconBytes = freedIcons,
            DeletedDbEntries = deletedEntries
        };
    }
}
