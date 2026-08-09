namespace SonarQuickMixer.Midi;

/// <summary>
/// Windows often exposes the same USB MIDI box as a primary name and a secondary
/// <c>MIDIIN2 (Product)</c> port that carries the same controls.
/// </summary>
public static class MidiDevicePortNaming
{
    public static bool IsSecondaryPortName(string name) =>
        name.StartsWith("MIDIIN", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("MIDIOUT", StringComparison.OrdinalIgnoreCase);

    public static string CoreProductName(string name)
    {
        var trimmed = name.Trim();
        var open = trimmed.IndexOf('(');
        var close = trimmed.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            return trimmed[(open + 1)..close].Trim();
        }

        return trimmed;
    }

    public static bool DevicesShareProduct(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || string.Equals(CoreProductName(left), CoreProductName(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="name"/> looks like MIDIIN2 (X) and a non-secondary sibling for X is present.
    /// </summary>
    public static bool IsAutoDuplicatePort(string name, IEnumerable<string> availableNames)
    {
        if (!IsSecondaryPortName(name))
        {
            return false;
        }

        var core = CoreProductName(name);
        return availableNames.Any(other =>
            !string.Equals(other, name, StringComparison.OrdinalIgnoreCase)
            && !IsSecondaryPortName(other)
            && DevicesShareProduct(other, core));
    }

    public static string? PreferPrimaryDeviceName(IEnumerable<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.FirstOrDefault(n => !IsSecondaryPortName(n))
               ?? list.FirstOrDefault();
    }
}
