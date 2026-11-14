using SonarQuickMixer.Localization;
using SonarQuickMixer.Settings;

namespace SonarQuickMixer.Midi;

public static class MidiDeviceInfoFormatter
{
    /// <summary>
    /// Header line listing MIDI controllers that currently drive Sonar channels.
    /// Returns null when MIDI control is off, the setting is disabled, or no device is in use.
    /// </summary>
    public static string? Format(IReadOnlyList<string>? deviceNames, AppSettings settings)
    {
        if (deviceNames is null || !settings.MidiEnabled || !settings.ShowMidiDeviceName)
        {
            return null;
        }

        var names = deviceNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0
            ? null
            : Loc.Format("midi.deviceInfo", string.Join(", ", names));
    }
}
