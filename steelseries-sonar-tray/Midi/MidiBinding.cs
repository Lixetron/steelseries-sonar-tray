using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Midi;

public sealed class MidiBinding
{
    public const string UnassignedChannelId = "";

    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// MIDI CC/note number, or pitch-bend channel index 0-15 when <see cref="IsPitchBend"/> is true
    /// (status byte E0 = 0 … EF = 15).
    /// </summary>
    public int Controller { get; set; }

    public bool IsNote { get; set; }

    /// <summary>True when the binding is driven by Pitch Bend (14-bit), not CC/Note.</summary>
    public bool IsPitchBend { get; set; }

    /// <summary>
    /// Sonar channel id, or empty when the hardware control is discovered but not routed yet.
    /// </summary>
    public string ChannelId { get; set; } = UnassignedChannelId;

    public SonarMixerPath Path { get; set; } = SonarMixerPath.Monitoring;

    public MidiValueMode Mode { get; set; } = MidiValueMode.Absolute;

    public MidiBindingAction Action { get; set; } = MidiBindingAction.None;

    /// <summary>Absolute non-motorized faders participate in hardware-priority rollback.</summary>
    public bool IsMotorized { get; set; }

    public MidiRelativeEncoding RelativeEncoding { get; set; } = MidiRelativeEncoding.OffsetBinary;

    /// <summary>Null uses AppSettings.MidiRelativeStep.</summary>
    public float? RelativeStep { get; set; }

    /// <summary>Optional blueprint control id this binding is linked to.</summary>
    public string? ControlId { get; set; }

    public string BindingKey =>
        $"{DeviceName}|{(IsPitchBend ? "P" : IsNote ? "N" : "C")}|{Controller}";

    public bool HasSonarChannel =>
        !string.IsNullOrWhiteSpace(ChannelId) && SonarChannels.IsValidChannel(ChannelId);

    public bool MatchesHardware(MidiIncomingEvent evt) =>
        Controller == evt.Controller
        && IsNote == evt.IsNote
        && IsPitchBend == evt.IsPitchBend;

    public static string FormatHardwareLabel(bool isNote, int controller, bool isPitchBend = false)
    {
        if (isPitchBend)
        {
            return $"PB {(0xE0 + Math.Clamp(controller, 0, 15)):X2}";
        }

        return isNote ? $"Note {controller}" : $"CC {controller}";
    }

    public static string FormatChannelLabel(string? channelId) =>
        string.IsNullOrWhiteSpace(channelId) || !SonarChannels.IsValidChannel(channelId)
            ? "Not assigned"
            : SonarChannels.GetDisplayName(channelId);
}
