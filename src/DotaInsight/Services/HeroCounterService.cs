using System.Net.Http;
using System.Net.Http.Json;
using DotaInsight.Helpers;
using DotaInsight.Models;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 英雄克制关系服务：
/// - 英雄列表：OpenDota GET /api/heroStats + LiteDB 缓存（TTL 24h）
/// - 中文名：国服官方接口 / 内置表覆盖 LocalizedName
/// - 对位数据：OpenDota GET /api/heroes/{id}/matchups（heroStats 不含对位，需此接口才能算克制）
/// - 网络：由 DI 注入的 HttpClient（已配置 Polly 最多重试 2 次）
/// </summary>
public sealed class HeroCounterService : IHeroCounterService
{
    public const string HttpClientName = "opendota";

    // 版本号变更可强制刷新旧缓存（v5：派生字段可写入缓存；分段原始字段并入本模型）
    private const string HeroStatsCacheKey = "opendota:heroStats:zh:v5";
    private const string MatchupsCacheKeyPrefix = "opendota:matchups:";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly IHeroLocalizationService _localization;
    private readonly ILogger _logger;

    public HeroCounterService(
        IHttpClientFactory httpClientFactory,
        ILiteDbCacheService cache,
        IHeroLocalizationService localization,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _localization = localization;
        _logger = logger.ForContext<HeroCounterService>();
    }

    private HttpClient Http => _httpClientFactory.CreateClient(HttpClientName);

    public async Task<IReadOnlyList<HeroStat>> GetHeroesAsync(CancellationToken cancellationToken = default)
    {
        var chineseNames = await _localization
            .GetChineseNamesByIdAsync(cancellationToken)
            .ConfigureAwait(false);

        var cached = _cache.Get<List<HeroStat>>(HeroStatsCacheKey);
        if (cached is { Count: > 0 })
        {
            ApplyChineseNames(cached, chineseNames);
            _logger.Information("从缓存加载英雄列表，共 {Count} 个", cached.Count);
            return cached;
        }

        try
        {
            _logger.Information("请求 OpenDota /api/heroStats");
            var heroes = await Http
                .GetFromJsonAsync<List<HeroStat>>(
                    "api/heroStats",
                    HttpCall.JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (heroes is null || heroes.Count == 0)
            {
                _logger.Warning("heroStats 返回空数据，尝试使用过期缓存");
                var staleEmpty = _cache.GetStale<List<HeroStat>>(HeroStatsCacheKey) ?? [];
                ApplyChineseNames(staleEmpty, chineseNames);
                return staleEmpty;
            }

            var normalized = NormalizeHeroStats(heroes, chineseNames);
            _cache.Set(HeroStatsCacheKey, normalized);
            _logger.Information("已刷新英雄列表缓存，共 {Count} 个", normalized.Count);
            return normalized;
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取 heroStats 失败，尝试离线缓存");
            var stale = _cache.GetStale<List<HeroStat>>(HeroStatsCacheKey);
            if (stale is { Count: > 0 })
            {
                ApplyChineseNames(stale, chineseNames);
                return stale;
            }

            throw;
        }
    }

    public async Task<HeroCounterResult> GetCounterRelationsAsync(
        int heroId,
        CancellationToken cancellationToken = default)
    {
        var heroes = await GetHeroesAsync(cancellationToken).ConfigureAwait(false);
        var selected = heroes.FirstOrDefault(h => h.Id == heroId);
        if (selected is null)
        {
            _logger.Warning("未找到英雄 Id={HeroId}", heroId);
            return new HeroCounterResult
            {
                SelectedHeroId = heroId,
                SelectedHeroName = $"#{heroId}"
            };
        }

        var heroLookup = heroes.ToDictionary(h => h.Id);
        var cacheKey = MatchupsCacheKeyPrefix + heroId;
        var (matchups, fromCache, isOffline) = await LoadMatchupsAsync(
            heroId,
            cacheKey,
            cancellationToken).ConfigureAwait(false);

        var items = BuildCounterItems(matchups, heroLookup);

        // 被克制：己方胜率 < 50%，按胜率升序（最劣势在前）
        var counteredBy = items
            .Where(x => !x.IsCountering)
            .OrderBy(x => x.WinRate)
            .ThenByDescending(x => x.Matches)
            .ToList();

        // 克制对方：己方胜率 > 50%，按胜率降序
        var counters = items
            .Where(x => x.IsCountering)
            .OrderByDescending(x => x.WinRate)
            .ThenByDescending(x => x.Matches)
            .ToList();

        _logger.Information(
            "克制分析完成 Hero={Hero} CounteredBy={CounteredBy} Counters={Counters} FromCache={FromCache} Offline={Offline}",
            selected.DisplayName,
            counteredBy.Count,
            counters.Count,
            fromCache,
            isOffline);

        return new HeroCounterResult
        {
            SelectedHeroId = selected.Id,
            SelectedHeroName = selected.DisplayName,
            CounteredBy = counteredBy,
            Counters = counters,
            FromCache = fromCache,
            IsOffline = isOffline
        };
    }

    /// <summary>
    /// 加载对位数据：缓存 → 网络 → 过期缓存（离线降级）。
    /// 返回标志：FromCache 表示来自本地、IsOffline 表示网络失败后的降级。
    /// </summary>
    private async Task<(List<HeroMatchupDto> Matchups, bool FromCache, bool IsOffline)> LoadMatchupsAsync(
        int heroId,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var cached = _cache.Get<List<HeroMatchupDto>>(cacheKey);
        if (cached is { Count: > 0 })
        {
            _logger.Information("从缓存加载对位数据 HeroId={HeroId}, Count={Count}", heroId, cached.Count);
            return (cached, FromCache: true, IsOffline: false);
        }

        try
        {
            _logger.Information("请求 OpenDota /api/heroes/{HeroId}/matchups", heroId);
            var fresh = await Http
                .GetFromJsonAsync<List<HeroMatchupDto>>(
                    $"api/heroes/{heroId}/matchups",
                    HttpCall.JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (fresh is { Count: > 0 })
            {
                _cache.Set(cacheKey, fresh);
                return (fresh, FromCache: false, IsOffline: false);
            }

            _logger.Warning("matchups 返回空，HeroId={HeroId}", heroId);
            return FallbackStaleMatchups(cacheKey);
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取 matchups 失败，HeroId={HeroId}，尝试离线缓存", heroId);
            return FallbackStaleMatchups(cacheKey);
        }
    }

    /// <summary>网络不可用或返回空时，统一走过期缓存兜底。</summary>
    private (List<HeroMatchupDto> Matchups, bool FromCache, bool IsOffline) FallbackStaleMatchups(
        string cacheKey)
    {
        var stale = _cache.GetStale<List<HeroMatchupDto>>(cacheKey) ?? [];
        var hasStale = stale.Count > 0;
        return (stale, FromCache: hasStale, IsOffline: hasStale);
    }

    private static List<HeroCounterItem> BuildCounterItems(
        IEnumerable<HeroMatchupDto> matchups,
        IReadOnlyDictionary<int, HeroStat> heroLookup)
    {
        var result = new List<HeroCounterItem>();

        foreach (var m in matchups)
        {
            if (m.GamesPlayed <= 0)
            {
                continue;
            }

            if (!heroLookup.TryGetValue(m.HeroId, out var enemy))
            {
                continue;
            }

            var winRate = m.Wins * 100.0 / m.GamesPlayed;
            var diff = winRate - 50.0;

            // 胜率刚好 50% 不计入克制/被克制，避免噪声
            if (Math.Abs(diff) < 0.01)
            {
                continue;
            }

            result.Add(new HeroCounterItem
            {
                HeroId = enemy.Id,
                HeroName = enemy.DisplayName,
                InternalName = enemy.Name,
                PrimaryAttr = enemy.PrimaryAttr,
                WinRate = winRate,
                WinRateDiff = diff,
                Matches = m.GamesPlayed,
                IsCountering = winRate > 50.0
            });
        }

        return result;
    }

    /// <summary>
    /// 就地归一化 heroStats：计算分段/胜率/选取率、应用中文名与中文枚举。
    /// </summary>
    private static List<HeroStat> NormalizeHeroStats(
        List<HeroStat> heroes,
        IReadOnlyDictionary<int, string> chineseNames)
    {
        var totalPicks = 0L;
        var picksByHero = new long[heroes.Count];

        for (var i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];

            // 大众分段聚合优先；无数据时回退 pub / pro
            var picks = SumBracketPicks(hero);
            var wins = SumBracketWins(hero);
            if (picks <= 0)
            {
                picks = hero.PubPick > 0 ? hero.PubPick : hero.ProPick;
                wins = hero.PubPick > 0 ? hero.PubWin : hero.ProWin;
            }

            var fallbackName = !string.IsNullOrWhiteSpace(hero.LocalizedName)
                ? hero.LocalizedName
                : !string.IsNullOrWhiteSpace(hero.Name)
                    ? hero.Name
                    : $"#{hero.Id}";

            hero.LocalizedName = ResolveChineseName(hero.Id, hero.Name, chineseNames) ?? fallbackName;
            hero.PrimaryAttr = HeroDisplayHelper.ToChinesePrimaryAttr(hero.PrimaryAttr);
            hero.AttackType = HeroDisplayHelper.ToChineseAttackType(hero.AttackType);
            hero.Roles = HeroDisplayHelper.ToChineseRoles(hero.Roles);
            hero.Brackets = BuildBrackets(hero);
            hero.Matches = (int)Math.Min(int.MaxValue, picks);
            hero.WinRate = picks > 0 ? wins * 100.0 / picks : 0;

            // 分段原始字段已消费，清零后不进入缓存
            ResetBracketRaw(hero);

            picksByHero[i] = picks;
            totalPicks += picks;
        }

        for (var i = 0; i < heroes.Count; i++)
        {
            heroes[i].PickRate = totalPicks > 0 ? picksByHero[i] * 100.0 / totalPicks : 0;
        }

        return heroes
            .OrderBy(h => h.DisplayName, HeroDisplayHelper.ChineseNameComparer)
            .ToList();
    }

    /// <summary>
    /// 对已有列表覆盖中文名与主属性中文（兼容缓存数据）。
    /// </summary>
    private static void ApplyChineseNames(
        IList<HeroStat> heroes,
        IReadOnlyDictionary<int, string> chineseNames)
    {
        foreach (var hero in heroes)
        {
            var zh = ResolveChineseName(hero.Id, hero.Name, chineseNames);
            if (!string.IsNullOrWhiteSpace(zh))
            {
                hero.LocalizedName = zh;
            }

            hero.PrimaryAttr = HeroDisplayHelper.ToChinesePrimaryAttr(hero.PrimaryAttr);
            hero.AttackType = HeroDisplayHelper.ToChineseAttackType(hero.AttackType);
            hero.Roles = HeroDisplayHelper.ToChineseRoles(hero.Roles);
        }
    }

    private static List<HeroBracketStat> BuildBrackets(HeroStat item)
    {
        var picks = new[] { item.Pick1, item.Pick2, item.Pick3, item.Pick4, item.Pick5, item.Pick6, item.Pick7, item.Pick8 };
        var wins = new[] { item.Win1, item.Win2, item.Win3, item.Win4, item.Win5, item.Win6, item.Win7, item.Win8 };

        var list = new List<HeroBracketStat>(8);
        for (var i = 0; i < 8; i++)
        {
            list.Add(new HeroBracketStat
            {
                Name = HeroDisplayHelper.GetBracketName(i + 1),
                Picks = picks[i],
                Wins = wins[i]
            });
        }

        return list;
    }

    private static string? ResolveChineseName(
        int heroId,
        string? internalName,
        IReadOnlyDictionary<int, string> chineseNames)
    {
        if (chineseNames.TryGetValue(heroId, out var byId) && !string.IsNullOrWhiteSpace(byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(internalName)
            && HeroChineseNames.ByInternalName.TryGetValue(internalName, out var byName)
            && !string.IsNullOrWhiteSpace(byName))
        {
            return byName;
        }

        if (HeroChineseNames.ById.TryGetValue(heroId, out var fallback)
            && !string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return null;
    }

    private static long SumBracketPicks(HeroStat item)
        => item.Pick1 + item.Pick2 + item.Pick3 + item.Pick4
           + item.Pick5 + item.Pick6 + item.Pick7 + item.Pick8;

    private static long SumBracketWins(HeroStat item)
        => item.Win1 + item.Win2 + item.Win3 + item.Win4
           + item.Win5 + item.Win6 + item.Win7 + item.Win8;

    private static void ResetBracketRaw(HeroStat item)
    {
        item.Pick1 = 0; item.Pick2 = 0; item.Pick3 = 0; item.Pick4 = 0;
        item.Pick5 = 0; item.Pick6 = 0; item.Pick7 = 0; item.Pick8 = 0;
        item.Win1 = 0; item.Win2 = 0; item.Win3 = 0; item.Win4 = 0;
        item.Win5 = 0; item.Win6 = 0; item.Win7 = 0; item.Win8 = 0;
    }
}
