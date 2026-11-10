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

    /// <summary>返回 item_id → 购买价格（金币）映射。失败时返回空字典。</summary>
    Task<IReadOnlyDictionary<int, int>> GetItemCostsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// OpenDota /constants/items 物品目录，进程内 + LiteDB 双层缓存，有效期 30 天
/// （物品表只随版本变动，不值得频繁重拉）。
/// </summary>
public sealed class ItemCatalogService : IItemCatalogService
{
    public const string HttpClientName = HeroCounterService.HttpClientName;

    private const string CacheKey = "opendota:itemCatalog:v1";

    private const string CostsCacheKey = "opendota:itemCosts:v1";

    private IReadOnlyDictionary<int, int>? _costsMemory;

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

    /// <summary>物品购买价格（金币），供装备投入统计使用。</summary>
    public async Task<IReadOnlyDictionary<int, int>> GetItemCostsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_costsMemory is not null)
        {
            return _costsMemory;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_costsMemory is not null)
            {
                return _costsMemory;
            }

            var cached = _cache.Get<Dictionary<int, int>>(CostsCacheKey);
            if (cached is { Count: > 0 })
            {
                _costsMemory = cached;
                return _costsMemory;
            }

            var raw = await Http.GetFromJsonAsync<Dictionary<string, ItemConstantDto>>(
                "api/constants/items",
                HttpCall.JsonOptions,
                cancellationToken).ConfigureAwait(false);

            var map = new Dictionary<int, int>();
            if (raw is not null)
            {
                foreach (var dto in raw.Values)
                {
                    if (dto.Id > 0 && dto.Cost > 0)
                    {
                        map[dto.Id] = dto.Cost;
                    }
                }
            }

            _logger.Information("DIAG 物品价格 {Count} 条", map.Count);
            _costsMemory = map;
            _cache.Set(CostsCacheKey, map, CacheTtl);
            return _costsMemory;
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取物品价格失败，装备投入将显示为空");
            _costsMemory ??= new Dictionary<int, int>();
            return _costsMemory;
        }
        finally
        {
            _gate.Release();
        }
    }

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

                map[dto.Id] = HeroAssetHelper.SteamCdnBase + StripQuery(dto.Img!);
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

        [JsonPropertyName("cost")] public int Cost { get; set; }
    }
}
