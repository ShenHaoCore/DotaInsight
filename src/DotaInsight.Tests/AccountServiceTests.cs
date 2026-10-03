using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;
using Serilog.Core;

namespace DotaInsight.Tests;

/// <summary>
/// 已保存账户服务：保存 / 更新 / 删除 / 旧历史迁移的规则。
/// 用内存缓存代替 LiteDB，避免测试触碰用户的真实 cache.db。
/// </summary>
public class AccountServiceTests
{
    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();

    private sealed class InMemoryCache : ILiteDbCacheService
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public T? Get<T>(string key)
            => _store.TryGetValue(key, out var json)
                ? System.Text.Json.JsonSerializer.Deserialize<T>(json)
                : default;

        public T? GetStale<T>(string key) => Get<T>(key);

        public void Set<T>(string key, T value, TimeSpan? ttl = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            _store[key] = System.Text.Json.JsonSerializer.Serialize(value);
        }

        public int ClearAll()
        {
            var count = _store.Count;
            _store.Clear();
            return count;
        }
    }

    private static AccountService CreateService(out InMemoryCache cache)
    {
        cache = new InMemoryCache();
        return new AccountService(cache, SilentLogger);
    }

    [Fact]
    public void SaveOrUpdate_AddsAccountAndMarksCurrent()
    {
        var service = CreateService(out _);

        service.SaveOrUpdate(new PlayerProfile
        {
            AccountId = 1111506,
            PersonaName = "LineDetail.com",
            AvatarUrl = "https://avatars.steamstatic.com/a_full.jpg",
            RankTier = 85,
            Wins = 3200,
            Losses = 2980
        });

        var all = service.GetAll();
        var account = Assert.Single(all);
        Assert.Equal(1111506, account.AccountId);
        Assert.Equal("LineDetail.com", account.PersonaName);
        Assert.True(account.IsCurrent);
        Assert.True(account.HasProfile);
        Assert.Equal(1111506, service.CurrentAccountId);
        Assert.Equal("冠绝 5", account.RankText);
    }

    [Fact]
    public void SaveOrUpdate_DegradedProfileKeepsExistingInfo()
    {
        // 离线降级时 profile 可能只有 Id：不得把已存的昵称与头像抹成空
        var service = CreateService(out _);

        service.SaveOrUpdate(new PlayerProfile
        {
            AccountId = 1111506,
            PersonaName = "LineDetail.com",
            AvatarUrl = "https://avatars.steamstatic.com/a_full.jpg",
            RankTier = 85
        });

        service.SaveOrUpdate(new PlayerProfile
        {
            AccountId = 1111506,
            PersonaName = "",
            AvatarUrl = ""
        });

        var account = Assert.Single(service.GetAll());
        Assert.Equal("LineDetail.com", account.PersonaName);
        Assert.NotEmpty(account.AvatarUrl);
        Assert.Equal(85, account.RankTier);
    }

    [Fact]
    public void SaveOrUpdate_TrimsOldestWhenOverLimit()
    {
        var service = CreateService(out _);

        for (var i = 0; i < 25; i++)
        {
            service.SaveOrUpdate(new PlayerProfile { AccountId = 1000 + i });
        }

        var all = service.GetAll();
        Assert.Equal(20, all.Count);
        // 最新的在最前，最旧的 1000~1004 被淘汰
        Assert.Equal(1024, all[0].AccountId);
        Assert.DoesNotContain(all, a => a.AccountId < 1005);
    }

    [Fact]
    public void MigrateLegacyIds_IsIdempotentAndSupportsSteamId64()
    {
        var service = CreateService(out _);

        // SteamID64 与 32 位 ID 指向同一账号：迁移不应产生重复条目
        Assert.Equal(1, service.MigrateLegacyIds(["1111506"]));
        Assert.Equal(0, service.MigrateLegacyIds(["1111506"]));
        Assert.Equal(0, service.MigrateLegacyIds([
            SteamAccountId.SteamId64Base + 1111506 + ""
        ]));

        var account = Assert.Single(service.GetAll());
        Assert.Equal(1111506, account.AccountId);
        Assert.False(account.HasProfile);
    }

    [Fact]
    public void MigrateLegacyIds_IgnoresInvalidInput()
    {
        var service = CreateService(out _);

        Assert.Equal(0, service.MigrateLegacyIds(["", "abc", "-5", "0"]));
        Assert.Empty(service.GetAll());
    }

    [Fact]
    public void Remove_ClearsCurrentWhenRemovingCurrentAccount()
    {
        var service = CreateService(out _);

        service.SaveOrUpdate(new PlayerProfile { AccountId = 1111506 });
        service.SaveOrUpdate(new PlayerProfile { AccountId = 1296625 });
        Assert.Equal(1296625, service.CurrentAccountId);

        Assert.True(service.Remove(1296625));
        Assert.Null(service.CurrentAccountId);

        var remaining = service.GetAll();
        var account = Assert.Single(remaining);
        Assert.Equal(1111506, account.AccountId);
        Assert.False(account.IsCurrent);

        Assert.False(service.Remove(1296625));
    }

    [Fact]
    public void UserAssetKeys_AreProtectedFromCacheClear()
    {
        // 清缓存时必须保留的键：前缀约定（LiteDbCacheService.ClearAll 依赖它）
        Assert.True(CacheKeys.IsUserAsset(CacheKeys.SavedAccounts));
        Assert.True(CacheKeys.IsUserAsset("saved_accounts_x"));
        Assert.False(CacheKeys.IsUserAsset("match_history_ids"));
        Assert.False(CacheKeys.IsUserAsset("opendota:matchAnalysis:v1:1"));
    }
}
