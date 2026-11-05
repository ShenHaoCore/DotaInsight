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
    {
        // LiteDB 5 反序列化 BsonDateTime 得到的是 Kind=Local、ticks 为本地钟面的值，
        // 而本属性写入的是 UTC 钟面。DateTime 的比较运算只看 ticks 不看 Kind，
        // 直接拿 DateTime.UtcNow 比较会把过期判定错位一个时区偏移：
        // UTC+8 的机器上缓存会多活 8 小时（如「未解析比赛缓存 30 分钟」实际 8.5 小时）。
        // 存储不变量是「写入值恒为 UTC」，因此先换算回 UTC 钟面再比较（Utc/Unspecified 均安全）。
        var expireAtUtc = ExpireAtUtc.Kind == DateTimeKind.Utc
            ? ExpireAtUtc
            : ExpireAtUtc.ToUniversalTime();

        return (utcNow ?? DateTime.UtcNow) >= expireAtUtc;
    }
}
