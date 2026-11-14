using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DotaInsight.Converters;

/// <summary>
/// 分段序列 → GitHub 风格迷你图几何体（平滑曲线 + 曲线下渐变填充区 + 端点圆点）。
/// 值 = IReadOnlyList&lt;double&gt;（百分比序列）；纵轴按序列自身的最小/最大值归一化，
/// 全序列持平（极差 &lt; 2 个点）时画在垂直居中位置。序列不足两个点返回 null（不绘制）。
/// ConverterParameter："fill" = 曲线下填充区（闭合），"dot" = 末点圆点，缺省 = 曲线本体。
/// </summary>
public sealed class BracketSparklineConverter : IValueConverter
{
    /// <summary>折线视口宽（px）。</summary>
    public const double ViewportWidth = 72;

    /// <summary>折线视口高（px）。</summary>
    public const double ViewportHeight = 20;

    /// <summary>极差小于该值（百分比点）时视为持平，画在居中位置。</summary>
    private const double FlatSpan = 2.0;

    /// <summary>末点圆点半径（px）。</summary>
    private const double DotRadius = 2.4;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<double> rates || rates.Count < 2)
        {
            return null;
        }

        var stepX = ViewportWidth / (rates.Count - 1);
        List<Point> points;

        if (parameter as string is { } mode && mode.StartsWith("fixed"))
        {
            // 统一刻度（0-30%）：跨行可比——占各分段选取率跨英雄差异大，
            // 若按每条线自身极值归一化，所有曲线会被拉成同一种形状
            const double fixedMax = 30.0;
            points = rates.Select((r, i) => new Point(
                i * stepX,
                ViewportHeight - Math.Min(1.0, r / fixedMax) * ViewportHeight)).ToList();
        }
        else
        {
            var min = rates.Min();
            var max = rates.Max();
            var mid = (min + max) / 2;
            var span = Math.Max(max - min, FlatSpan);
            points = rates.Select((r, i) => new Point(
                i * stepX,
                ViewportHeight / 2 - (r - mid) / span * (ViewportHeight / 2))).ToList();
        }

        var isFixed = (parameter as string)?.StartsWith("fixed") == true;
        if (isFixed)
        {
            var kind = (parameter as string)![5..];
            return kind switch
            {
                "Fill" => BuildArea(points),
                "Dot" => BuildDot(points),
                _ => BuildLine(points)
            };
        }

        var mode = parameter as string;
        return mode switch
        {
            "fill" => BuildArea(points),
            "dot" => BuildDot(points),
            "points" => BuildPoints(points),
            _ => BuildLine(points)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>GitHub 风格折线：直线段连接（不做样条平滑），配数据点圆点。</summary>
    private static Geometry BuildLine(IReadOnlyList<Point> p)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(p[0], false, false);
            for (var i = 1; i < p.Count; i++)
            {
                ctx.LineTo(p[i], true, false);
            }
        }

        geo.Freeze();
        return geo;
    }

    /// <summary>每个数据点一个小圆点（GitHub traffic 图语言）。</summary>
    private static Geometry BuildPoints(IReadOnlyList<Point> p)
    {
        var group = new GeometryGroup();
        foreach (var pt in p)
        {
            var ellipse = new EllipseGeometry(pt, 1.6, 1.6);
            group.Children.Add(ellipse);
        }

        group.Freeze();
        return group;
    }

    /// <summary>曲线下填充区：曲线 + 两端落底 + 闭合。</summary>
    private static Geometry BuildArea(IReadOnlyList<Point> p)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(p[0].X, ViewportHeight), true, true);
            ctx.LineTo(p[0], true, true);
            AppendSpline(ctx, p);
            ctx.LineTo(new Point(p[^1].X, ViewportHeight), true, true);
        }

        geo.Freeze();
        return geo;
    }

    /// <summary>末点圆点：标注最新值。</summary>
    private static Geometry BuildDot(IReadOnlyList<Point> p)
    {
        var geo = new EllipseGeometry(p[^1], DotRadius, DotRadius);
        geo.Freeze();
        return geo;
    }

    /// <summary>过点样条：每段由相邻点推算控制点（Catmull-Rom → Bezier，张力 1/6）。</summary>
    private static void AppendSpline(StreamGeometryContext ctx, IReadOnlyList<Point> p)
    {
        if (p.Count == 2)
        {
            ctx.LineTo(p[1], true, true);
            return;
        }

        for (var i = 0; i < p.Count - 1; i++)
        {
            var p0 = i > 0 ? p[i - 1] : p[i];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = i + 2 < p.Count ? p[i + 2] : p2;

            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.BezierTo(c1, c2, p2, true, true);
        }
    }
}
