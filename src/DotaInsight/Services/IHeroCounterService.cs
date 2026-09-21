using DotaInsight.Models;

namespace DotaInsight.Services;

/// <summary>
/// 英雄克制关系服务。
/// </summary>
public interface IHeroCounterService
{
    /// <summary>获取全部英雄列表（优先缓存）。</summary>
    Task<IReadOnlyList<HeroStat>> GetHeroesAsync(CancellationToken cancellationToken = default);

    /// <summary>根据己方英雄计算被克制 / 克制列表。</summary>
    Task<HeroCounterResult> GetCounterRelationsAsync(
        int heroId,
        CancellationToken cancellationToken = default);
}
