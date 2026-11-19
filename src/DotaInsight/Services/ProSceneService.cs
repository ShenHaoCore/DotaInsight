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
    /// <summary>
    /// 取职业选手/战队榜单（缓存 24h）；失败时抛出。
    /// <paramref name="forceRefresh"/>=true 时绕过内存与缓存直接重新聚合（更新按钮路径），
    /// 失败时抛出（调用方保留旧数据展示）。
    /// 缓存过期时立即返回旧榜（RefreshingInBackground=true）并在后台重新聚合；
    /// 聚合完成（成功传新榜 / 失败传 null）经 <paramref name="onBackgroundRefreshed"/> 通知调用方换界面。
    /// </summary>
    Task<ProSceneData> GetProSceneAsync(bool forceRefresh = false, CancellationToken cancellationToken = default, Action<ProSceneData?>? onBackgroundRefreshed = null);
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

    /// <summary>_memory 的装载时刻（来自缓存或聚合），用于判断是否过期需要后台刷新。</summary>
    private DateTime _memoryLoadedUtc = DateTime.MinValue;

    /// <summary>后台刷新去重/冷却：失败后 60s 内不再自动重试。</summary>
    private static readonly TimeSpan BackgroundRefreshMinInterval = TimeSpan.FromSeconds(60);

    private readonly object _bgSync = new();
    private bool _bgRefreshing;
    private DateTime _bgLastAttemptUtc = DateTime.MinValue;
    private readonly List<Action<ProSceneData?>> _bgCallbacks = [];

    /// <summary>
    /// 后台重新聚合榜单并写缓存；完成（成功传新数据 / 失败传 null）后逐个通知回调。
    /// 同账号刷新已在跑时新回调挂同一批；冷却期内失败过的立即以 null 通知（异步派发）。
    /// </summary>
    private void RefreshInBackground(Action<ProSceneData?>? onCompleted)
    {
        lock (_bgSync)
        {
            if (_bgRefreshing)
            {
                if (onCompleted is not null)
                {
                    _bgCallbacks.Add(onCompleted);
                }

                return;
            }

            if (DateTime.UtcNow - _bgLastAttemptUtc < BackgroundRefreshMinInterval)
            {
                if (onCompleted is not null)
                {
                    _ = Task.Run(() => onCompleted(null));
                }

                return;
            }

            _bgRefreshing = true;
            _bgLastAttemptUtc = DateTime.UtcNow;
            if (onCompleted is not null)
            {
                _bgCallbacks.Add(onCompleted);
            }
        }

        _ = Task.Run(async () =>
        {
            ProSceneData? fresh = null;
            try
            {
                // 后台刷新不接页面取消令牌
                fresh = await AggregateAsync(CancellationToken.None).ConfigureAwait(false);
                _memory = fresh;
                _memoryLoadedUtc = DateTime.UtcNow;
                _cache.Set(CacheKey, fresh, CacheTtl);
                _logger.Information("职业选手榜单后台刷新完成 Players={Players}", fresh.PlayerRows.Count);
            }
            catch (Exception ex)
            {
                fresh = null;
                _logger.Warning(ex, "职业选手榜单后台刷新失败（下次进入页面时再试）");
            }
            finally
            {
                List<Action<ProSceneData?>> toNotify;
                lock (_bgSync)
                {
                    toNotify = new List<Action<ProSceneData?>>(_bgCallbacks);
                    _bgCallbacks.Clear();
                    _bgRefreshing = false;
                }

                foreach (var callback in toNotify)
                {
                    try
                    {
                        callback(fresh);
                    }
                    catch
                    {
                        // 单个回调异常不影响其他通知
                    }
                }
            }
        });
    }

    public ProSceneService(IHttpClientFactory httpClientFactory, ILiteDbCacheService cache, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger.ForContext<ProSceneService>();
    }

    private HttpClient Http => _httpClientFactory.CreateClient(HttpClientName);

    public async Task<ProSceneData> GetProSceneAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default,
        Action<ProSceneData?>? onBackgroundRefreshed = null)
    {
        // 强刷（更新按钮）：绕过内存与缓存直接重新聚合，失败抛出让调用方提示、旧榜保留
        if (forceRefresh)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var fresh = await AggregateAsync(cancellationToken).ConfigureAwait(false);
                _memory = fresh;
                _memoryLoadedUtc = DateTime.UtcNow;
                _cache.Set(CacheKey, fresh, CacheTtl);
                _logger.Information("职业选手榜单已强制刷新 Players={Players}", fresh.PlayerRows.Count);
                return fresh;
            }
            finally
            {
                _gate.Release();
            }
        }

        if (_memory is not null)
        {
            // 过期缓存（Stale）秒开：先展示旧榜，后台聚合拉新；完成后经回调换新榜
            if (DateTime.UtcNow - _memoryLoadedUtc >= CacheTtl)
            {
                _logger.Information("职业选手榜单缓存过期，先展示旧榜并后台刷新");
                RefreshInBackground(onBackgroundRefreshed);
                _memory.RefreshingInBackground = true;
            }

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
                _memoryLoadedUtc = DateTime.UtcNow;
                return _memory;
            }

            var data = await AggregateAsync(cancellationToken).ConfigureAwait(false);
            _memory = data;
            _memoryLoadedUtc = DateTime.UtcNow;
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
