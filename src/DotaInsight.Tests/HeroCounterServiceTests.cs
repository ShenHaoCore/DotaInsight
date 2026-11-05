using System.IO;
using System.Net;
using System.Net.Http;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.Tests;

/// <summary>
/// 克制服务：条目构建（噪声过滤/胜率分档/排序）、heroStats 归一化，
/// 以及 对位数据 缓存 → 网络 → 过期缓存降级 的完整链路（桩 HTTP，不触真网）。
/// </summary>
public class HeroCounterServiceTests : IDisposable
{
    private static readonly ILogger SilentLog = new LoggerConfiguration().CreateLogger();

    private readonly string _dir;

    public HeroCounterServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dotainsight-counter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static HeroStat Hero(int id, string name, string zh)
        => new() { Id = id, Name = name, LocalizedName = zh };

    private static Dictionary<int, HeroStat> Lookup(params HeroStat[] heroes)
        => heroes.ToDictionary(h => h.Id);

    // ---------- BuildCounterItems ----------

    [Fact]
    public void BuildCounterItems_SplitsAndFiltersNoise()
    {
        var lookup = Lookup(Hero(2, "npc_axe", "斧王"), Hero(7, "npc_puck", "帕克"), Hero(9, "npc_shaman", "暗影萨满"));
        var matchups = new List<HeroMatchupDto>
        {
            new() { HeroId = 2, GamesPlayed = 100, Wins = 30 },  // 30% → 被克制
            new() { HeroId = 7, GamesPlayed = 50, Wins = 25 },   // 50% → 噪声剔除
            new() { HeroId = 9, GamesPlayed = 40, Wins = 36 },   // 90% → 克制
            new() { HeroId = 5, GamesPlayed = 0, Wins = 0 }      // 无样本剔除
        };

        var items = HeroCounterService.BuildCounterItems(matchups, lookup);

        Assert.Equal(2, items.Count);
        var counteredBy = items.First(x => !x.IsCountering);
        var counters = items.First(x => x.IsCountering);
        Assert.Equal("斧王", counteredBy.HeroName);
        Assert.Equal(30, counteredBy.WinRate, 6);
        Assert.Equal(-20, counteredBy.WinRateDiff, 6);
        Assert.Equal("暗影萨满", counters.HeroName);
        Assert.Equal(90, counters.WinRate, 6);
    }

    [Fact]
    public void BuildCounterItems_UnknownHeroSkipped()
    {
        var lookup = Lookup(Hero(2, "npc_axe", "斧王"));
        var matchups = new List<HeroMatchupDto> { new() { HeroId = 999, GamesPlayed = 10, Wins = 5 } };

        Assert.Empty(HeroCounterService.BuildCounterItems(matchups, lookup));
    }

    // ---------- NormalizeHeroStats ----------

    [Fact]
    public void NormalizeHeroStats_AggregatesBracketsAndLocalizes()
    {
        var heroes = new List<HeroStat>
        {
            new()
            {
                Id = 1,
                Name = "npc_dota_hero_antimage",
                LocalizedName = "antimage",
                Pick1 = 100, Pick2 = 50,
                Win1 = 55, Win2 = 20
            },
            new() { Id = 2, Name = "npc_dota_hero_axe", LocalizedName = "axe" }
        };

        var normalized = HeroCounterService.NormalizeHeroStats(heroes, new Dictionary<int, string>());

        var am = normalized[0];
        Assert.Equal(150, am.Matches);
        Assert.Equal(50, am.WinRate, 6);          // (55+20)/150 = 50%
        Assert.Equal(100 * 150.0 / 150, am.PickRate, 6); // 总场次=150，全占
        Assert.Equal(8, am.Brackets.Count);
        Assert.Equal(100, am.Brackets[0].Picks);
        // 原始分段字段消费后清零，不进缓存
        Assert.Equal(0, am.Pick1);
        Assert.Equal(0, am.Win2);
        // 内置表中文名兜底（1 = 敌法师）
        Assert.Equal("敌法师", am.LocalizedName);
        Assert.Equal(0, normalized[1].Matches);
    }

    [Fact]
    public void NormalizeHeroStats_EmptyList_PickRateZero()
    {
        var normalized = HeroCounterService.NormalizeHeroStats(
            [new HeroStat { Id = 3, Name = "npc_x", LocalizedName = "x" }],
            new Dictionary<int, string>());

        Assert.Equal(0, normalized[0].PickRate, 6);
        Assert.Equal(0, normalized[0].WinRate, 6);
    }

    // ---------- GetCounterRelationsAsync：缓存 → 网络 → 过期缓存 ----------

    private sealed class StubHandler(HttpResponseMessage? response) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(response ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            // 与真实客户端一致：服务层用的是相对 URI（api/heroes/{id}/matchups）
            BaseAddress = new Uri("https://api.opendota.com/")
        };
    }

    private sealed class StubLocalization : IHeroLocalizationService
    {
        public Task<IReadOnlyDictionary<int, string>> GetChineseNamesByIdAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<int, string>>(HeroChineseNames.ById);
    }

    private static IHeroCounterService CreateService(
        string dbPath,
        HttpResponseMessage? matchupsResponse)
    {
        return new HeroCounterService(
            new StubHttpClientFactory(new StubHandler(matchupsResponse)),
            new LiteDbCacheService(SilentLog, dbPath),
            new StubLocalization(),
            SilentLog);
    }

    [Fact]
    public async Task GetCounterRelationsAsync_NetworkThenCacheThenStaleFallback()
    {
        var dbPath = Path.Combine(_dir, "counter.db");
        var matchupsJson = """[{"hero_id":2,"games_played":100,"wins":30}]""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(matchupsJson, System.Text.Encoding.UTF8, "application/json")
        };

        // 预置英雄列表缓存（不打 heroStats 网络）
        using (var seeder = new LiteDbCacheService(SilentLog, dbPath))
        {
            seeder.Set("opendota:heroStats:zh:v6", new List<HeroStat>
            {
                Hero(1, "npc_dota_hero_antimage", "敌法师"),
                Hero(2, "npc_dota_hero_axe", "斧王")
            });
        }

        var service = CreateService(dbPath, response);

        // 第一次：走网络拉对位
        var first = await service.GetCounterRelationsAsync(1);
        Assert.False(first.FromCache);
        Assert.False(first.IsOffline);
        var counteredBy = Assert.Single(first.CounteredBy);
        Assert.Equal("斧王", counteredBy.HeroName);

        // 第二次：命中 LiteDB 缓存，请求数不增长
        var second = await service.GetCounterRelationsAsync(1);
        Assert.True(second.FromCache);
        Assert.False(second.IsOffline);

        // 场景三：对位条目已过期 + 网络不可用 → 过期缓存兜底，标记离线
        // （未过期时 Get 在进网络前就命中缓存，离线路径根本不会走到）
        var stalePath = Path.Combine(_dir, "counter-stale.db");
        using (var seeder = new LiteDbCacheService(SilentLog, stalePath))
        {
            seeder.Set("opendota:heroStats:zh:v6", new List<HeroStat>
            {
                Hero(1, "npc_dota_hero_antimage", "敌法师"),
                Hero(2, "npc_dota_hero_axe", "斧王")
            });
            seeder.Set(
                "opendota:matchups:1",
                new List<HeroMatchupDto> { new() { HeroId = 2, GamesPlayed = 100, Wins = 30 } },
                TimeSpan.FromHours(-1));
        }

        var offlineService = CreateService(stalePath, null);
        var third = await offlineService.GetCounterRelationsAsync(1);
        Assert.True(third.FromCache);
        Assert.True(third.IsOffline);
        Assert.Single(third.CounteredBy);
    }

    [Fact]
    public async Task GetCounterRelationsAsync_UnknownHero_ReturnsEmptyResult()
    {
        var dbPath = Path.Combine(_dir, "counter-unknown.db");
        using (var seeder = new LiteDbCacheService(SilentLog, dbPath))
        {
            seeder.Set("opendota:heroStats:zh:v6", new List<HeroStat>
            {
                Hero(1, "npc_dota_hero_antimage", "敌法师")
            });
        }

        var service = CreateService(dbPath, null);
        var result = await service.GetCounterRelationsAsync(999);

        Assert.True(result.IsEmpty);
        Assert.Equal("#999", result.SelectedHeroName);
    }
}
