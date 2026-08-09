namespace SonarQuickMixer.Midi;

/// <summary>Hit-test child used by <see cref="MidiLayoutTreeOps.TryResolveInsertSlot"/>.</summary>
public sealed class MidiDropHitChild
{
    public required string Id { get; init; }
    public required int Row { get; init; }
    public required int Col { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColSpan { get; init; } = 1;
    public required System.Windows.Rect Bounds { get; init; }
}
