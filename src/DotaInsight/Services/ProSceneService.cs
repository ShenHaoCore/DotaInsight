using System.Net.Http;
using System.Net.Http.Json;
using DotaInsight.Helpers;
using DotaInsight.Models;
using LiteDB;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 职业选手榜单：OpenDota proPlayers（选手名录）× 活跃战队 roster（出场/胜负）聚合。
/// 榜单一天聚合一次（缓存 24h），无逐选手 API 调用。
/// </summary>
public interface IProSceneService
{
    /// <summary>取职业选手/战队榜单（缓存 24h）；失败时抛出。</summary>
    Task<ProSceneData> GetProSceneAsync(CancellationToken cancellationToken = default);
}

public sealed class ProSceneService : IProSceneService
{
    public const string HttpClientName = HeroCounterService.HttpClientName;

    /// <summary>聚合缓存键；榜单一天更新一次足够。</summary>
    private const string CacheKey = "opendota:proScene:v1";

    /// <summary>聚合的战队数上限：按现役人数取前 N 支活跃战队（61 次调用，压在 OpenDota 60/min 限流边缘之下）。</summary>
    internal const int TopTeams = 24;

    /// <summary>战队 roster 并行度。</summary>
    private const int Parallelism = 6;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProSceneData? _memory;

    public ProSceneService(IHttpClientFactory httpClientFactory, ILiteDbCacheService cache, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger.ForContext<ProSceneService>();
    }

    private HttpClient Http => _httpClientFactory.CreateClient(HttpClientName);

    public async Task<ProSceneData> GetProSceneAsync(CancellationToken cancellationToken = default)
    {
        if (_memory is not null)
        {
            return _memory;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_memory is not null)
            {
                return _memory;
            }

            var cached = _cache.Get<ProSceneData>(CacheKey);
            if (cached is { PlayerRows.Count: > 0 })
            {
                _memory = cached;
                return _memory;
            }

            var data = await AggregateAsync(cancellationToken).ConfigureAwait(false);
            _memory = data;
            _cache.Set(CacheKey, data, CacheTtl);
            _logger.Information("职业选手榜单已聚合 Players={Players} Teams={Teams}",
                data.PlayerRows.Count, data.TeamRows.Count);
            return _memory;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProSceneData> AggregateAsync(CancellationToken cancellationToken)
    {
        var pros = await Http.GetFromJsonAsync<List<ProPlayerApiDto>>(
            "api/proPlayers", HttpCall.JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? [];

        // 活跃战队：按现役人数取前 N（人数多 = 名单还在维护的知名战队）
        var topTeams = pros
            .Where(p => p.TeamId > 0)
            .GroupBy(p => p.TeamId)
            .OrderByDescending(g => g.Count())
            .Take(TopTeams)
            .Select(g => (TeamId: g.Key, Headcount: g.Count()))
            .ToList();

        var teamBlocks = new List<(long TeamId, ProTeamApiDto Info, List<ProTeamPlayerApiDto> Players)>();
        using var gate = new SemaphoreSlim(Parallelism, Parallelism);
        var tasks = topTeams.Select(async tuple =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var info = await Http.GetFromJsonAsync<ProTeamApiDto>(
                    $"api/teams/{tuple.TeamId}", HttpCall.JsonOptions, cancellationToken).ConfigureAwait(false);
                var players = await Http.GetFromJsonAsync<List<ProTeamPlayerApiDto>>(
                    $"api/teams/{tuple.TeamId}/players", HttpCall.JsonOptions, cancellationToken).ConfigureAwait(false);

                return (tuple.TeamId, Info: info, Players: players ?? []);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单队失败跳过，不拖垮整张榜
                _logger.Warning("拉取战队 {TeamId} 失败，跳过：{Message}", tuple.TeamId, ex.Message);
                return (tuple.TeamId, null, new List<ProTeamPlayerApiDto>());
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        teamBlocks.AddRange((await Task.WhenAll(tasks).ConfigureAwait(false))
            .Where(b => b.Info is not null)
            .Select(b => (b.TeamId, b.Info!, b.Players)));

        var playerRows = BuildPlayerRows(pros, teamBlocks);
        var teamRows = BuildTeamRows(teamBlocks);

        return new ProSceneData
        {
            PlayerRows = playerRows,
            TeamRows = teamRows,
            AggregatedUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 选手榜：top 战队的现役成员（is_current_team_member）按队伍总场次聚合。
    /// proPlayers 提供认证名/头像/位置，roster 提供出场/胜负；一名选手只保留出场最多的现役战队。
    /// </summary>
    internal static List<ProPlayerRow> BuildPlayerRows(
        List<ProPlayerApiDto> pros,
        List<(long TeamId, ProTeamApiDto Info, List<ProTeamPlayerApiDto> Players)> teamBlocks)
    {
        var proById = pros.Where(p => p.AccountId > 0)
            .GroupBy(p => p.AccountId)
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<ProPlayerRow>();
        var seenAccounts = new HashSet<long>();
        foreach (var block in teamBlocks.OrderByDescending(b => b.Players.Max(p => (int?)p.GamesPlayed) ?? 0))
        {
            var teamName = string.IsNullOrWhiteSpace(block.Info.Name) ? "未知战队" : block.Info.Name;
            foreach (var tp in block.Players)
            {
                if (tp.IsCurrentTeamMember != true || tp.AccountId <= 0 || tp.GamesPlayed <= 0)
                {
                    continue;
                }

                if (!seenAccounts.Add(tp.AccountId))
                {
                    continue;
                }

                proById.TryGetValue(tp.AccountId, out var profile);
                var displayName = !string.IsNullOrWhiteSpace(tp.Name)
                    ? tp.Name
                    : !string.IsNullOrWhiteSpace(profile?.Name)
                        ? profile!.Name
                        : profile?.PersonaName ?? $"选手 {tp.AccountId}";

                var games = tp.GamesPlayed;
                var wins = Math.Clamp(tp.Wins, 0, games);
                rows.Add(new ProPlayerRow
                {
                    AccountId = tp.AccountId,
                    DisplayName = displayName,
                    AvatarUrl = profile?.AvatarFull ?? string.Empty,
                    TeamName = teamName,
                    RoleText = RoleText(profile?.FantasyRole),
                    GamesPlayed = games,
                    Wins = wins,
                    WinRate = games > 0 ? wins * 100.0 / games : 0,
                    WinRateText = $"{wins * 100.0 / games:F1}%"
                });
            }
        }

        var ordered = rows.OrderByDescending(r => r.GamesPlayed).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].RankNumber = i + 1;
        }

        return ordered;
    }

    /// <summary>战队榜：按 OpenDota rating 降序。</summary>
    internal static List<ProTeamRow> BuildTeamRows(
        List<(long TeamId, ProTeamApiDto Info, List<ProTeamPlayerApiDto> Players)> teamBlocks)
    {
        var teamRows = teamBlocks
            .Where(b => b.Info is { Wins: > 0 } or { Losses: > 0 })
            .Select(b =>
            {
                var info = b.Info!;
                var total = info.Wins + info.Losses;
                var rate = total > 0 ? info.Wins * 100.0 / total : 0;
                return new ProTeamRow
                {
                    TeamId = info.TeamId,
                    Name = string.IsNullOrWhiteSpace(info.Name) ? "未知战队" : info.Name,
                    Tag = info.Tag ?? string.Empty,
                    Rating = info.Rating ?? 0,
                    RatingText = info.Rating is null or 0 ? "—" : $"{info.Rating:F0}",
                    Wins = info.Wins,
                    Losses = info.Losses,
                    WinRate = rate,
                    WinRateText = total > 0 ? $"{rate:F1}%" : "—"
                };
            })
            .OrderByDescending(t => t.Rating)
            .ToList();

        for (var i = 0; i < teamRows.Count; i++)
        {
            teamRows[i].RankNumber = i + 1;
        }

        return teamRows;
    }

    /// <summary>位置（fantasy_role）→ 中文文案。</summary>
    internal static string RoleText(int? role) => role switch
    {
        1 => "一号位",
        2 => "中单",
        3 => "三号位",
        4 => "四号位",
        5 => "五号位",
        _ => string.Empty
    };
}
