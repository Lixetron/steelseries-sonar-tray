namespace SonarQuickMixer.Midi;

/// <summary>
/// Converts raw MIDI CC values into absolute volumes or relative tick deltas.
/// </summary>
public static class MidiValueParser
{
    public static float AbsoluteToVolume(int rawValue) =>
        Math.Clamp(rawValue, 0, 127) / 127f;

    /// <summary>
    /// Pitch Bend 14-bit → 0..1. Common fader wire encoding uses LSB=0 and MSB 0..127
    /// (maps 0..(127&lt;&lt;7) → 0..1).
    /// </summary>
    public static float PitchBendToVolume(int pitchValue14Bit)
    {
        const int maxUseful = 127 << 7; // wire bytes 00 00 … 00 7F when LSB is 0
        return Math.Clamp(pitchValue14Bit, 0, maxUseful) / (float)maxUseful;
    }

    public static float ToNormalizedVolume(bool isPitchBend, int rawValue) =>
        isPitchBend ? PitchBendToVolume(rawValue) : AbsoluteToVolume(rawValue);

    /// <summary>Inverse of <see cref="ToNormalizedVolume"/> for UI/feedback seeding.</summary>
    public static int VolumeToRaw(bool isPitchBend, float volume)
    {
        var clamped = Math.Clamp(volume, 0f, 1f);
        if (isPitchBend)
        {
            const int maxUseful = 127 << 7;
            return (int)Math.Round(clamped * maxUseful);
        }

        return (int)Math.Round(clamped * 127f);
    }

    /// <summary>Display helper: CC/Note as decimal, Pitch Bend as wire LSB MSB hex.</summary>
    public static string FormatRawDisplay(int rawValue, bool isPitchBend)
    {
        if (!isPitchBend)
        {
            return rawValue.ToString();
        }

        var lsb = rawValue & 0x7F;
        var msb = (rawValue >> 7) & 0x7F;
        return $"{lsb:X2} {msb:X2}";
    }

    /// <summary>
    /// Returns signed tick count (usually -1, 0, or +1; some encoders emit larger magnitudes).
    /// </summary>
    public static int ParseRelativeTicks(int rawValue, MidiRelativeEncoding encoding)
    {
        var value = Math.Clamp(rawValue, 0, 127);
        return encoding switch
        {
            MidiRelativeEncoding.TwosComplement => ParseTwosComplement(value),
            _ => ParseOffsetBinary(value)
        };
    }

    public static float ApplyRelativeDelta(float currentVolume, int ticks, float step)
    {
        if (ticks == 0 || Math.Abs(step) < 0.0001f)
        {
            return Math.Clamp(currentVolume, 0f, 1f);
        }

        return Math.Clamp(currentVolume + (ticks * step), 0f, 1f);
    }

    private static int ParseOffsetBinary(int value)
    {
        if (value == 0 || value == 64)
        {
            return 0;
        }

        if (value is >= 1 and <= 63)
        {
            return value;
        }

        // 65..127 => -1 .. -63
        return 64 - value;
    }

    private static int ParseTwosComplement(int value)
    {
        if (value == 0)
        {
            return 0;
        }

        // 1..64 positive, 65..127 as signed (-63..-1)
        if (value <= 64)
        {
            return value;
        }

        return value - 128;
    }
}
