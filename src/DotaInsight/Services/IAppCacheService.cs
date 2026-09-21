namespace DotaInsight.Services;



/// <summary>

/// 本地缓存统计。

/// </summary>

public sealed class AppCacheStats

{

    public int HeroIconFiles { get; init; }



    public long HeroIconBytes { get; init; }



    public long DatabaseBytes { get; init; }



    public long TotalBytes => HeroIconBytes + DatabaseBytes;



    public string SummaryText

    {

        get

        {

            if (TotalBytes <= 0 && HeroIconFiles <= 0)

            {

                return "本地缓存为空";

            }



            return $"缓存 {FormatBytes(TotalBytes)}（头像 {HeroIconFiles} 张 / {FormatBytes(HeroIconBytes)} · 数据 {FormatBytes(DatabaseBytes)}）";

        }

    }



    public static string FormatBytes(long bytes)

    {

        if (bytes < 1024)

        {

            return $"{bytes} B";

        }



        if (bytes < 1024 * 1024)

        {

            return $"{bytes / 1024.0:F1} KB";

        }



        return $"{bytes / (1024.0 * 1024):F1} MB";

    }

}



/// <summary>

/// 本地缓存清理结果。

/// </summary>

public sealed class AppCacheClearResult

{

    public int DeletedIconFiles { get; init; }



    public long FreedIconBytes { get; init; }



    public int DeletedDbEntries { get; init; }



    public string Message =>

        $"已清理：头像 {DeletedIconFiles} 张（{AppCacheStats.FormatBytes(FreedIconBytes)}），数据条目 {DeletedDbEntries} 条";

}



/// <summary>

/// 应用本地缓存（头像 + LiteDB）管理。

/// </summary>

public interface IAppCacheService

{

    AppCacheStats GetStats();



    AppCacheClearResult ClearAll();

}


