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

    /// <summary>
    /// 段位奖章图标（打包内嵌 Assets/RankIcons，官方 rank_icons 素材，与 OpenDota 玩家页同规则）。
    /// 打包而非走 CDN：cdn.opendota.com 在部分网络下 DNS 不可达，图标又只有十几张、随版本极少变动。
    /// 未定级用 rank_icon_0；冠绝按天梯排名分层：前 10 名 8c、前 100 名 8b、其余 8。
    /// </summary>
    private const string RankIconsPack = "pack://application:,,,/Assets/RankIcons/";

    public static string? MedalUrl(int? rankTier, int? leaderboardRank = null)
    {
        if (rankTier is null or <= 0)
        {
            return RankIconsPack + "rank_icon_0.png";
        }

        var medal = rankTier.Value / 10;
        return medal switch
        {
            >= 1 and <= 7 => $"{RankIconsPack}rank_icon_{medal}.png",
            8 when leaderboardRank is > 0 and <= 10 => RankIconsPack + "rank_icon_8c.png",
            8 when leaderboardRank is > 0 and <= 100 => RankIconsPack + "rank_icon_8b.png",
            8 => RankIconsPack + "rank_icon_8.png",
            _ => null
        };
    }

    /// <summary>星级覆盖条（rank_star_N 一张画 N 颗星）：冠绝无星级，个位 0 不显示。</summary>
    public static string? StarUrl(int? rankTier)
    {
        if (rankTier is null or <= 0)
        {
            return null;
        }

        var medal = rankTier.Value / 10;
        var star = rankTier.Value % 10;
        if (medal >= 8 || star <= 0)
        {
            return null;
        }

        return $"{RankIconsPack}rank_star_{Math.Clamp(star, 1, 5)}.png";
    }
}
