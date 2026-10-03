using System.Globalization;

namespace DotaInsight.Helpers;

/// <summary>
/// 英雄展示文案本地化（主属性、定位、攻击类型等）。
/// </summary>
public static class HeroDisplayHelper
{
    private static readonly CultureInfo ZhCn = CultureInfo.GetCultureInfo("zh-CN");

    private static readonly Dictionary<string, string> RoleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Carry"] = "核心",
        ["Support"] = "辅助",
        ["Nuker"] = "爆发",
        ["Disabler"] = "控制",
        ["Jungler"] = "打野",
        ["Durable"] = "耐久",
        ["Escape"] = "逃生",
        ["Pusher"] = "推进",
        ["Initiator"] = "先手"
    };

    /// <summary>
    /// 中文定位别名归一：不同数据源对同一角色译名不一致
    /// （如 Durable 有“耐久 / 生存”两种译法），统一到国服九宫格标准名。
    /// </summary>
    private static readonly Dictionary<string, string> ChineseRoleAlias = new(StringComparer.Ordinal)
    {
        ["生存"] = "耐久"
    };

    /// <summary>国服详情「定位」九宫格顺序。</summary>
    public static IReadOnlyList<string> StandardRoles { get; } =
    [
        "核心", "辅助", "爆发",
        "控制", "打野", "耐久",
        "逃生", "推进", "先手"
    ];

    /// <summary>
    /// 将 OpenDota primary_attr 转为中文。
    /// </summary>
    public static string ToChinesePrimaryAttr(string? primaryAttr)
    {
        return (primaryAttr ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "str" or "0" or "力量" => "力量",
            "agi" or "1" or "敏捷" => "敏捷",
            "int" or "2" or "智力" => "智力",
            "all" or "universal" or "3" or "全才" => "全才",
            _ => string.IsNullOrWhiteSpace(primaryAttr) ? "未知" : primaryAttr
        };
    }

    /// <summary>
    /// 近战 / 远程。
    /// </summary>
    public static string ToChineseAttackType(string? attackType)
    {
        return (attackType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "melee" or "近战" => "近战",
            "ranged" or "远程" => "远程",
            _ => string.IsNullOrWhiteSpace(attackType) ? "—" : attackType
        };
    }

    /// <summary>
    /// 英雄定位角色中文化。
    /// </summary>
    public static List<string> ToChineseRoles(IEnumerable<string>? roles)
    {
        if (roles is null)
        {
            return [];
        }

        return roles
            .Select(r => RoleMap.TryGetValue(r, out var zh) ? zh : r)
            .Select(r => ChineseRoleAlias.TryGetValue(r, out var std) ? std : r)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 段位显示名（1–8）。
    /// </summary>
    /// <summary>
    /// 将 OpenDota 分段序号（1-8）转为中文段位名。
    /// 直接复用 <see cref="RankTierFormatter"/>，保证与玩家段位显示同名。
    /// </summary>
    public static string GetBracketName(int bracketIndex1To8)
        => bracketIndex1To8 is >= 1 and <= 8
            ? RankTierFormatter.NameOfMedal(bracketIndex1To8)
            : $"段位{bracketIndex1To8}";

    /// <summary>
    /// 按简体中文排序比较器。
    /// </summary>
    public static StringComparer ChineseNameComparer { get; } = StringComparer.Create(ZhCn, ignoreCase: true);

    /// <summary>
    /// 返回第一个非空白字符串，全部为空则返回 null。
    /// </summary>
    public static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

