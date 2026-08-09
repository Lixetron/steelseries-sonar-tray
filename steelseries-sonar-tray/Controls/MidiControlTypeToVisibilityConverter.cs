using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SonarQuickMixer.Midi;

namespace SonarQuickMixer.Controls;

public sealed class MidiControlTypeToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MidiControlType type || parameter is null)
        {
            return Visibility.Collapsed;
        }

        var wanted = parameter.ToString();
        return string.Equals(type.ToString(), wanted, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
