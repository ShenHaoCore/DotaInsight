using System.Globalization;
using System.Windows.Data;

namespace DotaInsight.Converters;

/// <summary>
/// 输入 Value、Maximum，输出 Value / Maximum（0–1），
/// 供极简进度条模板的 ScaleTransform 使用，保证填充比例精确。
/// </summary>
public sealed class ProgressRatioConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length >= 2
            && TryDouble(values[0], out var value)
            && TryDouble(values[1], out var maximum)
            && maximum > 0)
        {
            return Math.Clamp(value / maximum, 0, 1);
        }

        return 0d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

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
