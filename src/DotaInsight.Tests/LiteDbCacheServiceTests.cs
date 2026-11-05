using System.IO;
using DotaInsight.Helpers;
using DotaInsight.Services;
using LiteDB;
using Serilog;

namespace DotaInsight.Tests;

/// <summary>
/// LiteDB 缓存服务的数据完整性：TTL 过期、损坏条目自愈、清理时保留用户资产。
/// 全部走临时数据库文件，不触碰真实 %LocalAppData% 缓存。
/// </summary>
public sealed class LiteDbCacheServiceTests : IDisposable
{
    private static readonly ILogger SilentLog = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;

    public LiteDbCacheServiceTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotainsight-litedb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "cache-test.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private LiteDbCacheService CreateService() => new(SilentLog, _dbPath);

    [Fact]
    public void Set_Get_RoundTrips()
    {
        using var service = CreateService();
        var payload = new Dictionary<string, string> { ["antimage"] = "敌法师", ["axe"] = "斧王" };

        service.Set("test:roundtrip", payload);

        var restored = service.Get<Dictionary<string, string>>("test:roundtrip");
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal("敌法师", restored["antimage"]);
    }

    [Fact]
    public void Get_Expired_ReturnsNull_ButEntrySurvivesForStaleFallback()
    {
        // 契约：过期条目 Get 返回 null 但不删除 —— 它是离线降级（GetStale）的最后底料。
        // 曾在这里 Delete，导致「过期 + 断网」场景永久失去降级能力（服务层测试抓到）。
        using var service = CreateService();
        service.Set("test:ttl", new List<string> { "a" }, TimeSpan.FromHours(-1));

        Assert.Null(service.Get<List<string>>("test:ttl"));

        var stale = service.GetStale<List<string>>("test:ttl");
        Assert.NotNull(stale);
        Assert.Single(stale!);
    }

    [Fact]
    public void GetStale_ServesExpiredEntry_WhenGetHasNotRunYet()
    {
        // GetStale 的语义：网络失败时的兜底读取，在 Get（会顺手删过期条目）之前调用仍能拿到旧数据
        using var service = CreateService();
        service.Set("test:stale", new List<string> { "a" }, TimeSpan.FromHours(-1));

        var stale = service.GetStale<List<string>>("test:stale");
        Assert.NotNull(stale);
        Assert.Single(stale!);
    }

    [Fact]
    public void Get_CorruptedPayload_SelfHealsByDeletingEntry()
    {
        // 先写入正常条目
        using (var service = CreateService())
        {
            service.Set("test:corrupt", new List<string> { "ok" });
        }

        // 绕开服务层，直接把载荷替换成非法 JSON（模拟历史版本写入 / 文件级损坏）
        using (var raw = new LiteDatabase($"Filename={_dbPath};Connection=shared"))
        {
            var col = raw.GetCollection("cache");
            var doc = col.FindById("test:corrupt");
            Assert.NotNull(doc);
            doc["PayloadJson"] = "{ this is not valid json";
            col.Upsert(doc);
        }

        // 坏条目应被识别：Get 返回空而不是抛异常，且坏条目被删除（自愈）
        using (var service = CreateService())
        {
            Assert.Null(service.Get<List<string>>("test:corrupt"));
            Assert.Null(service.GetStale<List<string>>("test:corrupt"));

            // 自愈后同键可正常重新写入
            service.Set("test:corrupt", new List<string> { "recovered" });
            var restored = service.Get<List<string>>("test:corrupt");
            Assert.NotNull(restored);
            Assert.Single(restored!);
        }
    }

    [Fact]
    public void ClearAll_DeletesCacheButPreservesUserAssets()
    {
        using var service = CreateService();
        service.Set(CacheKeys.SavedAccounts, new Dictionary<string, string> { ["86745912"] = "玩家" });
        service.Set("opendota:heroStats:zh:v6", new List<string> { "cache" });
        service.Set("dota2cn:herodata:v6:14", new Dictionary<string, string>());

        var deleted = service.ClearAll();

        Assert.True(deleted >= 2);
        Assert.NotNull(service.Get<Dictionary<string, string>>(CacheKeys.SavedAccounts));
        Assert.Null(service.Get<List<string>>("opendota:heroStats:zh:v6"));
        Assert.Null(service.Get<Dictionary<string, string>>("dota2cn:herodata:v6:14"));
    }

    [Fact]
    public void SharedConnection_TwoInstances_SeeEachOthersWrites()
    {
        using (var writer = CreateService())
        {
            writer.Set("test:shared", new List<int> { 1, 2, 3 });
        }

        using var reader = CreateService();
        var value = reader.Get<List<int>>("test:shared");
        Assert.NotNull(value);
        Assert.Equal([1, 2, 3], value);
    }
}
