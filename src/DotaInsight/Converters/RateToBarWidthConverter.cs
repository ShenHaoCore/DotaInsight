using System.Globalization;
using System.Windows.Data;

namespace DotaInsight.Converters;

/// <summary>
/// 选取率（%）→ 迷你条形宽度。分段占比/分段选取率在英雄间几乎同形或同水平，
/// 折线没有区分度；改用公共刻度的长度条：宽度 = min(值, 满刻度)/满刻度 × 总宽。
/// ConverterParameter 为满刻度值（如高选取榜 3.0）。
/// </summary>
public sealed class RateToBarWidthConverter : IValueConverter
{
    /// <summary>条形总宽（px），与原折线视口等宽。</summary>
    public const double TotalWidth = 72;

    /// <summary>最小可见宽度（px）：极小值也留一点可读长度。</summary>
    public const double MinWidth = 6;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var rate = value is double d ? d : 0;
        var max = parameter is string s && double.TryParse(s, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 3.0;
        var ratio = Math.Clamp(rate / max, 0, 1);
        return Math.Max(MinWidth, ratio * TotalWidth);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
