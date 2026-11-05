namespace DotaInsight.Helpers;

/// <summary>
/// 小地图建筑坐标表与 objectives 事件的映射。
/// 坐标取自 odota/web BuildingMap/buildingData733（7.33+ 地形，官方覆盖层），
/// 百分比相对整张小地图，直接乘画布边长即可定位。
/// NPC key 与 OpenDota objectives 的 building_kill key 一致
/// （如 npc_dota_goodguys_tower1_top；两座基地塔共用 tower4，按表序消耗）。
/// </summary>
public static class MatchBuildingTable
{
    public sealed record Entry(
        string Id,
        string NpcKey,
        string DisplayName,
        bool IsRadiant,
        bool IsTower,
        bool IsBarracks,
        bool IsAncient,
        double LeftPct,
        double TopPct);

    private static Entry Tower(string id, string npcKey, string name, bool radiant, double left, double top)
        => new(id, npcKey, name, radiant, IsTower: true, IsBarracks: false, IsAncient: false, left, top);

    private static Entry Barracks(string id, string npcKey, string name, bool radiant, double left, double top)
        => new(id, npcKey, name, radiant, IsTower: false, IsBarracks: true, IsAncient: false, left, top);

    public static IReadOnlyList<Entry> All { get; } =
    [
        // ===== 天辉（左下）=====
        Tower("t4br", "npc_dota_goodguys_tower4", "天辉基地塔（下）", true, 12, 82),
        Tower("t4tr", "npc_dota_goodguys_tower4", "天辉基地塔（上）", true, 8, 79),
        Tower("t3br", "npc_dota_goodguys_tower3_bot", "天辉下路三塔", true, 23, 83.5),
        Tower("t2br", "npc_dota_goodguys_tower2_bot", "天辉下路二塔", true, 46, 85),
        Tower("t1br", "npc_dota_goodguys_tower1_bot", "天辉下路一塔", true, 75, 82),
        Tower("t3mr", "npc_dota_goodguys_tower3_mid", "天辉中路三塔", true, 18.5, 71.5),
        Tower("t2mr", "npc_dota_goodguys_tower2_mid", "天辉中路二塔", true, 27.5, 63),
        Tower("t1mr", "npc_dota_goodguys_tower1_mid", "天辉中路一塔", true, 38, 54),
        Tower("t3tr", "npc_dota_goodguys_tower3_top", "天辉上路三塔", true, 8, 68),
        Tower("t2tr", "npc_dota_goodguys_tower2_top", "天辉上路二塔", true, 9, 53),
        Tower("t1tr", "npc_dota_goodguys_tower1_top", "天辉上路一塔", true, 9.5, 36),
        Barracks("brbr", "npc_dota_goodguys_range_rax_bot", "天辉下路远程兵营", true, 20, 80.5),
        Barracks("bmbr", "npc_dota_goodguys_melee_rax_bot", "天辉下路近战兵营", true, 20, 84.5),
        Barracks("brmr", "npc_dota_goodguys_range_rax_mid", "天辉中路远程兵营", true, 15, 72),
        Barracks("bmmr", "npc_dota_goodguys_melee_rax_mid", "天辉中路近战兵营", true, 18, 74.5),
        Barracks("brtr", "npc_dota_goodguys_range_rax_top", "天辉上路远程兵营", true, 6.5, 70.5),
        Barracks("bmtr", "npc_dota_goodguys_melee_rax_top", "天辉上路近战兵营", true, 10.5, 70.5),
        new("ar", "npc_dota_goodguys_fort", "天辉遗迹", true, false, false, true, 5, 83),

        // ===== 夜魇（右上）=====
        Tower("t4bd", "npc_dota_badguys_tower4", "夜魇基地塔（下）", false, 84, 16),
        Tower("t4td", "npc_dota_badguys_tower4", "夜魇基地塔（上）", false, 81, 13),
        Tower("t3bd", "npc_dota_badguys_tower3_bot", "夜魇下路三塔", false, 85.5, 28),
        Tower("t2bd", "npc_dota_badguys_tower2_bot", "夜魇下路二塔", false, 85, 45),
        Tower("t1bd", "npc_dota_badguys_tower1_bot", "夜魇下路一塔", false, 84, 60),
        Tower("t3md", "npc_dota_badguys_tower3_mid", "夜魇中路三塔", false, 73, 24),
        Tower("t2md", "npc_dota_badguys_tower2_mid", "夜魇中路二塔", false, 64, 33),
        Tower("t1md", "npc_dota_badguys_tower1_mid", "夜魇中路一塔", false, 51, 43.5),
        Tower("t3td", "npc_dota_badguys_tower3_top", "夜魇上路三塔", false, 70, 12.5),
        Tower("t2td", "npc_dota_badguys_tower2_top", "夜魇上路二塔", false, 49, 11),
        Tower("t1td", "npc_dota_badguys_tower1_top", "夜魇上路一塔", false, 18, 12),
        Barracks("brbd", "npc_dota_badguys_range_rax_bot", "夜魇下路远程兵营", false, 84, 24),
        Barracks("bmbd", "npc_dota_badguys_melee_rax_bot", "夜魇下路近战兵营", false, 88, 24),
        Barracks("brmd", "npc_dota_badguys_range_rax_mid", "夜魇中路远程兵营", false, 74.5, 19.5),
        Barracks("bmmd", "npc_dota_badguys_melee_rax_mid", "夜魇中路近战兵营", false, 77.5, 22),
        Barracks("brtd", "npc_dota_badguys_range_rax_top", "夜魇上路远程兵营", false, 74, 10),
        Barracks("bmtd", "npc_dota_badguys_melee_rax_top", "夜魇上路近战兵营", false, 74, 14),
        new("ad", "npc_dota_badguys_fort", "夜魇遗迹", false, false, false, true, 84, 9),
    ];
}
