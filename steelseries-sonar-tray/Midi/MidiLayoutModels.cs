using System.Text.Json.Serialization;

namespace SonarQuickMixer.Midi;

public sealed class MidiDeviceLayout
{
    public string Name { get; set; } = "Custom";

    public List<string> DeviceMatch { get; set; } = [];

    /// <summary>Optional tip shown in MIDI Setup for this layout.</summary>
    public string? Hint { get; set; }

    /// <summary>
    /// Canvas extent metadata derived from root areas/controls (<see cref="MidiLayoutTreeOps.SyncRootGridExtent"/>).
    /// </summary>
    public int Columns { get; set; } = 4;

    /// <summary>
    /// Canvas extent metadata derived from root areas/controls (<see cref="MidiLayoutTreeOps.SyncRootGridExtent"/>).
    /// </summary>
    public int Rows { get; set; } = 3;


    /// <summary>
    /// Nested visual groups. Empty list is fine — root <see cref="Controls"/> still render as a cell tree.
    /// </summary>
    public List<MidiLayoutRegion> Regions { get; set; } = [];

    public List<MidiLayoutControl> Controls { get; set; } = [];
}

/// <summary>A nestable area on the blueprint.</summary>
public sealed class MidiLayoutRegion
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Null = top-level (device canvas).</summary>
    public string? ParentRegionId { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Cell within the parent region (or root canvas).</summary>
    public int Row { get; set; }

    public int Col { get; set; }

    public int RowSpan { get; set; } = 1;

    public int ColSpan { get; set; } = 1;

    /// <summary>
    /// When true, the area has no visible border in normal mode.
    /// In the layout constructor a dashed outline is shown instead so the zone stays editable.
    /// </summary>
    public bool HideBorder { get; set; }

    /// <summary>
    /// When <see cref="HideBorder"/> is true, keep a modest outer/inner gap instead of collapsing
    /// chrome to zero — useful to separate channel strips or a transport row without drawing a border.
    /// Ignored when the border is visible (full chrome already applies).
    /// </summary>
    public bool KeepSpacing { get; set; }

    /// <summary>
    /// How children are distributed horizontally when this area is wider than packed content
    /// (flex-like: pack / space-between / space-evenly).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiContentJustify ContentJustify { get; set; } = MidiContentJustify.Pack;

    /// <summary>
    /// How children are distributed vertically when this area is taller than packed content
    /// (same options as <see cref="ContentJustify"/>).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiContentJustify ContentAlign { get; set; } = MidiContentJustify.Pack;
}

public sealed class MidiLayoutControl
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Parent area id; null = root canvas.</summary>
    public string? RegionId { get; set; }

    public int Row { get; set; }

    public int Col { get; set; }

    public int RowSpan { get; set; } = 1;

    public int ColSpan { get; set; } = 1;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiControlType Type { get; set; } = MidiControlType.Fader;

    public string Label { get; set; } = string.Empty;

    /// <summary>Suggested default mode for MIDI Learn / factory bindings.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiValueMode? DefaultMode { get; set; }

    /// <summary>
    /// Optional factory hardware identity baked into the official preset
    /// (CC/note number, or pitch-bend channel 0–15). Null = discover/Learn required.
    /// </summary>
    public int? Controller { get; set; }

    public bool IsNote { get; set; }

    public bool IsPitchBend { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiRelativeEncoding? RelativeEncoding { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiBindingAction? DefaultAction { get; set; }

    /// <summary>
    /// Optional host→device feedback (e.g. mute LED). Omitted = no outbound MIDI for this control.
    /// </summary>
    public MidiControlFeedbackSpec? Feedback { get; set; }

    public bool HasFactoryHardware => Controller is >= 0 and <= 127;
}

public sealed class MidiMappingsDocument
{
    public List<MidiBinding> Bindings { get; set; } = [];

    public List<string> EnabledDevices { get; set; } = [];

    /// <summary>Ports the user explicitly hid from the default device list.</summary>
    public List<string> HiddenDevices { get; set; } = [];

    /// <summary>
    /// Ports the user forced visible (overrides auto-hide of MIDIIN2 duplicates).
    /// </summary>
    public List<string> RevealedDevices { get; set; } = [];
}

public readonly record struct MidiIncomingEvent(
    string DeviceName,
    int Controller,
    int RawValue,
    bool IsNote,
    bool IsNoteOn,
    bool IsPitchBend = false)
{
    public bool MatchesHardware(int? controller, bool isNote, bool isPitchBend) =>
        controller == Controller && isNote == IsNote && isPitchBend == IsPitchBend;
}

public readonly record struct MidiControlFeedback(
    string DeviceName,
    int Controller,
    int RawValue,
    float NormalizedValue,
    bool IsNote,
    bool IsPitchBend = false);
