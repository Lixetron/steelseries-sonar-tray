using System.Globalization;
using System.Windows.Data;

namespace SonarQuickMixer.Controls;

public sealed class SliderLevelWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not double width ||
            values[1] is not double level)
        {
            return 0d;
        }

        return Math.Max(0, width * Math.Clamp(level, 0, 1));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
