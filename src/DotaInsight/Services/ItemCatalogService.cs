using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotaInsight.Helpers;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 物品目录：item_id → 图标地址。比赛详情页的装备栏用。
/// </summary>
public interface IItemCatalogService
{
    /// <summary>
    /// 返回 item_id → 图标 URL 映射。拉取失败时返回空字典（装备栏退化为占位方块）。
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> GetItemIconsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// OpenDota /constants/items 物品目录，进程内 + LiteDB 双层缓存，有效期 30 天
/// （物品表只随版本变动，不值得频繁重拉）。
/// </summary>
public sealed class ItemCatalogService : IItemCatalogService
{
    public const string HttpClientName = HeroCounterService.HttpClientName;

    private const string CacheKey = "opendota:itemCatalog:v1";
    private const string CdnBase = "https://cdn.cloudflare.steamstatic.com";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyDictionary<int, string>? _memory;

    public ItemCatalogService(
        IHttpClientFactory httpClientFactory,
        ILiteDbCacheService cache,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger.ForContext<ItemCatalogService>();
    }

    private HttpClient Http => _httpClientFactory.CreateClient(HttpClientName);

    public async Task<IReadOnlyDictionary<int, string>> GetItemIconsAsync(
        CancellationToken cancellationToken = default)
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

            var cached = _cache.Get<Dictionary<int, string>>(CacheKey);
            if (cached is { Count: > 0 })
            {
                _memory = cached;
                return _memory;
            }

            var raw = await Http.GetFromJsonAsync<Dictionary<string, ItemConstantDto>>(
                "api/constants/items",
                HttpCall.JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (raw is null || raw.Count == 0)
            {
                _memory = new Dictionary<int, string>();
                return _memory;
            }

            var map = new Dictionary<int, string>(raw.Count);
            foreach (var dto in raw.Values)
            {
                if (dto.Id <= 0 || string.IsNullOrWhiteSpace(dto.Img))
                {
                    continue;
                }

                map[dto.Id] = CdnBase + StripQuery(dto.Img!);
            }

            _memory = map;
            _cache.Set(CacheKey, map, CacheTtl);
            _logger.Information("物品目录已缓存 Count={Count}", map.Count);
            return _memory;
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            // 装备图标是锦上添花，拿不到就退化成占位方块，不要连累整页比赛详情
            _logger.Warning(ex, "拉取物品目录失败，装备栏将显示占位");
            _memory ??= new Dictionary<int, string>();
            return _memory;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string StripQuery(string img)
    {
        var index = img.IndexOf('?');
        return index >= 0 ? img[..index] : img;
    }

    private sealed class ItemConstantDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("img")]
        public string? Img { get; set; }
    }
}
