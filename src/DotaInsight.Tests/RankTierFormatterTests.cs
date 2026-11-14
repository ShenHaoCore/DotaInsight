using DotaInsight.Helpers;

namespace DotaInsight.Tests;

public class RankTierFormatterTests
{
    [Theory]
    [InlineData(null, "未定级")]
    [InlineData(0, "未定级")]
    [InlineData(10, "先锋")]
    [InlineData(63, "万古 3")]
    [InlineData(77, "超凡 7")]
    [InlineData(80, "冠绝")]
    public void Format_MapsTierAndStars(int? rankTier, string expected)
    {
        Assert.Equal(expected, RankTierFormatter.Format(rankTier));
    }

    [Theory]
    [InlineData(null, "rank_icon_0.png")]
    [InlineData(0, "rank_icon_0.png")]
    [InlineData(63, "rank_icon_6.png")]
    [InlineData(80, "rank_icon_8.png")]
    public void MedalUrl_PicksMedalByTens(int? rankTier, string expectedFile)
    {
        Assert.EndsWith(expectedFile, RankTierFormatter.MedalUrl(rankTier));
    }

    [Fact]
    public void MedalUrl_ImmortalLeaderboardStrata()
    {
        // 天梯榜分层：前 10 名 8c、前 100 名 8b、其余 8
        Assert.EndsWith("rank_icon_8c.png", RankTierFormatter.MedalUrl(80, 5));
        Assert.EndsWith("rank_icon_8b.png", RankTierFormatter.MedalUrl(80, 42));
        Assert.EndsWith("rank_icon_8.png", RankTierFormatter.MedalUrl(80, 101));
        Assert.EndsWith("rank_icon_8.png", RankTierFormatter.MedalUrl(80, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(80)]
    public void StarUrl_NullForUncalibratedAndImmortal(int? rankTier)
    {
        Assert.Null(RankTierFormatter.StarUrl(rankTier));
    }

    [Theory]
    [InlineData(61, "rank_star_1.png")]
    [InlineData(63, "rank_star_3.png")]
    [InlineData(75, "rank_star_5.png")]
    public void StarUrl_MapsUnitsDigitToStarStrip(int? rankTier, string expectedFile)
    {
        Assert.EndsWith(expectedFile, RankTierFormatter.StarUrl(rankTier));
    }

    [Fact]
    public void StarUrl_NullForZeroStars()
    {
        // 60 = 刚到达万古（0 星），不出星条
        Assert.Null(RankTierFormatter.StarUrl(60));
    }
}
