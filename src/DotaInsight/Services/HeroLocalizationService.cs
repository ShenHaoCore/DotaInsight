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
    private const string HeroListUrl = "https://www.dota2.com.cn/datafeed/heroList?task=herolist";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;

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
        var cached = _cache.Get<Dictionary<int, string>>(CacheKey);
        if (cached is { Count: > 0 })
        {
            _logger.Debug("使用缓存的中文英雄名，共 {Count} 个", cached.Count);
            return cached;
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
                _logger.Information("已刷新中文英雄名缓存，共 {Count} 个", map.Count);
                return map;
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
                return stale;
            }
        }

        return HeroChineseNames.ById;
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
