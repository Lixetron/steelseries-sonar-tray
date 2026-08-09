using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SonarQuickMixer.Controls;

/// <summary>
/// Maps fader normalized value + track ActualHeight → thumb TranslateTransform.Y.
/// values[0] = NormalizedValue (0..1), values[1] = track height in px.
/// </summary>
public sealed class FaderThumbOffsetConverter : IMultiValueConverter
{
    private const double ThumbHeight = 14.0;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return 0.0;
        }

        var normalized = values[0] switch
        {
            float f => f,
            double d => d,
            IConvertible c => c.ToDouble(culture),
            _ => 0.0
        };
        normalized = Math.Clamp(normalized, 0.0, 1.0);

        var trackHeight = values[1] switch
        {
            double d when !double.IsNaN(d) && !double.IsInfinity(d) => d,
            IConvertible c => c.ToDouble(culture),
            _ => 0.0
        };

        if (trackHeight <= ThumbHeight)
        {
            return 0.0;
        }

        return (1.0 - normalized) * (trackHeight - ThumbHeight);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
