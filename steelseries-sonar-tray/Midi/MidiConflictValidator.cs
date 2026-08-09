namespace SonarQuickMixer.Midi;

/// <summary>
/// Shared helpers for absolute-fader conflict checks during MIDI Learn / mapping.
/// </summary>
public static class MidiConflictValidator
{
    public static bool RequiresConflictConfirmation(
        MidiMappingStore store,
        MidiBinding candidate,
        out IReadOnlyList<MidiBinding> conflicts)
    {
        conflicts = [];
        if (!candidate.HasSonarChannel
            || candidate.Mode != MidiValueMode.Absolute
            || candidate.IsMotorized
            || candidate.IsNote
            || candidate.Action != MidiBindingAction.Volume)
        {
            return false;
        }

        conflicts = store.FindConflictingAbsoluteFaders(
            candidate.ChannelId,
            candidate.Path,
            candidate.BindingKey);

        return conflicts.Count > 0;
    }
}
