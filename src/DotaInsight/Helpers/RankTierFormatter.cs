namespace DotaInsight.Helpers;

/// <summary>
/// OpenDota rank_tier 数值 → 中文段位文本。
/// 十位为段位（1-8），个位为星级（0-5），例如 63 → 万古 3。
/// 玩家资料、账户卡片、英雄详情的分段页签共用，避免多处各写一份换算。
/// </summary>
public static class RankTierFormatter
{
    /// <summary>段位名（1-8），采用国服官方译名。</summary>
    private static readonly string[] MedalNames =
    [
        "先锋", "卫士", "中军", "统将", "传奇", "万古", "超凡", "冠绝"
    ];

    /// <summary>段位序号（1-8）→ 中文段位名。</summary>
    public static string NameOfMedal(int medal)
        => medal is >= 1 and <= 8 ? MedalNames[medal - 1] : "未知";

    public static string Format(int? rankTier)
    {
        if (rankTier is null or 0)
        {
            return "未定级";
        }

        var medal = rankTier.Value / 10;
        var stars = rankTier.Value % 10;
        var name = NameOfMedal(medal);
        return stars > 0 ? $"{name} {stars}" : name;
    }
}
