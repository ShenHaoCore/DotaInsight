using DotaInsight.Helpers;

namespace DotaInsight.Models;

/// <summary>
/// 英雄克制关系列表项（对位行 / 图表）。
/// </summary>
public sealed class HeroCounterItem
{
    public int HeroId { get; init; }

    public string HeroName { get; init; } = string.Empty;

    public string InternalName { get; init; } = string.Empty;

    public string PrimaryAttr { get; init; } = string.Empty;

    /// <summary>己方对该英雄的胜率（0–100）。</summary>
    public double WinRate { get; init; }

    /// <summary>胜率差相对 50%（正数表示己方优势）。</summary>
    public double WinRateDiff { get; init; }

    public int Matches { get; init; }

    /// <summary>是否克制对方（胜率 &gt; 50%）。</summary>
    public bool IsCountering { get; init; }

    public string IconUrl => HeroAssetHelper.GetIconUrl(InternalName);

    public string WinRateText => $"{WinRate:F1}%";

    public string WinRateDiffText => $"{WinRateDiff:+0.0;-0.0;0.0}%";

    public string MatchesText => Matches >= 10000
        ? $"{Matches / 1000.0:0.#}k"
        : Matches.ToString();
}
