using System.Text.Json;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;

namespace DotaInsight.Tests;

/// <summary>
/// 小地图战况层映射：objectives → 建筑推倒时间线（含双基地塔按序消耗、反补、肉山/盾/一血）、
/// 击杀时间轴（只有时间无坐标）、lane_pos 热区聚合。
/// 字段格式对拍 2026-11 真实 /api/matches 响应（match 9077341933）：
/// 建筑事件 key 为完整 NPC 名（npc_dota_badguys_tower1_bot / npc_dota_badguys_tower4 / ..._fort / ..._melee_rax_mid）。
/// </summary>
public class MatchMapTests
{
    private const string FixtureJson = """
        {
          "match_id": 9077341933,
          "radiant_win": true,
          "duration": 3762,
          "objectives": [
            {"time":313,"type":"CHAT_MESSAGE_FIRSTBLOOD","key":"6","slot":4,"player_slot":4},
            {"time":405,"type":"building_kill","unit":"npc_dota_hero_terrorblade","key":"npc_dota_badguys_tower1_bot","slot":0,"player_slot":0},
            {"time":1452,"type":"CHAT_MESSAGE_ROSHAN_KILL","team":3},
            {"time":1452,"type":"CHAT_MESSAGE_AEGIS","slot":0,"player_slot":128},
            {"time":3000,"type":"building_kill","unit":"npc_dota_badguys_siege","key":"npc_dota_goodguys_tower4"},
            {"time":3300,"type":"building_kill","unit":"npc_dota_hero_medusa","key":"npc_dota_goodguys_tower4"},
            {"time":3600,"type":"building_kill","key":"npc_dota_badguys_fort"},
            {"time":3700,"type":"CHAT_MESSAGE_TOWER_DENY","key":"npc_dota_goodguys_tower3_top"}
          ],
          "players": [
            {
              "player_slot": 0,
              "hero_id": 1,
              "kills_log": [
                {"time":588,"key":"npc_dota_hero_pangolier"},
                {"time":600,"key":"npc_dota_hero_medusa"}
              ],
              "lane_pos": {"76": {"76": 12}, "40": {"60": 3}}
            },
            {
              "player_slot": 129,
              "hero_id": 7,
              "kills_log": [
                {"time":366,"key":"npc_dota_hero_medusa"}
              ],
              "lane_pos": {"90": {"30": 8}}
            }
          ]
        }
        """;

    private static readonly Dictionary<int, HeroStat> HeroMap = new()
    {
        [1] = new HeroStat { Id = 1, Name = "npc_dota_hero_terrorblade", LocalizedName = "恐怖利刃" },
        [7] = new HeroStat { Id = 7, Name = "npc_dota_hero_medusa", LocalizedName = "美杜莎" },
        [78] = new HeroStat { Id = 78, Name = "npc_dota_hero_phantom_assassin", LocalizedName = "冥灵刺客" },
        [33] = new HeroStat { Id = 33, Name = "npc_dota_hero_nevermore", LocalizedName = "影魔" }
    };

    private static MatchDetail Map()
    {
        var dto = JsonSerializer.Deserialize<MatchAnalysisService.MatchDetailDto>(
            FixtureJson, HttpCall.JsonOptions);
        Assert.NotNull(dto);
        return MatchAnalysisService.MapMatchDetail(dto!, HeroMap, new Dictionary<int, string>());
    }

    [Fact]
    public void Buildings_FullTable_MappedWithNpcKeys()
    {
        var detail = Map();

        Assert.Equal(36, detail.Buildings.Count); // 22 塔 + 12 兵营 + 2 遗迹
        Assert.All(detail.Buildings, b => Assert.False(string.IsNullOrEmpty(b.NpcKey)));
        Assert.Equal(2, detail.Buildings.Count(b => b.IsAncient));
        Assert.Equal(22, detail.Buildings.Count(b => b.IsTower));
        Assert.Equal(12, detail.Buildings.Count(b => b.IsBarracks));
    }

    [Fact]
    public void Buildings_TowerFallsAtObjectiveTime_WithHeroName()
    {
        var detail = Map();

        var tower = detail.Buildings.Single(b => b.Id == "t1bd");
        Assert.Equal(405, tower.FallsAt);
        Assert.Equal("恐怖利刃", tower.KilledBy);
    }

    [Fact]
    public void Buildings_TwinTowers4_ConsumedInTableOrder()
    {
        var detail = Map();

        var bottom = detail.Buildings.Single(b => b.Id == "t4br");
        var top = detail.Buildings.Single(b => b.Id == "t4tr");
        Assert.Equal(3000, bottom.FallsAt); // 攻城兵拆的
        Assert.Equal("攻城兵", bottom.KilledBy);
        Assert.Equal(3300, top.FallsAt);    // 第二座按序消耗
        Assert.Equal("美杜莎", top.KilledBy);
    }

    [Fact]
    public void Buildings_AncientFalls_TowerDenyFallsToOwner()
    {
        var detail = Map();

        var direAncient = detail.Buildings.Single(b => b.Id == "ad");
        Assert.Equal(3600, direAncient.FallsAt);

        var deniedTower = detail.Buildings.Single(b => b.Id == "t3tr");
        Assert.Equal(3700, deniedTower.FallsAt);
    }

    [Fact]
    public void Building_StandingSemantics_FinalStateForAllTime()
    {
        var detail = Map();

        var tower = detail.Buildings.Single(b => b.Id == "t1bd");
        Assert.True(tower.IsStandingAt(390));
        Assert.False(tower.IsStandingAt(410));
        Assert.False(tower.IsStandingAt(-90)); // 「全部时间」= 终局状态，已倒
        Assert.True(detail.Buildings.Single(b => b.Id == "t1tr").IsStandingAt(-90));
    }

    [Fact]
    public void Events_ObjectivesPlusKills_SortedByTime()
    {
        var detail = Map();

        // objectives：一血+塔+肉山+盾+双塔4+遗迹+反补 = 8；击杀：2 + 1 = 3
        Assert.Equal(11, detail.MapEvents.Count);
        var times = detail.MapEvents.Select(e => e.Time).ToList();
        Assert.Equal(times.OrderBy(t => t).ToList(), times);

        var firstBlood = detail.MapEvents.Single(e => e.Kind == MatchMapEventKind.FirstBlood);
        Assert.True(firstBlood.IsRadiant);

        var roshan = detail.MapEvents.Single(e => e.Kind == MatchMapEventKind.Roshan);
        Assert.False(roshan.IsRadiant); // team=3 = 夜魇击杀

        var aegis = detail.MapEvents.Single(e => e.Kind == MatchMapEventKind.Aegis);
        Assert.False(aegis.IsRadiant);
        Assert.Equal("恐怖利刃 拿到肉山盾", aegis.Label); // slot 0 → 玩家 0 的英雄

        var deny = detail.MapEvents.Single(e => e.Label.StartsWith("反补"));
        Assert.True(deny.IsRadiant); // 反补=失方
    }

    [Fact]
    public void Events_KillTicks_HaveTimeButNoPosition()
    {
        var detail = Map();

        var kills = detail.MapEvents.Where(e => e.Kind == MatchMapEventKind.Kill).ToList();
        Assert.Equal(3, kills.Count);
        var radiantKills = kills.Where(e => e.IsRadiant).ToList();
        Assert.Equal(2, radiantKills.Count);
        Assert.Contains(radiantKills, e => e.Label == "击杀 美杜莎");
    }

    [Fact]
    public void Heatmap_AggregatesBySide()
    {
        var detail = Map();

        Assert.Equal(2, detail.RadiantHeatmap.Count);
        Assert.Equal(12, detail.RadiantHeatmap["76,76"]);
        Assert.Equal(3, detail.RadiantHeatmap["40,60"]);
        Assert.Equal(8, detail.DireHeatmap["90,30"]);
    }

    /// <summary>OMG（模式 18）+ 队长模式的模式专属字段 fixture。</summary>
    private const string ModeFixtureJson = """
        {
          "match_id": 9077341933,
          "radiant_win": true,
          "duration": 2400,
          "game_mode": 18,
          "picks_bans": [
            {"is_pick":false,"hero_id":78,"team":0,"order":0},
            {"is_pick":false,"hero_id":74,"team":1,"order":1},
            {"is_pick":true,"hero_id":33,"team":1,"order":2},
            {"is_pick":true,"hero_id":1,"team":0,"order":3}
          ],
          "teamfights": [
            {"start":1800,"end":1830,"deaths":6,"radiant_gold_adv_delta":1500},
            {"start":2400,"end":2440,"deaths":4,"radiant_gold_adv_delta":-800},
            {"start":2600,"end":2600,"deaths":0}
          ],
          "players": [
            {
              "player_slot": 0,
              "hero_id": 1,
              "abilities": ["antimage_blink", "generic_hidden", null, "lina_laguna_ability", ""]
            },
            {
              "player_slot": 129,
              "hero_id": 7
            }
          ]
        }
        """;

    private static MatchDetail MapMode()
    {
        var dto = JsonSerializer.Deserialize<MatchAnalysisService.MatchDetailDto>(
            ModeFixtureJson, HttpCall.JsonOptions);
        Assert.NotNull(dto);
        return MatchAnalysisService.MapMatchDetail(dto!, HeroMap, new Dictionary<int, string>());
    }

    [Fact]
    public void AbilityDraft_FiltersPlaceholders_KeepsOrder()
    {
        var detail = MapMode();

        Assert.True(detail.IsAbilityDraft);
        var p0 = detail.Radiant.Single();
        // generic_hidden 与空槽位（null / ""）都被剔除，顺序保持
        Assert.Equal(2, p0.DraftedAbilities.Count);
        Assert.Equal("antimage_blink", p0.DraftedAbilities[0]);
        Assert.Equal("lina_laguna_ability", p0.DraftedAbilities[1]);
        // 图标 URL 与技能一一对应
        Assert.Equal(2, p0.DraftedAbilityIconUrls.Count);
        Assert.Contains("abilities/antimage_blink.png", p0.DraftedAbilityIconUrls[0]);
        // 非 OMG 玩家（没选技能）不进入 OMG 列表：dire 玩家无 abilities 字段
        Assert.Empty(detail.Dire.Single().DraftedAbilities);
    }

    [Fact]
    public void DraftSteps_SortedByOrder_WithHeroNames()
    {
        var detail = MapMode();

        Assert.Equal(4, detail.DraftSteps.Count);
        Assert.Equal(0, detail.DraftSteps[0].Order);
        Assert.Equal(3, detail.DraftSteps[^1].Order);

        var ban0 = detail.DraftSteps[0];
        Assert.False(ban0.IsPick);
        Assert.True(ban0.IsRadiant); // team=0 = 天辉
        Assert.Equal("冥灵刺客", ban0.HeroName); // hero_id 78 → HeroMap

        var pick1 = detail.DraftSteps[2];
        Assert.True(pick1.IsPick);
        Assert.False(pick1.IsRadiant); // team=1 = 夜魇
        Assert.False(string.IsNullOrEmpty(pick1.HeroIconUrl));
    }

    [Fact]
    public void Teamfights_EmptyAggregatesFiltered_AttributionByGoldDelta()
    {
        var detail = MapMode();

        var tfs = detail.MapEvents.Where(e => e.Kind == MatchMapEventKind.TeamFight).ToList();
        Assert.Equal(2, tfs.Count); // deaths=0 的空聚合被过滤

        var first = tfs[0];
        Assert.Equal(1800, first.Time);
        Assert.True(first.IsRadiant); // 天辉经济 +1500 → 天辉受益
        Assert.Contains("6 次阵亡", first.Label);
        Assert.Contains("+1500", first.Label);

        Assert.False(tfs[1].IsRadiant); // 天辉 -800 → 夜魇受益
    }
}
