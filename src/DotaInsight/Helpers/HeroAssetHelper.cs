namespace DotaInsight.Helpers;

/// <summary>
/// 英雄静态资源地址（Steam CDN 头像 + 本地属性图标）。
/// </summary>
public static class HeroAssetHelper
{
    private const string HeroIconBase =
        "https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/";

    private const string PackIconBase = "pack://application:,,,/Assets/Icons/";

    public static string StrIcon { get; } = PackIcon("hero_strength.png");
    public static string AgiIcon { get; } = PackIcon("hero_agility.png");
    public static string IntIcon { get; } = PackIcon("hero_intelligence.png");
    public static string UniIcon { get; } = PackIcon("hero_universal.png");

    public static string HealthIcon { get; } = PackIcon("icon_health.png");
    public static string ManaIcon { get; } = PackIcon("icon_mana.png");
    public static string ArmorIcon { get; } = PackIcon("icon_armor.png");
    public static string MagicResistIcon { get; } = PackIcon("icon_magic_resist.png");
    public static string DamageIcon { get; } = PackIcon("icon_damage.png");
    public static string AttackTimeIcon { get; } = PackIcon("icon_attack_time.png");
    public static string AttackRangeIcon { get; } = PackIcon("icon_attack_range.png");
    public static string MoveSpeedIcon { get; } = PackIcon("icon_movement_speed.png");
    public static string VisionIcon { get; } = PackIcon("icon_vision.png");

    private static string PackIcon(string fileName) => PackIconBase + fileName;

    /// <summary>主属性对应官方图标。</summary>
    public static string GetPrimaryAttrIcon(string? primaryAttr) => primaryAttr switch
    {
        "力量" or "str" => StrIcon,
        "敏捷" or "agi" => AgiIcon,
        "智力" or "int" => IntIcon,
        "全才" or "all" or "universal" => UniIcon,
        _ => UniIcon
    };

    /// <summary>
    /// 由内部名生成头像 URL，如 npc_dota_hero_antimage → antimage.png。
    /// </summary>
    public static string GetIconUrl(string? internalName)
    {
        var key = GetHeroKey(internalName);
        return string.IsNullOrWhiteSpace(key)
            ? string.Empty
            : $"{HeroIconBase}{key}.png";
    }

    /// <summary>
    /// 小图标（国服/Steam icons 目录）。
    /// </summary>
    public static string GetSmallIconUrl(string? internalName)
    {
        var key = GetHeroKey(internalName);
        return string.IsNullOrWhiteSpace(key)
            ? string.Empty
            : $"{HeroIconBase}icons/{key}.png";
    }

    /// <summary>npc_dota_hero_elder_titan → elder_titan</summary>
    public static string GetHeroKey(string? internalName)
    {
        if (string.IsNullOrWhiteSpace(internalName))
        {
            return string.Empty;
        }

        const string prefix = "npc_dota_hero_";
        return internalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? internalName[prefix.Length..]
            : internalName;
    }

    /// <summary>elder_titan → Elder Titan</summary>
    public static string GetEnglishDisplayName(string? internalName)
    {
        var key = GetHeroKey(internalName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return string.Join(' ', key.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0
                ? part
                : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }
}
