using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DotaInsight.Helpers;
using DotaInsight.Models;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 国服英雄详情资料（背景、技能、webm）。
/// </summary>
public interface IHeroProfileService
{
    Task<HeroDetailProfile?> GetProfileAsync(int heroId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 从 dota2.com.cn/datafeed/herodata 拉取并缓存。
/// </summary>
public sealed class HeroProfileService : IHeroProfileService
{
    private const string CacheKeyPrefix = "dota2cn:herodata:v2:";
    private const string HeroDataUrl =
        "https://www.dota2.com.cn/datafeed/herodata?language=schinese&hero_id={0}";

    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex BrRegex = new("<br\\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;

    public HeroProfileService(HttpClient httpClient, ILiteDbCacheService cache, ILogger logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger.ForContext<HeroProfileService>();
    }

    public async Task<HeroDetailProfile?> GetProfileAsync(
        int heroId,
        CancellationToken cancellationToken = default)
    {
        if (heroId <= 0)
        {
            return null;
        }

        var cacheKey = CacheKeyPrefix + heroId;
        var cached = _cache.Get<HeroDetailProfile>(cacheKey);
        if (cached is not null && cached.HeroId == heroId)
        {
            return cached;
        }

        try
        {
            var url = string.Format(HeroDataUrl, heroId);
            _logger.Information("请求国服英雄详情 HeroId={HeroId}", heroId);
            var response = await _httpClient
                .GetFromJsonAsync<CnHeroDataResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            var raw = response?.Result?.Heroes;
            if (raw is null || raw.Id <= 0)
            {
                _logger.Warning("国服 herodata 为空 HeroId={HeroId}", heroId);
                return _cache.GetStale<HeroDetailProfile>(cacheKey);
            }

            var profile = Map(raw);
            _cache.Set(cacheKey, profile);
            return profile;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取国服英雄详情失败 HeroId={HeroId}", heroId);
            return _cache.GetStale<HeroDetailProfile>(cacheKey);
        }
    }

    private static HeroDetailProfile Map(CnHeroData raw)
    {
        var key = HeroAssetHelper.GetHeroKey(raw.Name);
        var video = FirstNonEmpty(
            raw.TopVideo,
            string.IsNullOrWhiteSpace(key)
                ? null
                : $"https://cdn.cloudflare.steamstatic.com/apps/dota2/videos/dota_react/heroes/renders/{key}.webm");

        var poster = FirstNonEmpty(
            raw.TopImg,
            raw.CropsImg,
            string.IsNullOrWhiteSpace(key)
                ? null
                : $"https://cdn.cloudflare.steamstatic.com/apps/dota2/videos/dota_react/heroes/renders/{key}.png");

        var abilities = (raw.Abilities ?? [])
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.NameLoc))
            .Select(a => new HeroAbilityInfo
            {
                Id = a!.Id,
                Name = a.Name ?? string.Empty,
                DisplayName = a.NameLoc!.Trim(),
                Description = StripHtml(a.DescLoc),
                Lore = StripHtml(a.LoreLoc),
                IconUrl = ResolveAbilityIcon(a.Img, a.Name),
                IsInnate = a.AbilityIsInnate || a.IsInborn != 0,
                HasScepter = a.AbilityHasScepter,
                HasShard = a.AbilityHasShard
            })
            .ToList();

        return new HeroDetailProfile
        {
            HeroId = raw.Id,
            InternalName = raw.Name ?? string.Empty,
            Hype = StripHtml(raw.HypeLoc),
            Bio = StripHtml(raw.BioLoc),
            NpeDesc = StripHtml(raw.NpeDescLoc),
            VideoUrl = video ?? string.Empty,
            PosterUrl = poster ?? string.Empty,
            Complexity = raw.Complexity,
            RoleLevels = raw.RoleLevels?.ToList() ?? [],
            Abilities = abilities
        };
    }

    private static string ResolveAbilityIcon(string? img, string? abilityName)
    {
        if (!string.IsNullOrWhiteSpace(img))
        {
            return img;
        }

        if (string.IsNullOrWhiteSpace(abilityName))
        {
            return string.Empty;
        }

        return $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/abilities/{abilityName}.png";
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    internal static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = BrRegex.Replace(html, "\n");
        text = HtmlTagRegex.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private sealed class CnHeroDataResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("result")]
        public CnHeroDataResult? Result { get; set; }
    }

    private sealed class CnHeroDataResult
    {
        [JsonPropertyName("heroes")]
        public CnHeroData? Heroes { get; set; }
    }

    private sealed class CnHeroData
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("bio_loc")]
        public string? BioLoc { get; set; }

        [JsonPropertyName("hype_loc")]
        public string? HypeLoc { get; set; }

        [JsonPropertyName("npe_desc_loc")]
        public string? NpeDescLoc { get; set; }

        [JsonPropertyName("top_video")]
        public string? TopVideo { get; set; }

        [JsonPropertyName("top_img")]
        public string? TopImg { get; set; }

        [JsonPropertyName("crops_img")]
        public string? CropsImg { get; set; }

        [JsonPropertyName("complexity")]
        public int Complexity { get; set; }

        [JsonPropertyName("role_levels")]
        public List<int>? RoleLevels { get; set; }

        [JsonPropertyName("abilities")]
        public List<CnAbilityData?>? Abilities { get; set; }
    }

    private sealed class CnAbilityData
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("name_loc")]
        public string? NameLoc { get; set; }

        [JsonPropertyName("desc_loc")]
        public string? DescLoc { get; set; }

        [JsonPropertyName("lore_loc")]
        public string? LoreLoc { get; set; }

        [JsonPropertyName("img")]
        public string? Img { get; set; }

        [JsonPropertyName("ability_is_innate")]
        public bool AbilityIsInnate { get; set; }

        /// <summary>国服接口可能返回 0/1 数字。</summary>
        [JsonPropertyName("is_inborn")]
        public int IsInborn { get; set; }

        [JsonPropertyName("ability_has_scepter")]
        public bool AbilityHasScepter { get; set; }

        [JsonPropertyName("ability_has_shard")]
        public bool AbilityHasShard { get; set; }
    }
}
