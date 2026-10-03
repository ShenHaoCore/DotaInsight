using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotaInsight.Helpers;

/// <summary>
/// 英雄静态资源地址（Steam CDN 头像 + 矢量属性图标）。
/// </summary>
public static class HeroAssetHelper
{
    private const string HeroIconBase =
        "https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/";

    private const string PackIconBase = "pack://application:,,,/DotaInsight;component/Assets/Icons/";
    private const string StatIconBase = PackIconBase + "stats/";

    /// <summary>三维主属性：官方矢量图标（HeroIcons.xaml，任意 DPI 清晰）。</summary>
    public static ImageSource StrIcon => GetVectorIcon("HeroStrengthIcon");
    public static ImageSource AgiIcon => GetVectorIcon("HeroAgilityIcon");
    public static ImageSource IntIcon => GetVectorIcon("HeroIntelligenceIcon");
    public static ImageSource UniIcon => GetVectorIcon("HeroUniversalIcon");

    public static string HealthIcon { get; } = PackIcon("icon_health.png");
    public static string ManaIcon { get; } = PackIcon("icon_mana.png");

    /// <summary>
    /// 战斗属性图标：与 dota2.com 官网英雄页同一套 PNG（dota_react/heroes/stats/）。
    /// 直接显示原图（中灰主体 + 深色细节 + alpha 渐变），浅色/深色主题下都有足够对比度；
    /// 不要用 OpacityMask 染成单色 —— 那会压平官网素材的灰度层次（眼睛变实心椭圆、飞靴丢翅膀）。
    /// </summary>
    public static ImageSource ArmorIcon => StatIcon("icon_armor.png");
    public static ImageSource MagicResistIcon => StatIcon("icon_magic_resist.png");
    public static ImageSource DamageIcon => StatIcon("icon_damage.png");
    public static ImageSource AttackTimeIcon => StatIcon("icon_attack_time.png");
    public static ImageSource AttackRangeIcon => StatIcon("icon_attack_range.png");
    public static ImageSource ProjectileSpeedIcon => StatIcon("icon_projectile_speed.png");
    public static ImageSource MoveSpeedIcon => StatIcon("icon_movement_speed.png");
    public static ImageSource TurnRateIcon => StatIcon("icon_turn_rate.png");
    public static ImageSource VisionIcon => StatIcon("icon_vision.png");

    private static string PackIcon(string fileName) => PackIconBase + fileName;

    private static ImageSource StatIcon(string fileName)
    {
        var bmp = new BitmapImage(new Uri(StatIconBase + fileName, UriKind.Absolute));
        if (bmp.CanFreeze)
        {
            bmp.Freeze();
        }

        return bmp;
    }

    private static ImageSource GetVectorIcon(string resourceKey) =>
        (ImageSource)(Application.Current?.Resources[resourceKey]
            ?? throw new InvalidOperationException($"未找到矢量图标资源：{resourceKey}"));

    /// <summary>主属性对应官方图标。</summary>
    public static ImageSource GetPrimaryAttrIcon(string? primaryAttr) => primaryAttr switch
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
