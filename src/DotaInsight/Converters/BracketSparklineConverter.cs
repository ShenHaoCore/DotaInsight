using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DotaInsight.Converters;

/// <summary>
/// 分段序列 → 迷你折线图的顶点集合（Polyline.Points）。
/// 值 = IReadOnlyList&lt;double&gt;（百分比序列）；纵轴按序列自身的最小/最大值归一化，
/// 全序列持平（极差 &lt; 2 个点）时画在垂直居中位置。序列不足两个点返回 null（不绘制）。
/// </summary>
public sealed class BracketSparklineConverter : IValueConverter
{
    /// <summary>折线视口宽（px）。</summary>
    public const double ViewportWidth = 72;

    /// <summary>折线视口高（px）。</summary>
    public const double ViewportHeight = 20;

    /// <summary>极差小于该值（百分比点）时视为持平，画在居中位置。</summary>
    private const double FlatSpan = 2.0;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<double> rates || rates.Count < 2)
        {
            return null;
        }

        var min = rates.Min();
        var max = rates.Max();
        var mid = (min + max) / 2;
        var span = Math.Max(max - min, FlatSpan);

        var stepX = ViewportWidth / (rates.Count - 1);
        var points = rates.Select((r, i) => new Point(
            i * stepX,
            ViewportHeight / 2 - (r - mid) / span * (ViewportHeight / 2)));

        var collection = new PointCollection(points);
        collection.Freeze();
        return collection;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
