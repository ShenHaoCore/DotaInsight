using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotaInsight.Helpers;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 英雄中文名本地化：优先国服官方接口，失败则用内置表。
/// </summary>
public interface IHeroLocalizationService
{
    /// <summary>
    /// 获取 Id -> 简体中文名 映射。
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> GetChineseNamesByIdAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 从 dota2.com.cn 拉取英雄中文名，并 LiteDB 缓存 24 小时。
/// </summary>
public sealed class HeroLocalizationService : IHeroLocalizationService
{
    public const string HttpClientName = HeroProfileService.HttpClientName;

    private const string CacheKey = "dota2cn:heroNames:zh:v1";
    private const string HeroListUrl = Helpers.ApiEndpoints.HeroListUrl;

    /// <summary>
    /// 网络失败降级后的退避期：期间不再重复发起网络请求。
    /// 没有 LiteDB 缓存又完全离线时（首次启动断网），不退避的话每次页面导航
    /// 都会触发一次带 Polly 重试的请求，首页/图鉴/克制页逐页卡顿。
    /// </summary>
    internal static readonly TimeSpan DegradedBackoff = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>进程内缓存：命中后跳过 LiteDB 反序列化（整张名字表每页导航都会用到）。</summary>
    private IReadOnlyDictionary<int, string>? _memory;

    /// <summary>降级退避截止时刻（UTC）；为过去值表示可以再次尝试网络。</summary>
    private DateTime _degradedUntilUtc;

    public HeroLocalizationService(
        IHttpClientFactory httpClientFactory,
        ILiteDbCacheService cache,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger.ForContext<HeroLocalizationService>();
    }

    public async Task<IReadOnlyDictionary<int, string>> GetChineseNamesByIdAsync(
        CancellationToken cancellationToken = default)
    {
        var memory = _memory;
        if (memory is not null)
        {
            return memory;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_memory is not null)
            {
                return _memory;
            }

            var cached = _cache.Get<Dictionary<int, string>>(CacheKey);
            if (cached is { Count: > 0 })
            {
                _memory = cached;
                _logger.Debug("使用缓存的中文英雄名，共 {Count} 个", cached.Count);
                return _memory;
            }

            // 降级退避期：跳过网络，用过期缓存或内置表顶住（退避期满后允许重试一次）
            var nowUtc = DateTime.UtcNow;
            if (nowUtc < _degradedUntilUtc)
            {
                var staleDuringBackoff = _cache.GetStale<Dictionary<int, string>>(CacheKey);
                var degraded = staleDuringBackoff is { Count: > 0 }
                    ? staleDuringBackoff
                    : HeroChineseNames.ById;
                _logger.Debug(
                    "中文名处于降级退避期，{Source}（剩余 {Seconds:F0}s）",
                    staleDuringBackoff is { Count: > 0 } ? "返回过期缓存" : "返回内置表",
                    (_degradedUntilUtc - nowUtc).TotalSeconds);
                return degraded;
            }

            try
            {
                _logger.Information("请求国服英雄列表以获取中文名：{Url}", HeroListUrl);
                var http = _httpClientFactory.CreateClient(HttpClientName);
                var response = await http
                    .GetFromJsonAsync<CnHeroListResponse>(HeroListUrl, cancellationToken)
                    .ConfigureAwait(false);

                var map = response?.Result?.Heroes?
                    .Where(h => h.Id > 0 && !string.IsNullOrWhiteSpace(h.NameLoc))
                    .GroupBy(h => h.Id)
                    .ToDictionary(g => g.Key, g => g.First().NameLoc!.Trim())
                    ?? new Dictionary<int, string>();

                if (map.Count > 0)
                {
                    _cache.Set(CacheKey, map);
                    _memory = map;
                    _logger.Information("已刷新中文英雄名缓存，共 {Count} 个", map.Count);
                    return _memory;
                }

                _logger.Warning("国服英雄列表为空，回退内置中文名表");
            }
            catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "拉取国服中文名失败，回退内置表或过期缓存");
                var stale = _cache.GetStale<Dictionary<int, string>>(CacheKey);
                if (stale is { Count: > 0 })
                {
                    // 失败降级的结果只作本次返回，不进 _memory；同时进入退避期，
                    // 避免每次页面导航都重打一轮网络
                    _degradedUntilUtc = DateTime.UtcNow.Add(DegradedBackoff);
                    return stale;
                }

                // 完全离线：退避期内直接用内置表，不再逐页重试
                _degradedUntilUtc = DateTime.UtcNow.Add(DegradedBackoff);
                return HeroChineseNames.ById;
            }

            return HeroChineseNames.ById;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class CnHeroListResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("result")]
        public CnHeroListResult? Result { get; set; }
    }

    private sealed class CnHeroListResult
    {
        [JsonPropertyName("heroes")]
        public List<CnHeroItem>? Heroes { get; set; }
    }

    private sealed class CnHeroItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("name_loc")]
        public string? NameLoc { get; set; }
    }
}
