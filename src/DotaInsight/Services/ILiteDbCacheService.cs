using DotaInsight.Models;

namespace DotaInsight.Services;

/// <summary>
/// LiteDB 本地缓存抽象。
/// </summary>
public interface ILiteDbCacheService
{
    /// <summary>读取未过期缓存；过期或不存在返回 null。</summary>
    T? Get<T>(string key);

    /// <summary>写入缓存，默认 TTL 24 小时。</summary>
    void Set<T>(string key, T value, TimeSpan? ttl = null);

    /// <summary>读取缓存（即使已过期），用于离线降级。</summary>
    T? GetStale<T>(string key);

    /// <summary>清空全部数据缓存条目，返回删除条数。</summary>
    int ClearAll();
}
