using System.Text.Json;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;

namespace DotaInsight.Tests;

/// <summary>
/// 比赛详情映射：优势曲线、眼位日志（含 null 容错）与 UV 坐标变换。
/// 坐标变换契约来自 odota/web utility.tsx gameCoordToUV：u = x−64，v = 191−y。
/// </summary>
public class MatchDetailMappingTests
{
    private const string FixtureJson = """
        {
          "match_id": 9022423471,
          "radiant_win": true,
          "duration": 2044,
          "radiant_gold_adv": [0, 120, null, -50],
          "radiant_xp_adv": null,
          "players": [
            {
              "player_slot": 0,
              "hero_id": 1,
              "kills": 5,
              "obs_log": [
                {"x": 139.6, "y": 89.7, "time": -61, "player_slot": 0, "ehandle": 1001},
                {"x": 75.6, "y": 155.8, "time": 120, "player_slot": 3, "ehandle": 1002}
              ],
              "obs_left_log": [
                {"x": 139.6, "y": 89.7, "time": 299, "player_slot": 0, "ehandle": 1001}
              ],
              "sen_log": null
            },
            {
              "player_slot": 129,
              "hero_id": 7,
              "kills": 2,
              "sen_log": [
                {"x": 89.9, "y": 155.8, "time": 47, "player_slot": 129}
              ]
            },
            {
              "player_slot": 5,
              "hero_id": 8,
              "kills": 1,
              "obs_log": null
            }
          ]
        }
        """;

    private static MatchDetail Map()
    {
        var dto = JsonSerializer.Deserialize<MatchAnalysisService.MatchDetailDto>(
            FixtureJson, HttpCall.JsonOptions);
        Assert.NotNull(dto);
        return MatchAnalysisService.MapMatchDetail(dto!, new Dictionary<int, HeroStat>(), new Dictionary<int, string>());
    }

    [Fact]
    public void Map_GoldAdvantage_KeepsNullGaps()
    {
        var detail = Map();

        Assert.NotNull(detail.GoldAdvantage);
        Assert.Equal(4, detail.GoldAdvantage!.Count);
        Assert.Equal(0, detail.GoldAdvantage[0]);
        Assert.Null(detail.GoldAdvantage[2]);   // null 元素保留 → 曲线断点
        Assert.Equal(-50, detail.GoldAdvantage[3]);
        Assert.Null(detail.XpAdvantage);        // 缺失整段 → null
    }

    [Fact]
    public void Map_Wards_AggregatesTeamsAndTypes()
    {
        var detail = Map();

        Assert.Equal(3, detail.Wards.Count);

        var obs = detail.Wards[0];
        Assert.True(obs.IsObserver);
        Assert.True(obs.IsRadiant);             // player_slot 0
        Assert.Equal(-61, obs.Time);

        var obs2 = detail.Wards[1];
        Assert.True(obs2.IsRadiant);            // 日志自带 player_slot 3
        Assert.Equal(120, obs2.Time);

        var sen = detail.Wards[2];
        Assert.False(sen.IsObserver);           // sen_log → 岗哨
        Assert.False(sen.IsRadiant);            // player_slot 129
        Assert.Equal(47, sen.Time);

        // ehandle 配对：1001 有消失记录（299s），1002 无 → null
        Assert.Equal(299, detail.Wards[0].LeftTime);
        Assert.Null(detail.Wards[1].LeftTime);
    }

    [Fact]
    public void Ward_IsVisibleAt_FollowsOdotaAliveSemantics()
    {
        // 布置 -61、消失 299 的观察眼
        var ward = new MatchWardPlacement { Time = -61, LeftTime = 299 };

        Assert.True(ward.IsVisibleAt(-90));    // 全部时间恒可见
        Assert.True(ward.IsVisibleAt(0));      // 对局开始后存活
        Assert.True(ward.IsVisibleAt(298));
        Assert.False(ward.IsVisibleAt(299));   // 消失时刻即不可见
        Assert.False(ward.IsVisibleAt(600));

        // 无消失记录（未被拆除）→ 一直存活
        var permanent = new MatchWardPlacement { Time = 100, LeftTime = null };
        Assert.True(permanent.IsVisibleAt(100));
        Assert.True(permanent.IsVisibleAt(3000));

        // t < 0 = 全部时间语义：未布置的也显示
        var future = new MatchWardPlacement { Time = 500, LeftTime = null };
        Assert.True(future.IsVisibleAt(-90));
        Assert.False(future.IsVisibleAt(100));
    }

    [Fact]
    public void Map_Wards_UvTransform_MatchesOdotaFormula()
    {
        var detail = Map();

        // odota/web utility.tsx gameCoordToUV：u = x−64，v = 191−y
        var obs = detail.Wards[0];
        Assert.Equal(139.6 - 64, obs.UvX, 6);
        Assert.Equal(191 - 89.7, obs.UvY, 6);

        // 全部点位必须落在 0-127 网格内（换算正确性的整体校验）
        foreach (var w in detail.Wards)
        {
            Assert.InRange(w.UvX, 0, 127);
            Assert.InRange(w.UvY, 0, 127);
        }
    }

    [Fact]
    public void Map_NoLogs_YieldsEmptyWards()
    {
        var dto = JsonSerializer.Deserialize<MatchAnalysisService.MatchDetailDto>(
            """{"match_id":1,"players":[{"player_slot":0,"hero_id":1,"obs_log":null}]}""",
            HttpCall.JsonOptions);
        Assert.NotNull(dto);

        var detail = MatchAnalysisService.MapMatchDetail(dto!, new Dictionary<int, HeroStat>(), new Dictionary<int, string>());

        Assert.Empty(detail.Wards);
    }
}
