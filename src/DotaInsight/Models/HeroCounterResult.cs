namespace DotaInsight.Models;

/// <summary>
/// 某英雄的克制分析结果。
/// </summary>
public sealed class HeroCounterResult
{
    public int SelectedHeroId { get; init; }

    public string SelectedHeroName { get; init; } = string.Empty;

    /// <summary>克制己方的敌方英雄（己方胜率偏低）。</summary>
    public IReadOnlyList<HeroCounterItem> CounteredBy { get; init; } = Array.Empty<HeroCounterItem>();

    /// <summary>己方克制的敌方英雄（己方胜率偏高）。</summary>
    public IReadOnlyList<HeroCounterItem> Counters { get; init; } = Array.Empty<HeroCounterItem>();

    /// <summary>数据是否来自本地缓存。</summary>
    public bool FromCache { get; init; }

    /// <summary>是否处于离线/降级模式。</summary>
    public bool IsOffline { get; init; }

    public bool IsEmpty => CounteredBy.Count == 0 && Counters.Count == 0;
}
