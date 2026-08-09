using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SonarQuickMixer.Midi;

namespace SonarQuickMixer.Controls;

public sealed class MidiDropZoneToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is null)
        {
            return Visibility.Collapsed;
        }

        var zoneName = value switch
        {
            MidiLayoutDropZone zone => zone.ToString(),
            string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
            _ => null
        };

        if (zoneName is null)
        {
            return Visibility.Collapsed;
        }

        return string.Equals(zoneName, parameter.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
