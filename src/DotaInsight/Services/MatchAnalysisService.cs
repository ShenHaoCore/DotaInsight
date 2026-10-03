using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotaInsight.Helpers;
using DotaInsight.Models;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 玩家战绩分析。
/// </summary>
public interface IMatchAnalysisService
{
    /// <summary>
    /// 解析输入为 OpenDota account_id（支持 32 位账号或 SteamID64）。
    /// </summary>
    bool TryParseAccountId(string? input, out long accountId);

    /// <summary>
    /// 获取玩家资料与近期战绩。
    /// </summary>
    Task<MatchAnalysisResult> AnalyzeAsync(long accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取单场比赛详情（双方阵容与逐人数据）。
    /// 比赛尚未被 Valve 解析时返回的对象 <see cref="MatchDetail.HasPlayers"/> 为 false。
    /// </summary>
    Task<MatchDetail?> GetMatchDetailAsync(long matchId, CancellationToken cancellationToken = default);
}

/// <summary>
/// OpenDota 战绩服务：profile + wl + recentMatches，LiteDB 缓存 6 小时。
/// </summary>
public sealed class MatchAnalysisService : IMatchAnalysisService
{
    public const string HttpClientName = HeroCounterService.HttpClientName;

    private const string CachePrefix = "opendota:matchAnalysis:v1:";
    private const string DetailCachePrefix = "opendota:matchDetail:v1:";

    /// <summary>已解析的比赛内容不会再变，可以放心长期缓存。</summary>
    private static readonly TimeSpan DetailCacheTtl = TimeSpan.FromDays(30);

    /// <summary>未解析的比赛稍后可能被解析，只短暂缓存，避免长期显示「暂无逐人数据」。</summary>
    private static readonly TimeSpan UnparsedDetailCacheTtl = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly IHeroCounterService _heroService;
    private readonly IItemCatalogService _itemCatalog;
    private readonly ILogger _logger;

    public MatchAnalysisService(
        IHttpClientFactory httpClientFactory,
        ILiteDbCacheService cache,
        IHeroCounterService heroService,
        IItemCatalogService itemCatalog,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _heroService = heroService;
        _itemCatalog = itemCatalog;
        _logger = logger.ForContext<MatchAnalysisService>();
    }

    private HttpClient Http => _httpClientFactory.CreateClient(HttpClientName);

    public bool TryParseAccountId(string? input, out long accountId)
        => SteamAccountId.TryParse(input, out accountId);

    public async Task<MatchAnalysisResult> AnalyzeAsync(
        long accountId,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = CachePrefix + accountId;
        var cached = _cache.Get<MatchAnalysisResult>(cacheKey);
        if (cached?.Profile is not null)
        {
            _logger.Information("战绩缓存命中 AccountId={AccountId}", accountId);
            return new MatchAnalysisResult
            {
                Profile = cached.Profile,
                Matches = cached.Matches,
                FromCache = true
            };
        }

        try
        {
            _logger.Information("拉取战绩 AccountId={AccountId}", accountId);
            var heroes = await _heroService.GetHeroesAsync(cancellationToken).ConfigureAwait(false);
            var heroMap = heroes.ToDictionary(h => h.Id);
            var http = Http;

            var profileTask = http.GetFromJsonAsync<PlayerApiDto>(
                $"api/players/{accountId}", HttpCall.JsonOptions, cancellationToken);
            var wlTask = http.GetFromJsonAsync<PlayerWlDto>(
                $"api/players/{accountId}/wl", HttpCall.JsonOptions, cancellationToken);
            var matchesTask = http.GetFromJsonAsync<List<RecentMatchDto>>(
                $"api/players/{accountId}/recentMatches", HttpCall.JsonOptions, cancellationToken);

            await Task.WhenAll(profileTask, wlTask, matchesTask).ConfigureAwait(false);

            var profileDto = await profileTask.ConfigureAwait(false);
            var wlDto = await wlTask.ConfigureAwait(false);
            var matchDtos = await matchesTask.ConfigureAwait(false) ?? [];

            if (profileDto?.Profile is null && matchDtos.Count == 0)
            {
                var stale = _cache.GetStale<MatchAnalysisResult>(cacheKey);
                if (stale is not null)
                {
                    return new MatchAnalysisResult
                    {
                        Profile = stale.Profile,
                        Matches = stale.Matches,
                        FromCache = true,
                        IsOffline = true
                    };
                }

                return new MatchAnalysisResult();
            }

            var profile = new PlayerProfile
            {
                AccountId = profileDto?.Profile?.AccountId > 0
                    ? profileDto.Profile.AccountId
                    : accountId,
                PersonaName = profileDto?.Profile?.PersonaName ?? $"玩家 {accountId}",
                AvatarUrl = profileDto?.Profile?.AvatarFull ?? string.Empty,
                RankTier = profileDto?.RankTier,
                Wins = wlDto?.Win ?? 0,
                Losses = wlDto?.Lose ?? 0
            };

            var matches = matchDtos.Select(m => MapMatch(m, heroMap)).ToList();
            var result = new MatchAnalysisResult
            {
                Profile = profile,
                Matches = matches
            };

            _cache.Set(cacheKey, result, TimeSpan.FromHours(6));
            _logger.Information(
                "战绩已刷新 AccountId={AccountId} Matches={Count}",
                accountId,
                matches.Count);
            return result;
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取战绩失败 AccountId={AccountId}", accountId);
            var stale = _cache.GetStale<MatchAnalysisResult>(cacheKey);
            if (stale is not null)
            {
                return new MatchAnalysisResult
                {
                    Profile = stale.Profile,
                    Matches = stale.Matches,
                    FromCache = true,
                    IsOffline = true
                };
            }

            throw;
        }
    }

    private static RecentMatchItem MapMatch(
        RecentMatchDto dto,
        IReadOnlyDictionary<int, HeroStat> heroMap)
    {
        var isRadiant = dto.PlayerSlot < 128;
        var isWin = isRadiant ? dto.RadiantWin : !dto.RadiantWin;
        heroMap.TryGetValue(dto.HeroId, out var hero);

        return new RecentMatchItem
        {
            MatchId = dto.MatchId,
            HeroId = dto.HeroId,
            HeroName = hero?.DisplayName ?? $"#{dto.HeroId}",
            HeroInternalName = hero?.Name ?? string.Empty,
            IconUrl = HeroAssetHelper.GetIconUrl(hero?.Name),
            IsWin = isWin,
            Kills = dto.Kills,
            Deaths = dto.Deaths,
            Assists = dto.Assists,
            DurationSeconds = dto.Duration,
            StartTimeLocal = DateTimeOffset.FromUnixTimeSeconds(dto.StartTime).LocalDateTime,
            IsRadiant = isRadiant
        };
    }

    public async Task<MatchDetail?> GetMatchDetailAsync(
        long matchId,
        CancellationToken cancellationToken = default)
    {
        if (matchId <= 0)
        {
            return null;
        }

        var cacheKey = DetailCachePrefix + matchId;
        var cached = _cache.Get<MatchDetail>(cacheKey);
        if (cached is not null)
        {
            _logger.Information("比赛详情缓存命中 MatchId={MatchId}", matchId);
            return cached;
        }

        try
        {
            var dto = await Http.GetFromJsonAsync<MatchDetailDto>(
                $"api/matches/{matchId}",
                HttpCall.JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (dto is null || dto.MatchId <= 0)
            {
                return null;
            }

            var heroes = await _heroService.GetHeroesAsync(cancellationToken).ConfigureAwait(false);
            var heroMap = heroes.ToDictionary(h => h.Id);
            var itemIcons = await _itemCatalog.GetItemIconsAsync(cancellationToken).ConfigureAwait(false);

            var detail = MapMatchDetail(dto, heroMap, itemIcons);

            // 未解析的比赛只短暂缓存：Valve 解析完成后就能补上逐人数据
            _cache.Set(
                cacheKey,
                detail,
                detail.HasPlayers ? DetailCacheTtl : UnparsedDetailCacheTtl);

            _logger.Information(
                "比赛详情已加载 MatchId={MatchId} Players={Count}",
                matchId,
                detail.Radiant.Count + detail.Dire.Count);
            return detail;
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取比赛详情失败 MatchId={MatchId}", matchId);
            return _cache.GetStale<MatchDetail>(cacheKey);
        }
    }

    private static MatchDetail MapMatchDetail(
        MatchDetailDto dto,
        IReadOnlyDictionary<int, HeroStat> heroMap,
        IReadOnlyDictionary<int, string> itemIcons)
    {
        var radiant = new List<MatchPlayerItem>();
        var dire = new List<MatchPlayerItem>();

        foreach (var player in dto.Players ?? [])
        {
            // player_slot < 128 为天辉，>= 128 为夜魇（与 recentMatches 同一套判定）
            var item = MapPlayer(player, heroMap, itemIcons);
            if (player.PlayerSlot < 128)
            {
                radiant.Add(item);
            }
            else
            {
                dire.Add(item);
            }
        }

        // 与经济面板一致：同队按 GPM 从高到低排列
        radiant.Sort((a, b) => b.GoldPerMin.CompareTo(a.GoldPerMin));
        dire.Sort((a, b) => b.GoldPerMin.CompareTo(a.GoldPerMin));

        return new MatchDetail
        {
            MatchId = dto.MatchId,
            RadiantWin = dto.RadiantWin,
            DurationSeconds = dto.Duration,
            StartTimeLocal = DateTimeOffset.FromUnixTimeSeconds(dto.StartTime).LocalDateTime,
            GameMode = dto.GameMode,
            LobbyType = dto.LobbyType,
            RadiantScore = dto.RadiantScore,
            DireScore = dto.DireScore,
            RadiantTeamName = dto.RadiantName,
            DireTeamName = dto.DireName,
            Radiant = radiant,
            Dire = dire
        };
    }

    private static MatchPlayerItem MapPlayer(
        MatchPlayerDto dto,
        IReadOnlyDictionary<int, HeroStat> heroMap,
        IReadOnlyDictionary<int, string> itemIcons)
    {
        heroMap.TryGetValue(dto.HeroId, out var hero);

        var slots = new[] { dto.Item0, dto.Item1, dto.Item2, dto.Item3, dto.Item4, dto.Item5 };
        var itemUrls = new List<string>(slots.Length);
        foreach (var itemId in slots)
        {
            itemUrls.Add(itemIcons.TryGetValue(itemId, out var url) ? url : string.Empty);
        }

        return new MatchPlayerItem
        {
            AccountId = dto.AccountId ?? 0,
            HeroId = dto.HeroId,
            HeroName = hero?.DisplayName ?? $"#{dto.HeroId}",
            HeroIconUrl = HeroAssetHelper.GetIconUrl(hero?.Name),
            PersonaName = dto.PersonaName ?? string.Empty,
            Level = dto.Level,
            Kills = dto.Kills,
            Deaths = dto.Deaths,
            Assists = dto.Assists,
            GoldPerMin = dto.GoldPerMin,
            XpPerMin = dto.XpPerMin,
            LastHits = dto.LastHits,
            Denies = dto.Denies,
            NetWorth = dto.NetWorth,
            HeroDamage = dto.HeroDamage,
            HeroHealing = dto.HeroHealing,
            TowerDamage = dto.TowerDamage,
            ItemIconUrls = itemUrls,
            NeutralItemIconUrl = itemIcons.TryGetValue(dto.ItemNeutral, out var neutral)
                ? neutral
                : string.Empty
        };
    }

    /// <summary>
    /// /matches/{id} 的响应包含大量时间序列字段（gold_t、kills_log 等），
    /// 这里只声明需要的字段，其余由序列化器忽略。
    /// </summary>
    internal sealed class MatchDetailDto
    {
        [JsonPropertyName("match_id")] public long MatchId { get; set; }
        [JsonPropertyName("radiant_win")] public bool RadiantWin { get; set; }
        [JsonPropertyName("duration")] public int Duration { get; set; }
        [JsonPropertyName("start_time")] public long StartTime { get; set; }
        [JsonPropertyName("game_mode")] public int GameMode { get; set; }
        [JsonPropertyName("lobby_type")] public int LobbyType { get; set; }
        [JsonPropertyName("radiant_score")] public int RadiantScore { get; set; }
        [JsonPropertyName("dire_score")] public int DireScore { get; set; }
        [JsonPropertyName("radiant_name")] public string? RadiantName { get; set; }
        [JsonPropertyName("dire_name")] public string? DireName { get; set; }
        [JsonPropertyName("players")] public List<MatchPlayerDto>? Players { get; set; }
    }

    internal sealed class MatchPlayerDto
    {
        [JsonPropertyName("account_id")] public long? AccountId { get; set; }
        [JsonPropertyName("player_slot")] public int PlayerSlot { get; set; }
        [JsonPropertyName("hero_id")] public int HeroId { get; set; }
        [JsonPropertyName("personaname")] public string? PersonaName { get; set; }
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("kills")] public int Kills { get; set; }
        [JsonPropertyName("deaths")] public int Deaths { get; set; }
        [JsonPropertyName("assists")] public int Assists { get; set; }
        [JsonPropertyName("gold_per_min")] public int GoldPerMin { get; set; }
        [JsonPropertyName("xp_per_min")] public int XpPerMin { get; set; }
        [JsonPropertyName("last_hits")] public int LastHits { get; set; }
        [JsonPropertyName("denies")] public int Denies { get; set; }
        [JsonPropertyName("net_worth")] public int NetWorth { get; set; }
        [JsonPropertyName("hero_damage")] public int HeroDamage { get; set; }
        [JsonPropertyName("hero_healing")] public int HeroHealing { get; set; }
        [JsonPropertyName("tower_damage")] public int TowerDamage { get; set; }
        [JsonPropertyName("item_0")] public int Item0 { get; set; }
        [JsonPropertyName("item_1")] public int Item1 { get; set; }
        [JsonPropertyName("item_2")] public int Item2 { get; set; }
        [JsonPropertyName("item_3")] public int Item3 { get; set; }
        [JsonPropertyName("item_4")] public int Item4 { get; set; }
        [JsonPropertyName("item_5")] public int Item5 { get; set; }
        [JsonPropertyName("item_neutral")] public int ItemNeutral { get; set; }
    }
}
