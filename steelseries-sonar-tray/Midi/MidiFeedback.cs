using System.Text.Json.Serialization;

namespace SonarQuickMixer.Midi;

/// <summary>Which app state drives hardware LED / lamp feedback.</summary>
public enum MidiFeedbackSource
{
    None,
    /// <summary>Lamp follows Sonar mute for the assigned channel.</summary>
    Mute,
    /// <summary>Lamp on when a Sonar channel is assigned (fader match LED / pad lamp).</summary>
    ChannelAssigned
}

/// <summary>How the lamp is driven while the source condition is active.</summary>
public enum MidiFeedbackStyle
{
    Solid,
    Blink
}

/// <summary>Wire format for a single feedback message.</summary>
public enum MidiFeedbackKind
{
    Note,
    Cc,
    /// <summary>
    /// Pitch Bend out (MCU fader position). Non-motorized surfaces use mismatch vs the physical
    /// fader to light the strip “match” LED above the fader (not the Select button).
    /// </summary>
    PitchBend
}

/// <summary>One MIDI message to send to the controller (lamp on/off, etc.).</summary>
public sealed class MidiFeedbackMessage
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiFeedbackKind Kind { get; set; } = MidiFeedbackKind.Note;

    /// <summary>Note number or CC number (0–127). Unused for <see cref="MidiFeedbackKind.PitchBend"/>.</summary>
    public int Controller { get; set; }

    /// <summary>
    /// Note velocity / CC value (0–127). For Pitch Bend templates before materialize:
    /// ≥64 = lamp on (intentional mismatch), &lt;64 = lamp off (match hardware).
    /// After materialize: MSB 0–127 of the 14-bit position (LSB sent as 0).
    /// </summary>
    public int Value { get; set; }

    /// <summary>MIDI channel 1–16 (Pitch Bend fader strip uses channels 1–8).</summary>
    public int Channel { get; set; } = 1;
}

/// <summary>
/// Optional host→device feedback for a blueprint control (config-driven).
/// When <see cref="On"/> / <see cref="Off"/> are omitted, messages are derived from the control
/// (notes for buttons; Pitch Bend match/mismatch for PB faders).
/// </summary>
public sealed class MidiControlFeedbackSpec
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiFeedbackSource Source { get; set; } = MidiFeedbackSource.Mute;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MidiFeedbackStyle Style { get; set; } = MidiFeedbackStyle.Solid;

    public MidiFeedbackMessage? On { get; set; }

    public MidiFeedbackMessage? Off { get; set; }
}

/// <summary>UI combo tags ↔ source/style (no brand hardcoding).</summary>
public static class MidiFeedbackUi
{
    public const string TagOff = "None";
    public const string TagMute = "Mute";
    public const string TagMuteBlink = "MuteBlink";
    public const string TagChannelAssigned = "ChannelAssigned";
    public const string TagStyleSolid = "Solid";
    public const string TagStyleBlink = "Blink";

    public static string ToTag(MidiFeedbackSource source, MidiFeedbackStyle style) =>
        (source, style) switch
        {
            (MidiFeedbackSource.None, _) => TagOff,
            (MidiFeedbackSource.Mute, MidiFeedbackStyle.Blink) => TagMuteBlink,
            (MidiFeedbackSource.Mute, _) => TagMute,
            (MidiFeedbackSource.ChannelAssigned, MidiFeedbackStyle.Blink) => TagChannelAssigned + TagStyleBlink,
            (MidiFeedbackSource.ChannelAssigned, _) => TagChannelAssigned,
            _ => TagOff
        };

    public static string ToSourceTag(MidiFeedbackSource source) =>
        source switch
        {
            MidiFeedbackSource.Mute => TagMute,
            MidiFeedbackSource.ChannelAssigned => TagChannelAssigned,
            _ => TagOff
        };

    public static string ToStyleTag(MidiFeedbackStyle style) =>
        style == MidiFeedbackStyle.Blink ? TagStyleBlink : TagStyleSolid;

    /// <summary>
    /// Faders (esp. Pitch Bend soft-takeover lamps) only support Off / Channel assigned.
    /// Buttons and encoders also get Mute.
    /// </summary>
    public static bool AllowsMuteSource(MidiLayoutControl control) =>
        !control.IsPitchBend && control.Type != MidiControlType.Fader;

    public static bool AllowsStyle(MidiLayoutControl control, MidiFeedbackSource source) =>
        source != MidiFeedbackSource.None && AllowsMuteSource(control);

    public static MidiFeedbackStyle NormalizeStyle(
        MidiLayoutControl control,
        MidiFeedbackSource source,
        MidiFeedbackStyle style) =>
        AllowsStyle(control, source) ? style : MidiFeedbackStyle.Solid;

    public static bool TryParseTag(string? tag, out MidiFeedbackSource source, out MidiFeedbackStyle style)
    {
        source = MidiFeedbackSource.None;
        style = MidiFeedbackStyle.Solid;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        switch (tag.Trim())
        {
            case TagOff:
            case "Off":
                return true;
            case TagMute:
                source = MidiFeedbackSource.Mute;
                return true;
            case TagMuteBlink:
                source = MidiFeedbackSource.Mute;
                style = MidiFeedbackStyle.Blink;
                return true;
            case TagChannelAssigned:
            case "ChannelSelect":
                source = MidiFeedbackSource.ChannelAssigned;
                return true;
            case TagChannelAssigned + TagStyleBlink:
                source = MidiFeedbackSource.ChannelAssigned;
                style = MidiFeedbackStyle.Blink;
                return true;
            default:
                return false;
        }
    }

    public static bool TryParseSourceTag(string? tag, out MidiFeedbackSource source)
    {
        source = MidiFeedbackSource.None;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        switch (tag.Trim())
        {
            case TagOff:
            case "Off":
                return true;
            case TagMute:
                source = MidiFeedbackSource.Mute;
                return true;
            case TagChannelAssigned:
            case "ChannelSelect":
                source = MidiFeedbackSource.ChannelAssigned;
                return true;
            default:
                return false;
        }
    }

    public static bool TryParseStyleTag(string? tag, out MidiFeedbackStyle style)
    {
        style = MidiFeedbackStyle.Solid;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        switch (tag.Trim())
        {
            case TagStyleSolid:
                return true;
            case TagStyleBlink:
                style = MidiFeedbackStyle.Blink;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Resolves concrete on/off MIDI messages from a layout control's feedback spec.</summary>
public static class MidiFeedbackResolver
{
    public static bool TryResolveMessages(
        MidiLayoutControl control,
        out MidiFeedbackMessage on,
        out MidiFeedbackMessage off) =>
        TryResolveMessages(control, control.Feedback, out on, out off);

    public static bool TryResolveMessages(
        MidiLayoutControl control,
        MidiControlFeedbackSpec? spec,
        out MidiFeedbackMessage on,
        out MidiFeedbackMessage off)
    {
        on = null!;
        off = null!;
        if (spec is null || spec.Source is MidiFeedbackSource.None)
        {
            return false;
        }

        on = CloneOrDefault(spec.On, control, value: 127);
        off = CloneOrDefault(spec.Off, control, value: 0);
        return true;
    }

    /// <summary>Legacy name used by older call sites.</summary>
    public static bool TryResolveMuteMessages(
        MidiLayoutControl control,
        out MidiFeedbackMessage on,
        out MidiFeedbackMessage off) =>
        TryResolveMessages(control, out on, out off)
        && control.Feedback?.Source == MidiFeedbackSource.Mute;

    /// <summary>
    /// Turns Pitch Bend lamp templates into concrete positions:
    /// on (≥64) → extreme opposite of hardware (mismatch / LED lit),
    /// off (&lt;64) → hardware position (match / LED off).
    /// </summary>
    public static MidiFeedbackMessage Materialize(
        MidiFeedbackMessage message,
        MidiLayoutControl control,
        float hardwareNormalized)
    {
        if (message.Kind != MidiFeedbackKind.PitchBend)
        {
            return message;
        }

        var strip = Math.Clamp(control.Controller ?? 0, 0, 15);
        var channel = message.Channel is >= 1 and <= 16 ? message.Channel : strip + 1;
        var hw = Math.Clamp(hardwareNormalized, 0f, 1f);
        var target = message.Value >= 64
            ? hw < 0.5f ? 1f : 0f
            : hw;
        var msb = (int)Math.Round(target * 127f);

        return new MidiFeedbackMessage
        {
            Kind = MidiFeedbackKind.PitchBend,
            Controller = 0,
            Value = Math.Clamp(msb, 0, 127),
            Channel = channel
        };
    }

    private static MidiFeedbackMessage CloneOrDefault(
        MidiFeedbackMessage? configured,
        MidiLayoutControl control,
        int value)
    {
        if (configured is not null)
        {
            return new MidiFeedbackMessage
            {
                Kind = configured.Kind,
                Controller = Math.Clamp(configured.Controller, 0, 127),
                Value = Math.Clamp(configured.Value, 0, 127),
                Channel = configured.Channel is >= 1 and <= 16 ? configured.Channel : 1
            };
        }

        if (control.IsPitchBend)
        {
            var strip = Math.Clamp(control.Controller ?? 0, 0, 7);
            // Fader-top “match” LED: Pitch Bend out vs physical position (not Select notes).
            return new MidiFeedbackMessage
            {
                Kind = MidiFeedbackKind.PitchBend,
                Controller = 0,
                Value = Math.Clamp(value, 0, 127),
                Channel = strip + 1
            };
        }

        return new MidiFeedbackMessage
        {
            Kind = MidiFeedbackKind.Note,
            Controller = ResolveDefaultNote(control),
            Value = Math.Clamp(value, 0, 127),
            Channel = 1
        };
    }

    private static int ResolveDefaultNote(MidiLayoutControl control)
    {
        var raw = control.Controller ?? 0;
        if (control.IsNote)
        {
            return Math.Clamp(raw, 0, 127);
        }

        // Absolute CC control: still emit a Note by default so LEDs work on MCU-like pads;
        // authors can override with feedback.on.kind=cc.
        return Math.Clamp(raw, 0, 127);
    }

    public static MidiControlFeedbackSpec? Clone(MidiControlFeedbackSpec? source)
    {
        if (source is null)
        {
            return null;
        }

        return new MidiControlFeedbackSpec
        {
            Source = source.Source,
            Style = source.Style,
            On = CloneMessage(source.On),
            Off = CloneMessage(source.Off)
        };
    }

    private static MidiFeedbackMessage? CloneMessage(MidiFeedbackMessage? source) =>
        source is null
            ? null
            : new MidiFeedbackMessage
            {
                Kind = source.Kind,
                Controller = source.Controller,
                Value = source.Value,
                Channel = source.Channel
            };
}
