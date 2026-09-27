using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
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
    public const string HttpClientName = "dota2cn";

    private const string CacheKeyPrefix = "dota2cn:herodata:v5:";
    private const string HeroDataUrl =
        "https://www.dota2.com.cn/datafeed/herodata?language=schinese&hero_id={0}";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;

    public HeroProfileService(
        IHttpClientFactory httpClientFactory,
        ILiteDbCacheService cache,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
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
            var http = _httpClientFactory.CreateClient(HttpClientName);
            var response = await http
                .GetFromJsonAsync<CnHeroDataResponse>(url, HttpCall.JsonOptions, cancellationToken)
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
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
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
        var video = HeroDisplayHelper.FirstNonEmpty(
            raw.TopVideo,
            string.IsNullOrWhiteSpace(key)
                ? null
                : $"https://cdn.cloudflare.steamstatic.com/apps/dota2/videos/dota_react/heroes/renders/{key}.webm");

        var poster = HeroDisplayHelper.FirstNonEmpty(
            raw.TopImg,
            raw.CropsImg,
            string.IsNullOrWhiteSpace(key)
                ? null
                : $"https://cdn.cloudflare.steamstatic.com/apps/dota2/videos/dota_react/heroes/renders/{key}.png");

        var abilities = (raw.Abilities ?? [])
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.NameLoc))
            .Select(a => MapAbility(a!))
            .ToList();

        var talents = MapTalents(raw.Talents, BuildBonusIndex(raw.Abilities));

        return new HeroDetailProfile
        {
            HeroId = raw.Id,
            InternalName = raw.Name ?? string.Empty,
            Hype = AbilityTextHelper.StripHtml(raw.HypeLoc),
            Bio = AbilityTextHelper.StripHtml(raw.BioLoc),
            NpeDesc = AbilityTextHelper.StripHtml(raw.NpeDescLoc),
            VideoUrl = video ?? string.Empty,
            PosterUrl = poster ?? string.Empty,
            Complexity = raw.Complexity,
            RoleLevels = raw.RoleLevels?.ToList() ?? [],
            Abilities = abilities,
            Talents = talents
        };
    }

    /// <summary>
    /// 天赋数据顺序为 [右10,左10,右15,左15,右20,左20,右25,左25]，
    /// 映射为行并按官网样式反转为 25→10 自上而下。
    /// </summary>
    private static List<HeroTalentRow> MapTalents(
        IReadOnlyList<CnAbilityData?>? talents,
        IReadOnlyDictionary<string, Dictionary<string, string>> bonusIndex)
    {
        var rows = new List<HeroTalentRow>();
        if (talents is null)
        {
            return rows;
        }

        for (var i = 0; i + 1 < talents.Count; i += 2)
        {
            var level = 10 + (i / 2) * 5;
            var rightText = ResolveTalentName(talents[i], bonusIndex);
            var leftText = ResolveTalentName(talents[i + 1], bonusIndex);
            if (string.IsNullOrWhiteSpace(leftText) && string.IsNullOrWhiteSpace(rightText))
            {
                continue;
            }

            rows.Add(new HeroTalentRow
            {
                Level = level,
                Left = leftText,
                Right = rightText
            });
        }

        rows.Reverse();
        return rows;
    }

    private static string ResolveTalentName(
        CnAbilityData? talent,
        IReadOnlyDictionary<string, Dictionary<string, string>> bonusIndex)
    {
        if (talent is null || string.IsNullOrWhiteSpace(talent.NameLoc))
        {
            return string.Empty;
        }

        var ownValues = BuildSpecialValueMap(talent.SpecialValues);
        var talentName = talent.Name ?? string.Empty;
        bonusIndex.TryGetValue(talentName, out var bonusMap);

        return AbilityTextHelper.FormatTalentName(talent.NameLoc, key =>
        {
            if (key.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                if (ownValues.TryGetValue("value", out var value))
                {
                    return value;
                }

                return ownValues.Values.FirstOrDefault();
            }

            var specialKey = key.StartsWith("bonus_", StringComparison.OrdinalIgnoreCase)
                ? key["bonus_".Length..]
                : key;

            if (bonusMap is not null && bonusMap.TryGetValue(specialKey, out var bonus))
            {
                return bonus;
            }

            return ownValues.TryGetValue(specialKey, out var own) ? own : null;
        });
    }

    /// <summary>
    /// 索引技能 special_values.bonuses：天赋内部名 → (special_value 名 → 数值)。
    /// 同一键出现多个值时用 / 连接（如最低/最高减速 9/18）。
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> BuildBonusIndex(
        IReadOnlyList<CnAbilityData?>? abilities)
    {
        var index = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (abilities is null)
        {
            return index;
        }

        foreach (var ability in abilities)
        {
            if (ability?.SpecialValues is null)
            {
                continue;
            }

            foreach (var sv in ability.SpecialValues)
            {
                if (sv?.Bonuses is null || string.IsNullOrEmpty(sv.Name))
                {
                    continue;
                }

                foreach (var bonus in sv.Bonuses)
                {
                    if (bonus is null || string.IsNullOrWhiteSpace(bonus.Name))
                    {
                        continue;
                    }

                    if (!index.TryGetValue(bonus.Name!, out var map))
                    {
                        map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        index[bonus.Name!] = map;
                    }

                    var formatted = AbilityTextHelper.FormatValue(bonus.Value);
                    map[sv.Name!] = map.TryGetValue(sv.Name!, out var existing)
                        ? existing + "/" + formatted
                        : formatted;
                }
            }
        }

        return index;
    }

    /// <summary>冷却/耗蓝数组：全 0 留空，连续相同值折叠为单个。</summary>
    private static string FormatLevelValues(IReadOnlyList<double>? values)
    {
        if (values is null || values.Count == 0 || values.All(v => Math.Abs(v) < 0.0001))
        {
            return string.Empty;
        }

        var parts = new List<string>(values.Count);
        foreach (var value in values)
        {
            var text = AbilityTextHelper.FormatValue(value);
            if (parts.Count == 0 || parts[^1] != text)
            {
                parts.Add(text);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>
    /// 国服 behavior 为位掩码字符串，DOTA_ABILITY_BEHAVIOR_PASSIVE = 1 &lt;&lt; 1（数值 2）。
    /// 兼容个别非数字（含 PASSIVE 文本）的返回。
    /// </summary>
    private static bool IsPassiveBehavior(string? behavior)
    {
        if (string.IsNullOrWhiteSpace(behavior))
        {
            return false;
        }

        if (long.TryParse(behavior.Trim(), out var mask))
        {
            return (mask & 2) != 0;
        }

        return behavior.Contains("PASSIVE", StringComparison.OrdinalIgnoreCase);
    }

    private static HeroAbilityInfo MapAbility(CnAbilityData a)
    {
        var values = BuildSpecialValueMap(a.SpecialValues);
        return new HeroAbilityInfo
        {
            Id = a.Id,
            Name = a.Name ?? string.Empty,
            DisplayName = a.NameLoc!.Trim(),
            Description = AbilityTextHelper.FormatDescription(a.DescLoc, values),
            Lore = AbilityTextHelper.StripHtml(a.LoreLoc),
            IconUrl = ResolveAbilityIcon(a.Img, a.Name),
            IsInnate = a.AbilityIsInnate || a.IsInborn != 0,
            IsPassive = IsPassiveBehavior(a.Behavior),
            HasScepter = a.AbilityHasScepter,
            HasShard = a.AbilityHasShard,
            GrantedByScepter = a.AbilityIsGrantedByScepter,
            GrantedByShard = a.AbilityIsGrantedByShard,
            CooldownText = FormatLevelValues(a.Cooldowns),
            ManaCostText = FormatLevelValues(a.ManaCosts),
            Notes = (a.NotesLoc ?? [])
                .Select(note => AbilityTextHelper.FormatDescription(note, values))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList(),
            ScepterDescription = AbilityTextHelper.FormatDescription(a.ScepterLoc, values),
            ShardDescription = AbilityTextHelper.FormatDescription(a.ShardLoc, values)
        };
    }

    private static Dictionary<string, string> BuildSpecialValueMap(
        IReadOnlyList<CnSpecialValue?>? specialValues)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (specialValues is null)
        {
            return map;
        }

        foreach (var sv in specialValues)
        {
            if (sv is null || string.IsNullOrWhiteSpace(sv.Name))
            {
                continue;
            }

            var floats = sv.ValuesFloat ?? [];
            if (floats.Count == 0)
            {
                continue;
            }

            map[sv.Name] = AbilityTextHelper.FormatSpecialValue(floats);
        }

        return map;
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

        [JsonPropertyName("talents")]
        public List<CnAbilityData?>? Talents { get; set; }
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

        [JsonPropertyName("notes_loc")]
        public List<string>? NotesLoc { get; set; }

        [JsonPropertyName("scepter_loc")]
        public string? ScepterLoc { get; set; }

        [JsonPropertyName("shard_loc")]
        public string? ShardLoc { get; set; }

        [JsonPropertyName("behavior")]
        public string? Behavior { get; set; }

        [JsonPropertyName("cooldowns")]
        public List<double>? Cooldowns { get; set; }

        [JsonPropertyName("mana_costs")]
        public List<double>? ManaCosts { get; set; }

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

        [JsonPropertyName("ability_is_granted_by_scepter")]
        public bool AbilityIsGrantedByScepter { get; set; }

        [JsonPropertyName("ability_is_granted_by_shard")]
        public bool AbilityIsGrantedByShard { get; set; }

        [JsonPropertyName("special_values")]
        public List<CnSpecialValue?>? SpecialValues { get; set; }
    }

    private sealed class CnSpecialValue
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("values_float")]
        public List<double>? ValuesFloat { get; set; }

        [JsonPropertyName("bonuses")]
        public List<CnBonus?>? Bonuses { get; set; }
    }

    private sealed class CnBonus
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public double Value { get; set; }
    }
}
