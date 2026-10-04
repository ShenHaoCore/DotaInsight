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

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
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

    /// <summary>实际耗时，用于结果页展示（删除上千张图时用户能感知到"确实干了活"）。</summary>
    public TimeSpan Elapsed { get; init; }

    public string Message =>
        $"已清理：头像 {DeletedIconFiles} 张（{AppCacheStats.FormatBytes(FreedIconBytes)}），数据条目 {DeletedDbEntries} 条";
}

/// <summary>清理阶段：进度条按阶段分段推进，文案随阶段切换。</summary>
public enum AppCacheClearStage
{
    /// <summary>统计现有缓存。</summary>
    Scanning,

    /// <summary>删除头像缓存文件（耗时主体，逐文件上报）。</summary>
    Images,

    /// <summary>清空 LiteDB 数据条目。</summary>
    Database,

    /// <summary>收尾。</summary>
    Done
}

/// <summary>清理进度快照（由后台线程上报，经 IProgress 回到 UI 线程）。</summary>
public sealed class AppCacheClearProgress
{
    public AppCacheClearStage Stage { get; init; }

    /// <summary>总体完成度 0–100。</summary>
    public double Percent { get; init; }

    /// <summary>阶段文案。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>当前阶段已处理数量（仅头像阶段有意义）。</summary>
    public int Done { get; init; }

    /// <summary>当前阶段总数量（仅头像阶段有意义）。</summary>
    public int Total { get; init; }
}

/// <summary>
/// 应用本地缓存（头像 + LiteDB）管理。
/// </summary>
public interface IAppCacheService
{
    AppCacheStats GetStats();

    /// <summary>
    /// 清理全部可再生缓存（用户资产如已保存账户不受影响）。
    /// 删除动作在后台线程执行，进度经 <paramref name="progress"/> 回到调用方线程。
    /// </summary>
    Task<AppCacheClearResult> ClearAllAsync(
        IProgress<AppCacheClearProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
