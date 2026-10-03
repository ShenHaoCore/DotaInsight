using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DotaInsight.Converters;

/// <summary>
/// 分段胜率 → 进度条 0–100。
/// 各分段的胜率都挤在 45–60% 之间，若直接按 0–100 映射，8 根条几乎一样长、
/// 强弱差异完全看不出来。这里把 40%–60% 拉满整条轨道（1% 胜率 ≈ 5% 长度），
/// 超出区间的值钳制在轨道内，配合轨道正中的 50% 基准刻度线阅读。
/// </summary>
public sealed class BracketWinRateToProgressConverter : IValueConverter
{
    private const double Floor = 40.0;
    private const double Ceiling = 60.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!TryDouble(value, out var winRate))
        {
            return 0d;
        }

        var ratio = (winRate - Floor) / (Ceiling - Floor);
        return Math.Clamp(ratio, 0, 1) * 100.0;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static bool TryDouble(object? raw, out double result)
    {
        if (raw is double d)
        {
            result = d;
            return true;
        }

        return double.TryParse(raw?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>
/// 分段胜率 → 相对 50% 的强弱色：高于 50% 用有利色（绿），低于用不利色（红），
/// 恰好 50% 用中性色。跟随主题资源，浅深色自动适配。
/// </summary>
public sealed class BracketWinRateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var winRate = value switch
        {
            double d => d,
            _ => double.NaN
        };

        var key = double.IsNaN(winRate)
            ? "DiMutedTextBrush"
            : winRate > 50
                ? "DiAdvantageBrush"
                : winRate < 50
                    ? "DiDisadvantageBrush"
                    : "DiMutedTextBrush";

        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        // 仅在主题字典缺失时兜底；画刷静态冻结，避免每次转换分配
        return double.IsNaN(winRate) || winRate >= 50 ? FallbackAdvantageBrush : FallbackDisadvantageBrush;
    }

    private static readonly Brush FallbackAdvantageBrush = FrozenBrush(0x1F, 0x9D, 0x5B);
    private static readonly Brush FallbackDisadvantageBrush = FrozenBrush(0xD6, 0x3B, 0x32);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
