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
    Task<MatchAnalysisResult> AnalyzeAsync(long accountId, bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取单场比赛详情（双方阵容与逐人数据）。
    /// 比赛尚未被 Valve 解析时返回的对象 <see cref="MatchDetail.HasPlayers"/> 为 false。
    /// </summary>
    Task<MatchDetail?> GetMatchDetailAsync(long matchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 玩家生涯英雄胜率榜（按场次降序，最多 10 条）。失败/无数据返回空列表。
    /// </summary>
    Task<IReadOnlyList<PlayerHeroStat>> GetPlayerHeroStatsAsync(long accountId, bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 每日游玩热力图（近 12 个月，按本地日期聚合）。失败/无数据返回 null。
    /// </summary>
    Task<PlayerActivityHeatmap?> GetPlayerActivityAsync(long accountId, bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// OpenDota 战绩服务：profile + wl + recentMatches，LiteDB 缓存 6 小时。
/// </summary>
public sealed class MatchAnalysisService : IMatchAnalysisService
{
    public const string HttpClientName = HeroCounterService.HttpClientName;

    // v2：RecentMatchItem 扩展打法字段（GPM/伤害/分路等），旧 v1 缓存缺字段会污染打法分析
    private const string CachePrefix = "opendota:matchAnalysis:v2:";
    // v2：MatchDetail 扩展优势曲线与眼位数据，旧 v1 缓存缺字段会让新页签永远显示空
    private const string DetailCachePrefix = "opendota:matchDetail:v7:";

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
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = CachePrefix + accountId;
        var cached = forceRefresh ? null : _cache.Get<MatchAnalysisResult>(cacheKey);
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
            var heroes = await _heroService.GetHeroesAsync(false, cancellationToken).ConfigureAwait(false);
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

    private const string PlayerHeroesCachePrefix = "opendota:playerHeroes:v1:";

    private static readonly TimeSpan PlayerHeroesCacheTtl = TimeSpan.FromHours(6);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerHeroStat>> GetPlayerHeroStatsAsync(
        long accountId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return [];
        }

        var cacheKey = PlayerHeroesCachePrefix + accountId;
        var cached = forceRefresh ? null : _cache.Get<List<PlayerHeroStat>>(cacheKey);
        if (cached is { Count: > 0 })
        {
            return cached;
        }

        try
        {
            var dtos = await Http
                .GetFromJsonAsync<List<PlayerHeroStatDto>>(
                    $"api/players/{accountId}/heroes",
                    HttpCall.JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            var heroes = await _heroService.GetHeroesAsync(false, cancellationToken).ConfigureAwait(false);
            var heroMap = heroes.ToDictionary(h => h.Id);

            var list = (dtos ?? [])
                .Select(d =>
                {
                    heroMap.TryGetValue(d.HeroId, out var hero);
                    return new PlayerHeroStat
                    {
                        HeroId = d.HeroId,
                        HeroName = hero?.DisplayName ?? $"#{d.HeroId}",
                        HeroIconUrl = HeroAssetHelper.GetIconUrl(hero?.Name),
                        Games = d.Games,
                        Wins = d.Wins
                    };
                })
                .OrderByDescending(s => s.Games)
                .ThenByDescending(s => s.WinRate)
                .Take(10)
                .ToList();

            if (list.Count > 0)
            {
                _cache.Set(cacheKey, list, PlayerHeroesCacheTtl);
            }

            return list;
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取英雄胜率榜失败 AccountId={AccountId}", accountId);
            return _cache.GetStale<List<PlayerHeroStat>>(cacheKey) ?? [];
        }
    }

    private const string PlayerActivityCachePrefix = "opendota:playerActivity:v1:";

    private static readonly TimeSpan PlayerActivityCacheTtl = TimeSpan.FromHours(12);

    /// <inheritdoc />
    public async Task<PlayerActivityHeatmap?> GetPlayerActivityAsync(
        long accountId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return null;
        }

        var cacheKey = PlayerActivityCachePrefix + accountId;
        var cached = forceRefresh ? null : _cache.Get<PlayerActivityHeatmap>(cacheKey);
        if (cached is not null)
        {
            return cached;
        }

        try
        {
            var dtos = await Http
                .GetFromJsonAsync<List<RecentMatchDto>>(
                    $"api/players/{accountId}/matches?limit=600",
                    HttpCall.JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            var heatmap = PlayerActivityBuilder.Build(
                (dtos ?? []).Select(d => d.StartTime),
                DateTime.Today);

            _cache.Set(cacheKey, heatmap, PlayerActivityCacheTtl);
            return heatmap;
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取每日游玩热力图失败 AccountId={AccountId}", accountId);
            return _cache.GetStale<PlayerActivityHeatmap>(cacheKey);
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
            GameMode = dto.GameMode,
            Kills = dto.Kills,
            Deaths = dto.Deaths,
            Assists = dto.Assists,
            DurationSeconds = dto.Duration,
            StartTimeLocal = DateTimeOffset.FromUnixTimeSeconds(dto.StartTime).LocalDateTime,
            IsRadiant = isRadiant,
            Gpm = dto.GoldPerMin,
            Xpm = dto.Xpm,
            HeroDamage = dto.HeroDamage,
            TowerDamage = dto.TowerDamage,
            HeroHealing = dto.HeroHealing,
            LastHits = dto.LastHits,
            LaneRole = dto.LaneRole,
            IsRoaming = dto.IsRoaming,
            AverageRank = dto.AverageRank,
            PartySize = dto.PartySize
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
            // 旧缓存条目可能缺装备投入（字段后加）：解析过的对局就地补算，
            // 成本表走进程内/LiteDB 缓存，命中时零网络请求
            if (cached.HasPlayers)
            {
                var itemCosts = await _itemCatalog.GetItemCostsAsync(cancellationToken).ConfigureAwait(false);
                if (itemCosts.Count > 0)
                {
                    foreach (var player in cached.Radiant.Concat(cached.Dire))
                    {
                        if (player.ItemGold > 0 || player.ItemIds.Count == 0)
                        {
                            continue;
                        }

                        player.ItemGold = player.ItemIds
                            .Sum(id => itemCosts.TryGetValue(id, out var cost) ? cost : 0);
                    }
                }
            }

            _logger.Information(
                "比赛详情缓存命中 MatchId={MatchId} Radiant={R} Dire={D} Dmg0={Dmg}",
                matchId, cached.Radiant.Count, cached.Dire.Count,
                cached.Radiant.FirstOrDefault()?.HeroDamage ?? -1);
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

            var heroes = await _heroService.GetHeroesAsync(false, cancellationToken).ConfigureAwait(false);
            var heroMap = heroes.ToDictionary(h => h.Id);
            var itemIcons = await _itemCatalog.GetItemIconsAsync(cancellationToken).ConfigureAwait(false);
            var itemCosts = await _itemCatalog.GetItemCostsAsync(cancellationToken).ConfigureAwait(false);

            var detail = MapMatchDetail(dto, heroMap, itemIcons, itemCosts);

            // 无小地图数据的比赛只短暂缓存：这类对局往往尚未被 Valve 解析，
            // 解析完成后眼位/建筑事件就会补上——若按长 TTL 缓存，用户会一直看到空态
            _cache.Set(
                cacheKey,
                detail,
                detail.HasMapLayers ? DetailCacheTtl : UnparsedDetailCacheTtl);

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

    internal static MatchDetail MapMatchDetail(
        MatchDetailDto dto,
        IReadOnlyDictionary<int, HeroStat> heroMap,
        IReadOnlyDictionary<int, string> itemIcons,
        IReadOnlyDictionary<int, int>? itemCosts = null)
    {
        var radiant = new List<MatchPlayerItem>();
        var dire = new List<MatchPlayerItem>();

        foreach (var player in dto.Players ?? [])
        {
            // player_slot < 128 为天辉，>= 128 为夜魇（与 recentMatches 同一套判定）
            var item = MapPlayer(player, heroMap, itemIcons, itemCosts);
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

        var heroByNpc = BuildHeroNameByNpc(heroMap);
        // AEGIS 事件按玩家序号（0-9）取英雄，预先解析好显示名
        var heroNameBySlot = (dto.Players ?? [])
            .Select(p => heroMap.TryGetValue(p.HeroId, out var hero) ? hero.DisplayName : "玩家")
            .ToArray();
        var buildings = CollectBuildings(dto, heroByNpc, heroNameBySlot, out var mapEvents);
        mapEvents.AddRange(CollectKillEvents(dto, heroByNpc));
        mapEvents.AddRange(CollectTeamfightEvents(dto));
        mapEvents.Sort((a, b) => a.Time.CompareTo(b.Time));

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
            Dire = dire,
            GoldAdvantage = dto.RadiantGoldAdv,
            XpAdvantage = dto.RadiantXpAdv,
            Wards = CollectWards(dto),
            Buildings = buildings,
            MapEvents = mapEvents,
            RadiantHeatmap = CollectHeatmap(dto, isRadiant: true),
            DireHeatmap = CollectHeatmap(dto, isRadiant: false),
            Spots = CollectPlayerSpots(dto, heroMap),
            DraftSteps = CollectDraftSteps(dto, heroMap)
        };
    }

    /// <summary>Ban/Pick 序列（队长模式 / 队长征召）：按 order 排序，英雄名与图标就地解析。</summary>
    private static List<MatchDraftStep> CollectDraftSteps(
        MatchDetailDto dto,
        IReadOnlyDictionary<int, HeroStat> heroMap)
    {
        if (dto.PicksBans is not { Count: > 0 })
        {
            return [];
        }

        return dto.PicksBans
            .OrderBy(pb => pb.Order)
            .Select(pb =>
            {
                heroMap.TryGetValue(pb.HeroId, out var hero);
                return new MatchDraftStep
                {
                    Order = pb.Order,
                    IsPick = pb.IsPick,
                    IsRadiant = pb.Team == 0,
                    HeroId = pb.HeroId,
                    HeroName = hero?.DisplayName ?? $"#{pb.HeroId}",
                    HeroIconUrl = HeroAssetHelper.GetIconUrl(hero?.Name)
                };
            })
            .ToList();
    }

    /// <summary>英雄内部名（npc_dota_hero_xxx）→ 显示名，供击杀/建筑事件的单位名转中文。</summary>
    private static Dictionary<string, string> BuildHeroNameByNpc(IReadOnlyDictionary<int, HeroStat> heroMap)
        => heroMap.Values
            .GroupBy(h => h.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.Ordinal);

    /// <summary>
    /// 建筑 + 推倒时间线：objectives 的 building_kill / tower_deny 按 npc key 对应坐标表。
    /// 两座基地塔共用 tower4 key，按表序消耗（objectives 无法区分左右，与 odota/web 同限制）。
    /// </summary>
    private static List<MatchBuilding> CollectBuildings(
        MatchDetailDto dto,
        Dictionary<string, string> heroByNpc,
        IReadOnlyList<string> heroNameBySlot,
        out List<MatchMapEvent> mapEvents)
    {
        var buildings = MatchBuildingTable.All
            .Select(e => new MatchBuilding
            {
                Id = e.Id,
                NpcKey = e.NpcKey,
                DisplayName = e.DisplayName,
                IsRadiant = e.IsRadiant,
                IsTower = e.IsTower,
                IsBarracks = e.IsBarracks,
                IsAncient = e.IsAncient,
                LeftPct = e.LeftPct,
                TopPct = e.TopPct
            })
            .ToList();

        mapEvents = [];
        if (dto.Objectives is not { Count: > 0 })
        {
            return [];
        }

        var byKey = new Dictionary<string, Queue<MatchBuilding>>(StringComparer.Ordinal);
        foreach (var building in buildings)
        {
            if (!byKey.TryGetValue(building.NpcKey, out var queue))
            {
                queue = [];
                byKey[building.NpcKey] = queue;
            }

            queue.Enqueue(building);
        }

        foreach (var obj in dto.Objectives)
        {
            switch (obj.Type)
            {
                // 建筑事件才带 key（npc_dota_*guys_*）；肉山/盾/一血没有 key，不能提前过滤
                case "building_kill" or "CHAT_MESSAGE_TOWER_DENY"
                    when !string.IsNullOrEmpty(obj.Key)
                        && byKey.TryGetValue(obj.Key, out var queue)
                        && TakeStanding(queue) is { } fallen:
                {
                    var key = obj.Key;
                    // key 含 goodguys = 天辉的建筑被拆；进攻方 = 对面（反补时除外）
                    var lostRadiant = key.Contains("goodguys", StringComparison.Ordinal);
                    var isDeny = obj.Type == "CHAT_MESSAGE_TOWER_DENY";
                    fallen.FallsAt = obj.Time;
                    fallen.KilledBy = DescribeUnit(obj.Unit, heroByNpc);

                    mapEvents.Add(new MatchMapEvent
                    {
                        Time = obj.Time,
                        Kind = fallen.IsBarracks ? MatchMapEventKind.Barracks : MatchMapEventKind.Tower,
                        IsRadiant = isDeny ? lostRadiant : !lostRadiant,
                        Label = (isDeny ? "反补 " : "推掉 ") + fallen.DisplayName
                    });
                    break;
                }

                case "CHAT_MESSAGE_ROSHAN_KILL":
                    mapEvents.Add(new MatchMapEvent
                    {
                        Time = obj.Time,
                        Kind = MatchMapEventKind.Roshan,
                        IsRadiant = obj.Team == 2,
                        Label = "击杀肉山"
                    });
                    break;

                case "CHAT_MESSAGE_AEGIS" when obj.Slot is >= 0 && heroNameBySlot.Count > 0:
                    mapEvents.Add(new MatchMapEvent
                    {
                        Time = obj.Time,
                        Kind = MatchMapEventKind.Aegis,
                        IsRadiant = (obj.PlayerSlot ?? 0) < 128,
                        Label = $"{(obj.Slot < heroNameBySlot.Count ? heroNameBySlot[obj.Slot.Value] : "玩家")} 拿到肉山盾"
                    });
                    break;

                case "CHAT_MESSAGE_FIRSTBLOOD":
                    mapEvents.Add(new MatchMapEvent
                    {
                        Time = obj.Time,
                        Kind = MatchMapEventKind.FirstBlood,
                        IsRadiant = (obj.PlayerSlot ?? 0) < 128,
                        Label = "一血"
                    });
                    break;
            }
        }

        mapEvents.Sort((a, b) => a.Time.CompareTo(b.Time));
        return buildings;
    }

    private static MatchBuilding? TakeStanding(Queue<MatchBuilding> queue)
    {
        while (queue.Count > 0)
        {
            var candidate = queue.Dequeue();
            if (candidate.FallsAt is null)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>building_kill 的 unit 字段 → 中文描述（英雄名 / 攻城兵 / 小兵）。</summary>
    private static string DescribeUnit(string? unit, Dictionary<string, string> heroByNpc)
    {
        if (string.IsNullOrEmpty(unit))
        {
            return "小兵";
        }

        if (heroByNpc.TryGetValue(unit, out var heroName))
        {
            return heroName;
        }

        return unit.EndsWith("_siege", StringComparison.Ordinal) ? "攻城兵" : "小兵";
    }

    /// <summary>时间轴事件：建筑事件在 CollectBuildings 里顺带产出，这里补上逐次击杀。</summary>
    private static List<MatchMapEvent> CollectKillEvents(
        MatchDetailDto dto,
        Dictionary<string, string> heroByNpc)
    {
        var events = new List<MatchMapEvent>();
        foreach (var player in dto.Players ?? [])
        {
            if (player.KillsLog is null)
            {
                continue;
            }

            var killerRadiant = player.PlayerSlot < 128;
            foreach (var kill in player.KillsLog)
            {
                var victim = kill.Key is not null && heroByNpc.TryGetValue(kill.Key, out var name)
                    ? name
                    : "未知英雄";
                events.Add(new MatchMapEvent
                {
                    Time = kill.Time,
                    Kind = MatchMapEventKind.Kill,
                    IsRadiant = killerRadiant,
                    Label = $"击杀 {victim}"
                });
            }
        }

        return events;
    }

    /// <summary>团战事件：一场团战一个刻度（起始时刻），收益方按经济净变化判定。</summary>
    private static List<MatchMapEvent> CollectTeamfightEvents(MatchDetailDto dto)
    {
        var events = new List<MatchMapEvent>();
        foreach (var tf in dto.Teamfights ?? [])
        {
            // 过滤无伤亡的空聚合（解析器可能给出 start==end 且 deaths=0 的条目）
            if (tf.Deaths <= 0)
            {
                continue;
            }

            var goldDelta = tf.RadiantGoldAdvDelta ?? 0;
            events.Add(new MatchMapEvent
            {
                Time = tf.Start,
                Kind = MatchMapEventKind.TeamFight,
                IsRadiant = goldDelta >= 0,
                Label = $"团战 · {tf.Deaths} 次阵亡 · 天辉经济{(goldDelta >= 0 ? "+" : string.Empty)}{goldDelta}"
            });
        }

        return events;
    }

    /// <summary>
    /// 玩家常驻位置：lane_pos 里出现次数最高的格，按官方 gameCoordToUV 换算成 UV。
    /// </summary>
    private static List<MatchPlayerSpot> CollectPlayerSpots(
        MatchDetailDto dto,
        IReadOnlyDictionary<int, HeroStat> heroMap)
    {
        var spots = new List<MatchPlayerSpot>();
        foreach (var player in dto.Players ?? [])
        {
            if (player.LanePos is null || player.LanePos.Count == 0)
            {
                continue;
            }

            string? bestKey = null;
            var bestCount = 0;
            foreach (var (x, ys) in player.LanePos)
            {
                foreach (var (y, count) in ys)
                {
                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestKey = $"{x},{y}";
                    }
                }
            }

            if (bestKey is null)
            {
                continue;
            }

            var parts = bestKey.Split(',');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var gx)
                || !int.TryParse(parts[1], out var gy))
            {
                continue;
            }

            var uvX = gx - 64;
            var uvY = 191 - gy;
            if (uvX < 0 || uvX > 127 || uvY < 0 || uvY > 127)
            {
                continue;
            }

            heroMap.TryGetValue(player.HeroId, out var hero);
            spots.Add(new MatchPlayerSpot
            {
                UvX = uvX,
                UvY = uvY,
                HeroIconUrl = HeroAssetHelper.GetIconUrl(hero?.Name),
                HeroName = hero?.DisplayName ?? $"#{player.HeroId}",
                PersonaName = player.PersonaName ?? string.Empty,
                IsRadiant = player.PlayerSlot < 128
            });
        }

        return spots;
    }

    /// <summary>lane_pos 聚合为单侧活动热区（"x,y" → 出现次数）。</summary>
    private static Dictionary<string, int> CollectHeatmap(MatchDetailDto dto, bool isRadiant)
    {
        var cells = new Dictionary<string, int>();
        foreach (var player in dto.Players ?? [])
        {
            if (player.LanePos is null || (player.PlayerSlot < 128) != isRadiant)
            {
                continue;
            }

            foreach (var (x, ys) in player.LanePos)
            {
                foreach (var (y, count) in ys)
                {
                    // 官方约定（gameCoordToUV）：u = x-64，v = 191-y（y 轴翻转）
                    // 键自带 +64 偏移，直接入 128 网格会把热区整体画偏并丢掉越界格
                    if (!int.TryParse(x, out var gx) || !int.TryParse(y, out var gy))
                    {
                        continue;
                    }

                    var uvX = gx - 64;
                    var uvY = 191 - gy;
                    if (uvX < 0 || uvX >= 128 || uvY < 0 || uvY >= 128)
                    {
                        continue;
                    }

                    var cellKey = $"{uvX},{uvY}";
                    cells[cellKey] = cells.GetValueOrDefault(cellKey) + count;
                }
            }
        }

        return cells;
    }

    /// <summary>
    /// 聚合双方眼位日志并换算 UV 网格（odota/web utility.tsx gameCoordToUV：
    /// u = x−64，v = 191−y —— 世界左下原点 → 图像左上）。
    /// </summary>
    /// <summary>观察/岗哨守卫的存在时长（秒）。无消失记录时按满时长推算。</summary>
    private const int WardLifetimeSeconds = 360;

    private static List<MatchWardPlacement> CollectWards(MatchDetailDto dto)
    {
        var wards = new List<MatchWardPlacement>();
        foreach (var player in dto.Players ?? [])
        {
            AddWards(wards, player.ObsLog, player.ObsLeftLog, isObserver: true, owner: player);
            AddWards(wards, player.SenLog, player.SenLeftLog, isObserver: false, owner: player);
        }

        return wards;
    }

    private static void AddWards(
        List<MatchWardPlacement> into,
        List<MatchLogEntryDto>? logs,
        List<MatchLogEntryDto>? leftLogs,
        bool isObserver,
        MatchPlayerDto owner)
    {
        if (logs is null)
        {
            return;
        }

        // 按 ehandle 配对消失事件（实测配对率 100%）；缺失时按满时长推算
        var leftByEhandle = (leftLogs ?? []).Where(l => l.Ehandle is not null)
            .GroupBy(l => l.Ehandle!.Value)
            .ToDictionary(g => g.Key, g => g.First().Time);

        foreach (var log in logs)
        {
            int? leftTime = null;
            if (log.Ehandle is not null && leftByEhandle.TryGetValue(log.Ehandle.Value, out var lt))
            {
                leftTime = lt ?? 0;
            }

            into.Add(new MatchWardPlacement
            {
                UvX = log.X - 64,
                UvY = 191 - log.Y,
                Time = log.Time ?? 0,
                LeftTime = leftTime,
                IsObserver = isObserver,
                IsRadiant = (log.PlayerSlot ?? owner.PlayerSlot) < 128
            });
        }
    }

    private static MatchPlayerItem MapPlayer(
        MatchPlayerDto dto,
        IReadOnlyDictionary<int, HeroStat> heroMap,
        IReadOnlyDictionary<int, string> itemIcons,
        IReadOnlyDictionary<int, int>? itemCosts = null)
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
            IsRadiant = dto.PlayerSlot < 128,
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
            ItemIds = new[] { dto.Item0, dto.Item1, dto.Item2, dto.Item3, dto.Item4, dto.Item5, dto.ItemNeutral }
                .Where(id => id > 0)
                .ToList(),
            ItemGold = itemCosts is null ? 0 : new[] { dto.Item0, dto.Item1, dto.Item2, dto.Item3, dto.Item4, dto.Item5, dto.ItemNeutral }
                .Where(id => id > 0)
                .Sum(id => itemCosts.TryGetValue(id, out var cost) ? cost : 0),
            NeutralItemIconUrl = itemIcons.TryGetValue(dto.ItemNeutral, out var neutral)
                ? neutral
                : string.Empty,
            DraftedAbilities = (dto.Abilities ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a) && a != "generic_hidden")
                .Select(a => a!)
                .ToList()
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

        [JsonPropertyName("radiant_gold_adv")] public List<int?>? RadiantGoldAdv { get; set; }

        [JsonPropertyName("radiant_xp_adv")] public List<int?>? RadiantXpAdv { get; set; }

        /// <summary>推塔/肉山/一血等事件（仅已解析比赛有）。</summary>
        [JsonPropertyName("objectives")] public List<MatchObjectiveDto>? Objectives { get; set; }

        /// <summary>Ban/Pick 序列（队长模式 2 / 队长征召 16；team 0=天辉 1=夜魇）。</summary>
        [JsonPropertyName("picks_bans")] public List<MatchPickBanDto>? PicksBans { get; set; }

        /// <summary>团战聚合（仅已解析比赛有）。</summary>
        [JsonPropertyName("teamfights")] public List<MatchTeamfightDto>? Teamfights { get; set; }
    }

    internal sealed class MatchTeamfightDto
    {
        [JsonPropertyName("start")] public int Start { get; set; }

        [JsonPropertyName("end")] public int End { get; set; }

        [JsonPropertyName("deaths")] public int Deaths { get; set; }

        /// <summary>团战期间天辉经济净变化（正=天辉赚）。</summary>
        [JsonPropertyName("radiant_gold_adv_delta")] public int? RadiantGoldAdvDelta { get; set; }
    }

    internal sealed class MatchPickBanDto
    {
        [JsonPropertyName("is_pick")] public bool IsPick { get; set; }

        [JsonPropertyName("hero_id")] public int HeroId { get; set; }

        [JsonPropertyName("team")] public int Team { get; set; }

        [JsonPropertyName("order")] public int Order { get; set; }
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

        [JsonPropertyName("obs_log")] public List<MatchLogEntryDto>? ObsLog { get; set; }

        [JsonPropertyName("sen_log")] public List<MatchLogEntryDto>? SenLog { get; set; }

        [JsonPropertyName("obs_left_log")] public List<MatchLogEntryDto>? ObsLeftLog { get; set; }

        [JsonPropertyName("sen_left_log")] public List<MatchLogEntryDto>? SenLeftLog { get; set; }

        [JsonPropertyName("kills_log")] public List<MatchKillLogDto>? KillsLog { get; set; }

        /// <summary>技能征召（OMG）选到的技能内部名，按槽位序（仅 OMG 场次有）。</summary>
        [JsonPropertyName("abilities")] public List<string?>? Abilities { get; set; }

        /// <summary>活动位置热区：{"x": {"y": 次数}}，UV 0-127 网格。</summary>
        [JsonPropertyName("lane_pos")] public Dictionary<string, Dictionary<string, int>>? LanePos { get; set; }
    }

    /// <summary>objectives 事件条目（推塔/兵营/肉山/一血/信使等，取所需字段）。</summary>
    internal sealed class MatchObjectiveDto
    {
        [JsonPropertyName("time")] public int Time { get; set; }

        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

        /// <summary>建筑事件的 NPC 单位名；一血事件为受害者英雄 id 字符串。</summary>
        [JsonPropertyName("key")] public string? Key { get; set; }

        /// <summary>building_kill 的最后一击单位（npc_dota_hero_xxx / siege）。</summary>
        [JsonPropertyName("unit")] public string? Unit { get; set; }

        /// <summary>方（2=天辉 3=夜魇），肉山事件用。</summary>
        [JsonPropertyName("team")] public int? Team { get; set; }

        /// <summary>玩家序号（0-9），拿盾事件用。</summary>
        [JsonPropertyName("slot")] public int? Slot { get; set; }

        [JsonPropertyName("player_slot")] public int? PlayerSlot { get; set; }
    }

    /// <summary>击杀日志：只有时间与受害者内部名，无坐标（OpenDota 不提供击杀位置）。</summary>
    internal sealed class MatchKillLogDto
    {
        [JsonPropertyName("time")] public int Time { get; set; }

        [JsonPropertyName("key")] public string? Key { get; set; }
    }

    /// <summary>
    /// 眼位日志条目。x/y 为游戏坐标浮点（ odota 解析器原值，非 0-127 网格），
    /// player_slot 缺失时回退所属玩家。
    /// </summary>
    internal sealed class MatchLogEntryDto
    {
        [JsonPropertyName("x")] public double X { get; set; }

        [JsonPropertyName("y")] public double Y { get; set; }

        [JsonPropertyName("time")] public int? Time { get; set; }

        [JsonPropertyName("player_slot")] public int? PlayerSlot { get; set; }

        [JsonPropertyName("ehandle")] public long? Ehandle { get; set; }
    }
}
