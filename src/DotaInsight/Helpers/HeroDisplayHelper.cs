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

    /// <summary>国服详情「定位」九宫格顺序。</summary>
    public static IReadOnlyList<string> StandardRoles { get; } =
    [
        "核心", "辅助", "爆发",
        "控制", "打野", "耐久",
        "逃生", "推进", "先手"
    ];

    private static readonly string[] BracketNames =
    [
        "纹章", "护卫", "十字军", "中军", "传奇", "万古", "神谕", "不朽"
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
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 段位显示名（1–8）。
    /// </summary>
    public static string GetBracketName(int bracketIndex1To8)
    {
        if (bracketIndex1To8 is >= 1 and <= 8)
        {
            return BracketNames[bracketIndex1To8 - 1];
        }

        return $"段位{bracketIndex1To8}";
    }

    /// <summary>
    /// 按简体中文排序比较器。
    /// </summary>
    public static StringComparer ChineseNameComparer { get; } = StringComparer.Create(ZhCn, ignoreCase: true);
}

