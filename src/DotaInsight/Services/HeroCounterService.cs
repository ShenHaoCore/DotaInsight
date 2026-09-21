using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    // 版本号变更可强制刷新旧缓存（v2：补全详情字段）
    private const string HeroStatsCacheKey = "opendota:heroStats:zh:v3";
    private const string MatchupsCacheKeyPrefix = "opendota:matchups:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly ILiteDbCacheService _cache;
    private readonly IHeroLocalizationService _localization;
    private readonly ILogger _logger;

    public HeroCounterService(
        HttpClient httpClient,
        ILiteDbCacheService cache,
        IHeroLocalizationService localization,
        ILogger logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _localization = localization;
        _logger = logger.ForContext<HeroCounterService>();
    }

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
            var raw = await _httpClient
                .GetFromJsonAsync<List<HeroStatRaw>>(
                    "api/heroStats",
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (raw is null || raw.Count == 0)
            {
                _logger.Warning("heroStats 返回空数据，尝试使用过期缓存");
                var staleEmpty = _cache.GetStale<List<HeroStat>>(HeroStatsCacheKey) ?? [];
                ApplyChineseNames(staleEmpty, chineseNames);
                return staleEmpty;
            }

            var heroes = NormalizeHeroStats(raw, chineseNames);
            _cache.Set(HeroStatsCacheKey, heroes);
            _logger.Information("已刷新英雄列表缓存，共 {Count} 个", heroes.Count);
            return heroes;
        }
        catch (OperationCanceledException)
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
        var fromCache = false;
        var isOffline = false;
        List<HeroMatchupDto>? matchups = _cache.Get<List<HeroMatchupDto>>(cacheKey);

        if (matchups is { Count: > 0 })
        {
            fromCache = true;
            _logger.Information("从缓存加载对位数据 HeroId={HeroId}, Count={Count}", heroId, matchups.Count);
        }
        else
        {
            try
            {
                _logger.Information("请求 OpenDota /api/heroes/{HeroId}/matchups", heroId);
                matchups = await _httpClient
                    .GetFromJsonAsync<List<HeroMatchupDto>>(
                        $"api/heroes/{heroId}/matchups",
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (matchups is { Count: > 0 })
                {
                    _cache.Set(cacheKey, matchups);
                }
                else
                {
                    _logger.Warning("matchups 返回空，HeroId={HeroId}", heroId);
                    matchups = _cache.GetStale<List<HeroMatchupDto>>(cacheKey) ?? [];
                    fromCache = matchups.Count > 0;
                    isOffline = fromCache;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "拉取 matchups 失败，HeroId={HeroId}，尝试离线缓存", heroId);
                matchups = _cache.GetStale<List<HeroMatchupDto>>(cacheKey) ?? [];
                fromCache = matchups.Count > 0;
                isOffline = true;
            }
        }

        var items = BuildCounterItems(matchups ?? [], heroLookup);

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
    /// 将 OpenDota 原始字段归一化为业务模型（胜率/选取率/场次），并应用中文名。
    /// </summary>
    private static List<HeroStat> NormalizeHeroStats(
        IReadOnlyList<HeroStatRaw> raw,
        IReadOnlyDictionary<int, string> chineseNames)
    {
        var totalPicks = 0L;
        var mapped = new List<(HeroStat Hero, long Picks)>(raw.Count);

        foreach (var item in raw)
        {
            // 大众分段聚合优先；无数据时回退 pub / pro
            var picks = SumPicks(item);
            var wins = SumWins(item);
            if (picks <= 0)
            {
                picks = item.PubPick > 0 ? item.PubPick : item.ProPick;
                wins = item.PubPick > 0 ? item.PubWin : item.ProWin;
            }

            var englishName = item.LocalizedName ?? item.Name ?? $"#{item.Id}";
            var chineseName = ResolveChineseName(item.Id, item.Name, chineseNames) ?? englishName;
            var brackets = BuildBrackets(item);

            var hero = new HeroStat
            {
                Id = item.Id,
                Name = item.Name ?? string.Empty,
                LocalizedName = chineseName,
                PrimaryAttr = HeroDisplayHelper.ToChinesePrimaryAttr(item.PrimaryAttr),
                AttackType = HeroDisplayHelper.ToChineseAttackType(item.AttackType),
                Roles = HeroDisplayHelper.ToChineseRoles(item.Roles),
                ProPick = item.ProPick,
                ProWin = item.ProWin,
                ProBan = item.ProBan,
                PubPick = item.PubPick,
                PubWin = item.PubWin,
                TurboPicks = item.TurboPicks,
                TurboWins = item.TurboWins,
                BaseStr = item.BaseStr,
                BaseAgi = item.BaseAgi,
                BaseInt = item.BaseInt,
                StrGain = item.StrGain,
                AgiGain = item.AgiGain,
                IntGain = item.IntGain,
                BaseArmor = item.BaseArmor,
                BaseMagicResist = item.BaseMr,
                BaseHealth = item.BaseHealth,
                BaseMana = item.BaseMana,
                BaseHealthRegen = item.BaseHealthRegen ?? 0,
                BaseManaRegen = item.BaseManaRegen ?? 0,
                BaseAttackMin = item.BaseAttackMin,
                BaseAttackMax = item.BaseAttackMax,
                AttackRange = item.AttackRange,
                AttackRate = item.AttackRate,
                MoveSpeed = item.MoveSpeed,
                TurnRate = item.TurnRate ?? 0,
                ProjectileSpeed = item.ProjectileSpeed ?? 0,
                DayVision = item.DayVision,
                NightVision = item.NightVision,
                Brackets = brackets,
                Matches = (int)Math.Min(int.MaxValue, picks),
                WinRate = picks > 0 ? wins * 100.0 / picks : 0
            };

            totalPicks += picks;
            mapped.Add((hero, picks));
        }

        foreach (var (hero, picks) in mapped)
        {
            hero.PickRate = totalPicks > 0 ? picks * 100.0 / totalPicks : 0;
        }

        return mapped
            .Select(x => x.Hero)
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

    private static List<HeroBracketStat> BuildBrackets(HeroStatRaw item)
    {
        var picks = new[]
        {
            item.Pick1, item.Pick2, item.Pick3, item.Pick4,
            item.Pick5, item.Pick6, item.Pick7, item.Pick8
        };
        var wins = new[]
        {
            item.Win1, item.Win2, item.Win3, item.Win4,
            item.Win5, item.Win6, item.Win7, item.Win8
        };

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

    private static long SumPicks(HeroStatRaw item)
        => item.Pick1 + item.Pick2 + item.Pick3 + item.Pick4
           + item.Pick5 + item.Pick6 + item.Pick7 + item.Pick8;

    private static long SumWins(HeroStatRaw item)
        => item.Win1 + item.Win2 + item.Win3 + item.Win4
           + item.Win5 + item.Win6 + item.Win7 + item.Win8;

    /// <summary>
    /// OpenDota heroStats 原始 JSON 映射（含分段 pick/win）。
    /// </summary>
    private sealed class HeroStatRaw
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("localized_name")]
        public string? LocalizedName { get; set; }

        [JsonPropertyName("primary_attr")]
        public string? PrimaryAttr { get; set; }

        [JsonPropertyName("attack_type")]
        public string? AttackType { get; set; }

        [JsonPropertyName("roles")]
        public List<string>? Roles { get; set; }

        [JsonPropertyName("pro_pick")]
        public int ProPick { get; set; }

        [JsonPropertyName("pro_win")]
        public int ProWin { get; set; }

        [JsonPropertyName("pro_ban")]
        public int ProBan { get; set; }

        [JsonPropertyName("pub_pick")]
        public long PubPick { get; set; }

        [JsonPropertyName("pub_win")]
        public long PubWin { get; set; }

        [JsonPropertyName("turbo_picks")]
        public long TurboPicks { get; set; }

        [JsonPropertyName("turbo_wins")]
        public long TurboWins { get; set; }

        [JsonPropertyName("base_str")]
        public int BaseStr { get; set; }

        [JsonPropertyName("base_agi")]
        public int BaseAgi { get; set; }

        [JsonPropertyName("base_int")]
        public int BaseInt { get; set; }

        [JsonPropertyName("str_gain")]
        public double StrGain { get; set; }

        [JsonPropertyName("agi_gain")]
        public double AgiGain { get; set; }

        [JsonPropertyName("int_gain")]
        public double IntGain { get; set; }

        [JsonPropertyName("base_armor")]
        public double BaseArmor { get; set; }

        [JsonPropertyName("base_mr")]
        public double BaseMr { get; set; }

        [JsonPropertyName("base_health")]
        public int BaseHealth { get; set; }

        [JsonPropertyName("base_mana")]
        public int BaseMana { get; set; }

        [JsonPropertyName("base_health_regen")]
        public double? BaseHealthRegen { get; set; }

        [JsonPropertyName("base_mana_regen")]
        public double? BaseManaRegen { get; set; }

        [JsonPropertyName("base_attack_min")]
        public int BaseAttackMin { get; set; }

        [JsonPropertyName("base_attack_max")]
        public int BaseAttackMax { get; set; }

        [JsonPropertyName("attack_range")]
        public int AttackRange { get; set; }

        [JsonPropertyName("attack_rate")]
        public double AttackRate { get; set; }

        [JsonPropertyName("move_speed")]
        public int MoveSpeed { get; set; }

        [JsonPropertyName("turn_rate")]
        public double? TurnRate { get; set; }

        [JsonPropertyName("projectile_speed")]
        public int? ProjectileSpeed { get; set; }

        [JsonPropertyName("day_vision")]
        public int DayVision { get; set; }

        [JsonPropertyName("night_vision")]
        public int NightVision { get; set; }

        [JsonPropertyName("1_pick")] public long Pick1 { get; set; }
        [JsonPropertyName("2_pick")] public long Pick2 { get; set; }
        [JsonPropertyName("3_pick")] public long Pick3 { get; set; }
        [JsonPropertyName("4_pick")] public long Pick4 { get; set; }
        [JsonPropertyName("5_pick")] public long Pick5 { get; set; }
        [JsonPropertyName("6_pick")] public long Pick6 { get; set; }
        [JsonPropertyName("7_pick")] public long Pick7 { get; set; }
        [JsonPropertyName("8_pick")] public long Pick8 { get; set; }

        [JsonPropertyName("1_win")] public long Win1 { get; set; }
        [JsonPropertyName("2_win")] public long Win2 { get; set; }
        [JsonPropertyName("3_win")] public long Win3 { get; set; }
        [JsonPropertyName("4_win")] public long Win4 { get; set; }
        [JsonPropertyName("5_win")] public long Win5 { get; set; }
        [JsonPropertyName("6_win")] public long Win6 { get; set; }
        [JsonPropertyName("7_win")] public long Win7 { get; set; }
        [JsonPropertyName("8_win")] public long Win8 { get; set; }
    }
}
