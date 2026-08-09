namespace SonarQuickMixer.Midi;

public enum MidiValueMode
{
    Absolute,
    Relative
}

public enum MidiControlType
{
    Fader,
    Encoder,
    Button
}

public enum MidiRelativeEncoding
{
    /// <summary>1..63 = increment, 65..127 = decrement (common infinite encoder).</summary>
    OffsetBinary,
    /// <summary>Two's complement: 1..64 = +, 127..65 = −.</summary>
    TwosComplement
}

public enum MidiBindingAction
{
    /// <summary>Control is mapped but does not drive Sonar (channel may still be staged).</summary>
    None,
    Volume,
    MuteToggle
}

public static class MidiBindingActions
{
    public static MidiBindingAction DefaultFor(MidiControlType type) =>
        MidiBindingAction.None;

    /// <summary>
    /// Buttons: None | MuteToggle (legacy Volume → MuteToggle).
    /// Faders/encoders: None | Volume (legacy MuteToggle → Volume).
    /// </summary>
    public static MidiBindingAction Normalize(MidiControlType type, MidiBindingAction action)
    {
        if (type == MidiControlType.Button)
        {
            return action switch
            {
                MidiBindingAction.MuteToggle => MidiBindingAction.MuteToggle,
                MidiBindingAction.Volume => MidiBindingAction.MuteToggle,
                _ => MidiBindingAction.None
            };
        }

        return action switch
        {
            MidiBindingAction.Volume => MidiBindingAction.Volume,
            MidiBindingAction.MuteToggle => MidiBindingAction.Volume,
            _ => MidiBindingAction.None
        };
    }

    /// <summary>Normalize from wire identity when layout type is unknown (IsNote ≈ button).</summary>
    public static MidiBindingAction NormalizeFromHardware(bool isNote, MidiBindingAction action) =>
        Normalize(isNote ? MidiControlType.Button : MidiControlType.Fader, action);
}

/// <summary>Where a drag lands relative to a target region or control cell.</summary>
public enum MidiLayoutDropZone
{
    Inside,
    Left,
    Right,
    Top,
    Bottom
}

/// <summary>Which grid axis siblings shift when inserting into a slot.</summary>
public enum MidiLayoutShiftAxis
{
    Horizontal,
    Vertical
}

/// <summary>Constructor drop target: insert at (Row,Col) under ParentRegionId, shifting siblings on Axis.</summary>
public readonly record struct MidiDropSlot(
    string? ParentRegionId,
    int Row,
    int Col,
    MidiLayoutShiftAxis Axis,
    int RowSpan = 1,
    int ColSpan = 1);

/// <summary>
/// How free space is distributed among children inside an area (flex-like).
/// Used for horizontal (<c>contentJustify</c>) and vertical (<c>contentAlign</c>) independently.
/// </summary>
public enum MidiContentJustify
{
    /// <summary>Children stay packed together at the start (left / top).</summary>
    Pack,

    /// <summary>Equal gaps between children; no extra gap at the container edges.</summary>
    SpaceBetween,

    /// <summary>Equal gaps between children and at both container edges.</summary>
    SpaceEvenly
}

