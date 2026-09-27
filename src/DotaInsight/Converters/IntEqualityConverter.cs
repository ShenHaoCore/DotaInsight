using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DotaInsight.Converters;

/// <summary>int 与 ConverterParameter 相等判断，供 RadioButton 绑定 Tab 索引双向使用。</summary>
public sealed class IntEqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i
           && int.TryParse(parameter as string, out var p)
           && i == p;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && int.TryParse(parameter as string, out var p)
            ? p
            : System.Windows.Data.Binding.DoNothing;
}

/// <summary>int 与 ConverterParameter 相等 → Visibility，供页签内容面板切换。</summary>
public sealed class IntEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i
           && int.TryParse(parameter as string, out var p)
           && i == p
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
