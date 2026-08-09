using System.ComponentModel;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Shared grid placement contract for constructor form cells
/// (controls, areas, and temporary insert slots).
/// </summary>
public interface IBlueprintFormCell : INotifyPropertyChanged
{
    string Id { get; }

    int Row { get; set; }

    int Col { get; set; }

    int RowSpan { get; }

    int ColSpan { get; }

    /// <summary>True for temporary insert placeholders (not persisted layout nodes).</summary>
    bool IsDropSlot { get; }
}
