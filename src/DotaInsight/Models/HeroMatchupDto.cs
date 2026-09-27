using System.Text.Json.Serialization;

namespace DotaInsight.Models;

/// <summary>
/// OpenDota /api/heroes/{id}/matchups 单条对位数据。
/// wins / games_played 表示「己方英雄」对阵该敌方英雄的战绩。
/// 仅英雄克制服务内部使用（网络 / 缓存）。
/// </summary>
internal sealed class HeroMatchupDto
{
    [JsonPropertyName("hero_id")]
    public int HeroId { get; init; }

    [JsonPropertyName("games_played")]
    public int GamesPlayed { get; init; }

    [JsonPropertyName("wins")]
    public int Wins { get; init; }
}
