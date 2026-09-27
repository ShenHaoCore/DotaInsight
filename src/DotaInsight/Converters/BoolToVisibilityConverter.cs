using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DotaInsight.Converters;

/// <summary>
/// bool → Visibility；ConverterParameter=Invert 反转。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// null → Collapsed，非 null → Visible；Invert 反转。
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 胜率差绝对值映射到进度条 0–100（以 15% 为满格）。
/// </summary>
public sealed class AdvantageToProgressConverter : IValueConverter
{
    private const double FullScale = 15.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double diff)
        {
            return 0d;
        }

        var ratio = Math.Min(1.0, Math.Abs(diff) / FullScale);
        return ratio * 100.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 主属性 → 属性色画刷（力量/敏捷/智力/全才）。
/// </summary>
public sealed class AttrBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var attr = (value as string ?? string.Empty).Trim();
        var color = attr switch
        {
            "力量" or "str" or "0" => Color.FromRgb(0xC2, 0x3C, 0x2A),
            "敏捷" or "agi" or "1" => Color.FromRgb(0x2F, 0xCB, 0x7A),
            "智力" or "int" or "2" => Color.FromRgb(0x4A, 0x8E, 0xF0),
            "全才" or "all" or "universal" or "3" => Color.FromRgb(0xC9, 0xA2, 0x27),
            _ => Color.FromRgb(0x8B, 0x98, 0xA8)
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// bool → WPF-UI Appearance：true=Primary，false=Secondary。
/// </summary>
public sealed class BoolToAppearanceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 头像 URL → 缓存 ImageSource。
/// ConverterParameter 可传解码宽度（默认 160）。
/// </summary>
public sealed class CachedImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var decodeWidth = 160;
        if (parameter is string s && int.TryParse(s, out var w) && w > 0)
        {
            decodeWidth = w;
        }

        return Helpers.HeroImageCache.Get(value as string, decodeWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 有利 / 不利画刷，跟随当前主题资源。
/// </summary>
public sealed class AdvantageBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var advantage = value switch
        {
            bool isCountering => isCountering,
            double diff => diff >= 0,
            _ => true
        };

        var key = advantage ? "DiAdvantageBrush" : "DiDisadvantageBrush";
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return advantage
            ? new SolidColorBrush(Color.FromRgb(0x2F, 0xD5, 0x7F))
            : new SolidColorBrush(Color.FromRgb(0xF0, 0x56, 0x4A));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// ItemsControl 从 0 开始的 AlternationIndex → 从 1 开始的排名文本。
/// </summary>
public sealed class IndexPlusOneConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? (i + 1).ToString("00", culture) : "00";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// int Count → Visibility；大于 0 显示，否则隐藏。
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
