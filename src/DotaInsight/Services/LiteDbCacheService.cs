using System.IO;
using System.Text.Json;
using DotaInsight.Helpers;
using DotaInsight.Models;
using LiteDB;
using Serilog;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace DotaInsight.Services;

/// <summary>
/// 基于 LiteDB 的本地缓存服务，默认 TTL 24 小时。
/// </summary>
public sealed class LiteDbCacheService : ILiteDbCacheService, IDisposable
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly ILiteDatabase _database;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    public LiteDbCacheService(ILogger logger)
    {
        _logger = logger.ForContext<LiteDbCacheService>();

        AppPaths.EnsureCreated();
        var dbPath = AppPaths.Database;
        // Shared：允许多进程只读/错开写入，避免第二个实例启动即崩溃
        _database = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _logger.Information("LiteDB 缓存已打开：{DbPath}", dbPath);
    }

    public T? Get<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            var entry = Collection().FindById(key);
            if (entry is null)
            {
                return default;
            }

            if (entry.IsExpired())
            {
                _logger.Debug("缓存已过期：{Key}", key);
                Collection().Delete(key);
                return default;
            }

            return Deserialize<T>(entry.PayloadJson, key);
        }
    }

    public T? GetStale<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            var entry = Collection().FindById(key);
            if (entry is null)
            {
                return default;
            }

            _logger.Debug("读取可能过期的缓存：{Key}, Expired={Expired}", key, entry.IsExpired());
            return Deserialize<T>(entry.PayloadJson, key);
        }
    }

    public void Set<T>(string key, T value, TimeSpan? ttl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var now = DateTime.UtcNow;
        var entry = new CacheEntry
        {
            Id = key,
            PayloadJson = JsonSerializer.Serialize(value, JsonOptions),
            CreatedAtUtc = now,
            ExpireAtUtc = now.Add(ttl ?? DefaultTtl)
        };

        lock (_sync)
        {
            Collection().Upsert(entry);
        }

        _logger.Debug("已写入缓存：{Key}, ExpireAt={ExpireAt}", key, entry.ExpireAtUtc);
    }

    public int ClearAll()
    {
        lock (_sync)
        {
            var deleted = Collection().DeleteAll();
            _logger.Information("已清空 LiteDB 缓存条目：{Count}", deleted);
            return deleted;
        }
    }

    private ILiteCollection<CacheEntry> Collection()
        => _database.GetCollection<CacheEntry>("cache");

    private T? Deserialize<T>(string json, string key)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "缓存反序列化失败，删除损坏条目 Key={Key}", key);
            try
            {
                Collection().Delete(key);
            }
            catch (Exception deleteEx)
            {
                _logger.Warning(deleteEx, "删除损坏缓存失败 Key={Key}", key);
            }

            return default;
        }
    }

    public void Dispose()
    {
        _database.Dispose();
    }
}
