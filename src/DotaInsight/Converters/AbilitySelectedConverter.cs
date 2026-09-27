using System.Globalization;
using System.Windows.Data;

namespace DotaInsight.Converters;

/// <summary>判断技能项是否为当前选中项（引用比较），供技能图标选中态使用。</summary>
public sealed class AbilitySelectedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
