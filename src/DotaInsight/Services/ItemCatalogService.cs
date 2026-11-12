using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotaInsight.Helpers;
using LiteDB;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 物品目录：item_id → 图标地址 / 购买价格。比赛详情页的装备栏与投入统计用。
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
/// OpenDota /constants/items 物品目录（图标 + 价格单次拉取），进程内 + LiteDB 双层缓存，有效期 30 天
/// （物品表只随版本变动，不值得频繁重拉）。
/// </summary>
public sealed class ItemCatalogService : IItemCatalogService
{
    public const string HttpClientName = HeroCounterService.HttpClientName;

    /// <summary>v2：图标 + 价格合并为单条缓存（此前分开存导致同端点拉两次）。</summary>
    private const string CacheKey = "opendota:itemCatalog:v2";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ItemCatalogData? _memory;

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
        => (await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false)).Icons;

    public async Task<IReadOnlyDictionary<int, int>> GetItemCostsAsync(
        CancellationToken cancellationToken = default)
        => (await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false)).Costs;

    /// <summary>单次拉取 /constants/items，同时构建图标与价格两份映射。</summary>
    private async Task<ItemCatalogData> EnsureCatalogAsync(
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

            var cached = _cache.Get<ItemCatalogData>(CacheKey);
            if (cached is { Icons.Count: > 0 })
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
                var empty = new ItemCatalogData();
                _memory = empty;
                return _memory;
            }

            var icons = new Dictionary<int, string>(raw.Count);
            var costs = new Dictionary<int, int>();
            foreach (var dto in raw.Values)
            {
                if (dto.Id <= 0)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(dto.Img))
                {
                    icons[dto.Id] = HeroAssetHelper.SteamCdnBase + StripQuery(dto.Img!);
                }

                if (dto.Cost > 0)
                {
                    costs[dto.Id] = dto.Cost;
                }
            }

            _logger.Information("物品目录已缓存 Count={Count} Icons={Icons} Costs={Costs}",
                raw.Count, icons.Count, costs.Count);

            var data = new ItemCatalogData(icons, costs);
            _memory = data;
            _cache.Set(CacheKey, data, CacheTtl);
            return _memory;
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            // 装备图标/价格是锦上添花，拿不到就退化成占位方块，不要连累整页比赛详情
            _logger.Warning(ex, "拉取物品目录失败，装备栏将显示占位");
            _memory ??= new ItemCatalogData();
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

/// <summary>物品目录数据：图标映射 + 价格映射（LiteDB 序列化用）。</summary>
public sealed class ItemCatalogData
{
    public Dictionary<int, string> Icons { get; set; } = new();

    public Dictionary<int, int> Costs { get; set; } = new();

    public ItemCatalogData() { }

    public ItemCatalogData(Dictionary<int, string> icons, Dictionary<int, int> costs)
    {
        Icons = icons;
        Costs = costs;
    }
}
