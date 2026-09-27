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
}

/// <summary>
/// OpenDota 战绩服务：profile + wl + recentMatches，LiteDB 缓存 6 小时。
/// </summary>
public sealed class MatchAnalysisService : IMatchAnalysisService
{
    public const string HttpClientName = HeroCounterService.HttpClientName;

    private const string CachePrefix = "opendota:matchAnalysis:v1:";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly IHeroCounterService _heroService;
    private readonly ILogger _logger;

    public MatchAnalysisService(
        IHttpClientFactory httpClientFactory,
        ILiteDbCacheService cache,
        IHeroCounterService heroService,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _heroService = heroService;
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
                ProfileUrl = profileDto?.Profile?.ProfileUrl ?? string.Empty,
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
}
