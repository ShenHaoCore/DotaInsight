namespace DotaInsight.Models;

/// <summary>
/// LiteDB 通用缓存条目（含 TTL）。仅缓存基础设施内部使用。
/// </summary>
internal sealed class CacheEntry
{
    public string Id { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpireAtUtc { get; set; }

    public bool IsExpired(DateTime? utcNow = null)
        => (utcNow ?? DateTime.UtcNow) >= ExpireAtUtc;
}
