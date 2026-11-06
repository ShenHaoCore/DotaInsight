using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotaInsight.Models;

namespace DotaInsight.Helpers;

/// <summary>
/// 每日游玩热力图的渲染器：把 53×7 的日历画成一张预渲染位图。
/// 相比逐格 Border（371 个带触发器样式的元素 + 嵌套 ItemsControl），
/// 单张冻结位图让页面布局与滚动近乎零成本；悬浮提示由页面对
/// MouseMove 按坐标反查（单元格尺寸见常量）。
/// </summary>
public static class ActivityCalendarRenderer
{
    /// <summary>单元格边长（px）。</summary>
    public const double CellSize = 12;

    /// <summary>单元格间距（px，两侧各一半）。</summary>
    public const double GapSize = 2;

    /// <summary>无活动格：中性半透明灰，明暗主题下都轻于卡片底色。</summary>
    private static readonly Color EmptyColor = Color.FromArgb(0x1A, 0x80, 0x8A, 0x98);

    /// <summary>活跃等级的绿色阶（与胜负语义绿一致，透明度递增）。</summary>
    private static readonly Color[] LevelColors =
    [
        Color.FromArgb(0x40, 0x2F, 0xD5, 0x7F),
        Color.FromArgb(0x73, 0x2F, 0xD5, 0x7F),
        Color.FromArgb(0xA6, 0x2F, 0xD5, 0x7F),
        Color.FromArgb(0xE6, 0x2F, 0xD5, 0x7F)
    ];

    /// <summary>渲染日历位图；无周数据返回 null。</summary>
    public static ImageSource? Render(IReadOnlyList<PlayerActivityWeek> weeks)
    {
        if (weeks.Count == 0)
        {
            return null;
        }

        var width = (int)(weeks.Count * (CellSize + GapSize));
        var height = (int)(7 * (CellSize + GapSize));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            for (var w = 0; w < weeks.Count; w++)
            {
                var days = weeks[w].Days;
                for (var d = 0; d < days.Count && d < 7; d++)
                {
                    var day = days[d];
                    if (day.Level < 0)
                    {
                        continue; // 未来格：不绘制（透明）
                    }

                    var brush = new SolidColorBrush(
                        day.Level == 0 ? EmptyColor : LevelColors[Math.Min(day.Level - 1, LevelColors.Length - 1)]);
                    brush.Freeze();

                    var rect = new Rect(
                        w * (CellSize + GapSize) + GapSize / 2,
                        d * (CellSize + GapSize) + GapSize / 2,
                        CellSize,
                        CellSize);
                    dc.DrawRectangle(brush, null, rect);
                }
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>位图总宽（供 Image 元素定宽）。</summary>
    public static double WidthFor(int weekCount) => weekCount * (CellSize + GapSize);
}
