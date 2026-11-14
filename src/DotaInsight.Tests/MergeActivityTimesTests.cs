using DotaInsight.Models;
using DotaInsight.Services;

namespace DotaInsight.Tests;

/// <summary>
/// 热力图数据合并：/matches（历史，可能滞后）∪ /recentMatches（最新约 100 场）按 match_id 去重。
/// 场景来源：部分账号 /matches 索引滞后数月，只看它会热力图最近几个月凭空空白。
/// </summary>
public class MergeActivityTimesTests
{
    private static RecentMatchDto Match(long id, long startTime) => new()
    {
        MatchId = id,
        StartTime = startTime,
        GameMode = 22
    };

    [Fact]
    public void MergesBothSourcesAndDeduplicatesByMatchId()
    {
        var recent = new[] { Match(300, 1700000000), Match(200, 1699000000) };
        var history = new[] { Match(200, 1699000000), Match(100, 1698000000) };

        var times = MatchAnalysisService.MergeActivityTimes(recent, history);

        Assert.Equal(3, times.Count);
        Assert.Contains(1700000000L, times);
        Assert.Contains(1699000000L, times);
        Assert.Contains(1698000000L, times);
    }

    [Fact]
    public void SkipsInvalidRowsAndEmptySources()
    {
        var recent = new[] { Match(0, 1700000000), Match(301, 0) };   // 无效：ID 为 0 / 时间为 0
        var history = new[] { Match(100, 1698000000), Match(-5, 1) }; // 无效：负 ID

        var times = MatchAnalysisService.MergeActivityTimes(recent, history);

        Assert.Single(times);
        Assert.Equal(1698000000L, times[0]);
    }

    [Fact]
    public void HandlesNullSources()
    {
        Assert.Empty(MatchAnalysisService.MergeActivityTimes(null, null));
        var times = MatchAnalysisService.MergeActivityTimes(new[] { Match(1, 123) }, null);
        Assert.Single(times);
    }
}
