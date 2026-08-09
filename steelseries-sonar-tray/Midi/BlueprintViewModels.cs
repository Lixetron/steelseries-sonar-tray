using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using SonarQuickMixer.Sonar;
using Media = System.Windows.Media;

namespace SonarQuickMixer.Midi;

public sealed class BlueprintControlVm : IBlueprintFormCell
{
    /// <summary>
    /// Visual degrees per relative MIDI tick (before smoothing).
    /// Calibrated iteratively against common relative MIDI knobs (physical ≈ UI).
    /// </summary>
    internal const double RelativeDegreesPerTick = 3.43;

    /// <summary>
    /// Fraction of the remaining gap applied immediately on each MIDI tick (rest eases in 1–2 frames).
    /// High = tight to the hand; low = jelly lag.
    /// </summary>
    private const double RelativeInstantCatchup = 0.82;

    /// <summary>Exponential follow rate for the leftover gap (~settles in ~2 frames at 60 Hz).</summary>
    private const double RelativeResponsePerSecond = 55.0;

    /// <summary>Discard backlog beyond this so direction reversals stay crisp.</summary>
    private const double RelativeMaxLeadDegrees = 28.0;

    private const double RelativeSnapDegrees = 0.4;

    private static readonly TimeSpan NeedleFrameInterval = TimeSpan.FromMilliseconds(16);

    private float _normalizedValue;
    private double _relativeNeedleDegrees;
    private double _relativeNeedleTargetDegrees;
    private DispatcherTimer? _relativeNeedleTimer;
    private DateTime _relativeNeedleLastTickUtc;
    private bool _isLearning;
    private bool _isSelected;
    private bool _hasUnsavedChanges;
    private bool _hasUnsavedChannel;
    private bool _hasUnsavedMode;
    private bool _hasUnsavedAction;
    private bool _hasUnsavedFeedbackSource;
    private bool _hasUnsavedFeedbackStyle;
    private bool _isPressed;
    private bool _hasBinding;
    private MidiLayoutDropZone? _dropPreviewZone;
    private string _bindingSummary = "Unmapped — move control to discover";
    private string _channelId = string.Empty;
    private MidiValueMode _mode = MidiValueMode.Absolute;
    private MidiBindingAction _action = MidiBindingAction.Volume;
    private MidiRelativeEncoding _relativeEncoding = MidiRelativeEncoding.OffsetBinary;
    private int? _controller;
    private bool _isNote;
    private bool _isPitchBend;

    public required string Id { get; init; }

    /// <summary>Parent area id; null = root canvas.</summary>
    public string? RegionId { get; init; }

    public required string Label { get; init; }
    public required MidiControlType Type { get; init; }

    private int _row;
    private int _col;

    public required int Row
    {
        get => _row;
        set
        {
            if (_row == value)
            {
                return;
            }

            _row = value;
            OnPropertyChanged();
        }
    }

    public required int Col
    {
        get => _col;
        set
        {
            if (_col == value)
            {
                return;
            }

            _col = value;
            OnPropertyChanged();
        }
    }

    public int RowSpan { get; init; } = 1;
    public int ColSpan { get; init; } = 1;

    public bool IsDropSlot => false;
    public MidiValueMode? DefaultMode { get; init; }
    public bool IsPlaceholder { get; init; }

    /// <summary>Compact chrome for strip side-buttons (M/S/R/□).</summary>
    public bool Compact { get; init; }

    /// <summary>Tall fader track used in channel-strip chassis layout.</summary>
    public bool TallFader { get; init; }

    public float NormalizedValue
    {
        get => _normalizedValue;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_normalizedValue - clamped) < 0.0001f)
            {
                return;
            }

            _normalizedValue = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NeedleAngle));
            OnPropertyChanged(nameof(FaderOffset));
        }
    }

    /// <summary>
    /// Absolute encoders/faders: needle maps 0..1 across a 270° arc.
    /// Relative (endless) encoders: needle spins continuously with each tick (no absolute position).
    /// </summary>
    public double NeedleAngle =>
        UsesRelativeNeedle
            ? _relativeNeedleDegrees
            : -135 + (NormalizedValue * 270);

    public bool UsesRelativeNeedle =>
        Type == MidiControlType.Encoder && Mode == MidiValueMode.Relative;

    public double FaderTrackHeight => TallFader ? 168.0 : 90.0;

    /// <summary>Thumb Y within the fader track (thumb height 14).</summary>
    public double FaderOffset => (1.0 - NormalizedValue) * (FaderTrackHeight - 14.0);

    /// <summary>
    /// Updates blueprint chrome from a raw MIDI message.
    /// Relative encoders accumulate rotation; absolute controls set 0..1 position.
    /// </summary>
    public void ApplyIncomingVisual(int rawValue, bool isPitchBend = false)
    {
        if (UsesRelativeNeedle && !isPitchBend)
        {
            var ticks = MidiValueParser.ParseRelativeTicks(rawValue, RelativeEncoding);
            if (ticks == 0)
            {
                return;
            }

            QueueRelativeSpin(ticks);
            return;
        }

        NormalizedValue = MidiValueParser.ToNormalizedVolume(isPitchBend, rawValue);
    }

    public void ResetRelativeNeedle(double degrees = 0)
    {
        StopRelativeNeedleTimer();
        _relativeNeedleDegrees = degrees;
        _relativeNeedleTargetDegrees = degrees;
        OnPropertyChanged(nameof(NeedleAngle));
    }

    private void QueueRelativeSpin(int ticks)
    {
        _relativeNeedleTargetDegrees += ticks * RelativeDegreesPerTick;
        ClampRelativeLead();

        // Unit tests / no WPF pump: snap so assertions stay deterministic.
        if (System.Windows.Application.Current?.Dispatcher is null)
        {
            _relativeNeedleDegrees = _relativeNeedleTargetDegrees;
            OnPropertyChanged(nameof(NeedleAngle));
            return;
        }

        // Most of the motion lands on the MIDI event itself — avoids "following behind the hand".
        var gap = _relativeNeedleTargetDegrees - _relativeNeedleDegrees;
        _relativeNeedleDegrees += gap * RelativeInstantCatchup;
        OnPropertyChanged(nameof(NeedleAngle));

        if (Math.Abs(_relativeNeedleTargetDegrees - _relativeNeedleDegrees) <= RelativeSnapDegrees)
        {
            FinishRelativeNeedle();
            return;
        }

        EnsureRelativeNeedleTimer();
    }

    private void ClampRelativeLead()
    {
        var lead = _relativeNeedleTargetDegrees - _relativeNeedleDegrees;
        if (Math.Abs(lead) <= RelativeMaxLeadDegrees)
        {
            return;
        }

        _relativeNeedleTargetDegrees =
            _relativeNeedleDegrees + (Math.Sign(lead) * RelativeMaxLeadDegrees);
    }

    private void EnsureRelativeNeedleTimer()
    {
        if (_relativeNeedleTimer is not null)
        {
            return;
        }

        _relativeNeedleLastTickUtc = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = NeedleFrameInterval
        };
        timer.Tick += RelativeNeedleTimer_Tick;
        _relativeNeedleTimer = timer;
        timer.Start();
    }

    private void RelativeNeedleTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _relativeNeedleLastTickUtc).TotalSeconds, 0.001, 0.05);
        _relativeNeedleLastTickUtc = now;

        var remaining = _relativeNeedleTargetDegrees - _relativeNeedleDegrees;
        if (Math.Abs(remaining) <= RelativeSnapDegrees)
        {
            FinishRelativeNeedle();
            return;
        }

        // Exponential ease: leftover gap closes quickly without a long rubber-band trail.
        var alpha = 1.0 - Math.Exp(-RelativeResponsePerSecond * dt);
        _relativeNeedleDegrees += remaining * alpha;
        OnPropertyChanged(nameof(NeedleAngle));
    }

    private void FinishRelativeNeedle()
    {
        _relativeNeedleDegrees = WrapDegrees(_relativeNeedleTargetDegrees);
        _relativeNeedleTargetDegrees = _relativeNeedleDegrees;
        OnPropertyChanged(nameof(NeedleAngle));
        StopRelativeNeedleTimer();
    }

    private void StopRelativeNeedleTimer()
    {
        if (_relativeNeedleTimer is null)
        {
            return;
        }

        _relativeNeedleTimer.Stop();
        _relativeNeedleTimer.Tick -= RelativeNeedleTimer_Tick;
        _relativeNeedleTimer = null;
    }

    private static double WrapDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        return degrees;
    }

    public bool IsLearning
    {
        get => _isLearning;
        set
        {
            if (_isLearning == value)
            {
                return;
            }

            _isLearning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(IsPassive));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(BorderThickness));
            OnPropertyChanged(nameof(ShowDeleteButton));
        }
    }

    /// <summary>
    /// Staged channel / mode / action / LED feedback not yet written with Save changes.
    /// Drives the yellow chrome outline.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set
        {
            if (_hasUnsavedChanges == value)
            {
                return;
            }

            _hasUnsavedChanges = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(BorderThickness));
        }
    }

    public bool HasUnsavedChannel
    {
        get => _hasUnsavedChannel;
        set => SetUnsavedField(ref _hasUnsavedChannel, value);
    }

    public bool HasUnsavedMode
    {
        get => _hasUnsavedMode;
        set => SetUnsavedField(ref _hasUnsavedMode, value);
    }

    public bool HasUnsavedAction
    {
        get => _hasUnsavedAction;
        set => SetUnsavedField(ref _hasUnsavedAction, value);
    }

    public bool HasUnsavedFeedbackSource
    {
        get => _hasUnsavedFeedbackSource;
        set => SetUnsavedField(ref _hasUnsavedFeedbackSource, value);
    }

    public bool HasUnsavedFeedbackStyle
    {
        get => _hasUnsavedFeedbackStyle;
        set => SetUnsavedField(ref _hasUnsavedFeedbackStyle, value);
    }

    private void SetUnsavedField(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    /// <summary>Constructor delete chrome — only on the selected control.</summary>
    public bool ShowDeleteButton => IsConstructorMode && IsSelected && !IsPlaceholder;

    /// <summary>True while the layout constructor is open.</summary>
    public bool IsConstructorMode { get; init; }

    public bool IsPressed
    {
        get => _isPressed;
        set
        {
            if (_isPressed == value)
            {
                return;
            }

            _isPressed = value;
            OnPropertyChanged();
        }
    }

    public MidiLayoutDropZone? DropPreviewZone
    {
        get => _dropPreviewZone;
        set
        {
            if (_dropPreviewZone == value)
            {
                return;
            }

            _dropPreviewZone = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDropPreview));
            OnPropertyChanged(nameof(DropPreviewLabel));
        }
    }

    public bool HasDropPreview => _dropPreviewZone is not null;

    public string DropPreviewLabel => _dropPreviewZone?.ToString() ?? string.Empty;

    public bool HasBinding
    {
        get => _hasBinding;
        set
        {
            if (_hasBinding == value)
            {
                return;
            }

            _hasBinding = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(IsPassive));
        }
    }

    public string BindingSummary
    {
        get => _bindingSummary;
        set
        {
            if (_bindingSummary == value)
            {
                return;
            }

            _bindingSummary = value;
            OnPropertyChanged();
        }
    }

    public string ChannelId
    {
        get => _channelId;
        set
        {
            if (_channelId == value)
            {
                return;
            }

            _channelId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSonarChannel));
            OnPropertyChanged(nameof(ChannelCaption));
            OnPropertyChanged(nameof(ShowChannelCaption));
            OnPropertyChanged(nameof(IsPassive));
            OnPropertyChanged(nameof(BorderBrush));
        }
    }

    /// <summary>True when this control routes to a Sonar channel (not just hardware-discovered).</summary>
    public bool HasSonarChannel =>
        !string.IsNullOrWhiteSpace(_channelId) && SonarChannels.IsValidChannel(_channelId);

    /// <summary>Short channel name under the label; empty when unassigned.</summary>
    public string ChannelCaption =>
        HasSonarChannel ? SonarChannels.GetDisplayName(_channelId) : string.Empty;

    public bool ShowChannelCaption => HasSonarChannel && !IsPlaceholder;

    /// <summary>
    /// Soft muted chrome for controls without a Sonar channel — brighter than the
    /// device-not-in-use blueprint dim (≈0.42), so the map stays readable.
    /// Not applied in the layout constructor (editing needs full-contrast chrome).
    /// </summary>
    public bool IsPassive =>
        !IsPlaceholder && !IsConstructorMode && !HasSonarChannel && !IsLearning;

    public MidiValueMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesRelativeNeedle));
            OnPropertyChanged(nameof(NeedleAngle));
        }
    }

    public MidiRelativeEncoding RelativeEncoding
    {
        get => _relativeEncoding;
        set
        {
            if (_relativeEncoding == value)
            {
                return;
            }

            _relativeEncoding = value;
            OnPropertyChanged();
        }
    }

    public MidiBindingAction Action
    {
        get => _action;
        set
        {
            if (_action == value)
            {
                return;
            }

            _action = value;
            OnPropertyChanged();
        }
    }

    public int? Controller
    {
        get => _controller;
        set
        {
            if (_controller == value)
            {
                return;
            }

            _controller = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMissingHardwareWarning));
        }
    }

    /// <summary>
    /// Control exists on the layout but has no MIDI CC/Note/PitchBend mapping yet.
    /// </summary>
    public bool ShowMissingHardwareWarning => !IsPlaceholder && Controller is null;

    public bool IsNote
    {
        get => _isNote;
        set
        {
            if (_isNote == value)
            {
                return;
            }

            _isNote = value;
            OnPropertyChanged();
        }
    }

    public bool IsPitchBend
    {
        get => _isPitchBend;
        set
        {
            if (_isPitchBend == value)
            {
                return;
            }

            _isPitchBend = value;
            OnPropertyChanged();
        }
    }

    public Media.Brush BorderBrush
    {
        get
        {
            if (IsLearning)
            {
                return LearningBorderBrush;
            }

            if (HasUnsavedChanges)
            {
                return IsSelected ? UnsavedSelectedBorderBrush : UnsavedBorderBrush;
            }

            if (IsSelected)
            {
                return SelectedBorderBrush;
            }

            return HasSonarChannel ? AssignedBorderBrush : IdleBorderBrush;
        }
    }

    /// <summary>Slightly thicker chrome when the control has staged edits.</summary>
    public double BorderThickness => HasUnsavedChanges ? 2.25 : 1.5;

    private static readonly Media.Brush LearningBorderBrush = CreateFrozenBrush(0x60, 0xCD, 0xFF);
    private static readonly Media.Brush UnsavedSelectedBorderBrush = CreateFrozenBrush(0xFF, 0xE0, 0x4A);
    private static readonly Media.Brush UnsavedBorderBrush = CreateFrozenBrush(0xC9, 0xA2, 0x27);
    private static readonly Media.Brush SelectedBorderBrush = CreateFrozenBrush(0x9A, 0x9A, 0x9A);
    private static readonly Media.Brush AssignedBorderBrush = CreateFrozenBrush(0x5A, 0x5A, 0x5A);
    private static readonly Media.Brush IdleBorderBrush = CreateFrozenBrush(0x4A, 0x4A, 0x4A);

    private static Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Nestable area chrome for the layout constructor.</summary>
public sealed class BlueprintRegionVm : IBlueprintFormCell
{
    private bool _isSelected;
    private string _label = string.Empty;
    private string _dropHint = string.Empty;

    public required string Id { get; init; }

    /// <summary>Parent region id from layout JSON; null = root.</summary>
    public string? ParentRegionId { get; init; }

    private int _row;
    private int _col;

    public int Row
    {
        get => _row;
        set
        {
            if (_row == value)
            {
                return;
            }

            _row = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Row)));
        }
    }

    public int Col
    {
        get => _col;
        set
        {
            if (_col == value)
            {
                return;
            }

            _col = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Col)));
        }
    }

    public int RowSpan { get; init; } = 1;

    public int ColSpan { get; init; } = 1;

    public bool IsDropSlot => false;

    /// <summary>From layout JSON — hide solid border in normal mode.</summary>
    public bool HideBorder { get; init; }

    /// <summary>From layout JSON — keep a gap when <see cref="HideBorder"/> collapses chrome.</summary>
    public bool KeepSpacing { get; init; }

    /// <summary>Flex-like horizontal distribution of children when this area is wider than packed content.</summary>
    public MidiContentJustify ContentJustify { get; init; } = MidiContentJustify.Pack;

    /// <summary>Flex-like vertical distribution of children when this area is taller than packed content.</summary>
    public MidiContentJustify ContentAlign { get; init; } = MidiContentJustify.Pack;

    /// <summary>True when this area sits on the left edge of its parent grid (outer margin collapsed).</summary>
    public bool FlushLeft { get; init; }

    public bool FlushTop { get; init; }

    public bool FlushRight { get; init; }

    public bool FlushBottom { get; init; }

    /// <summary>True while the layout constructor is open (drives dashed editor outline).</summary>
    public bool IsConstructorMode { get; init; }

    /// <summary>Dashed outline only: hidden-border areas still need a visual cue while editing.</summary>
    public bool ShowDashedEditorOutline => HideBorder && IsConstructorMode;

    public double ChromeBorderThickness => HideBorder ? 0 : 2;

    /// <summary>
    /// Outer gap. Visible border → full margin. Hidden + keep spacing → one modest gap.
    /// Hidden without keep spacing → 0 in normal view (nested chrome collapses).
    /// Outer edges of the parent grid drop that side's margin so spacing stays between siblings only.
    /// </summary>
    public Thickness ChromeMargin
    {
        get
        {
            double gap;
            if (!HideBorder)
            {
                gap = 6;
            }
            else if (KeepSpacing)
            {
                gap = IsConstructorMode ? 3 : 4;
            }
            else
            {
                gap = IsConstructorMode ? 2 : 0;
            }

            return EdgeAwareMargin(gap);
        }
    }

    /// <summary>
    /// Inner padding. Same three-way rule as <see cref="ChromeMargin"/> (padding is not edge-flushed).
    /// </summary>
    public Thickness ChromePadding
    {
        get
        {
            if (!HideBorder)
            {
                return new Thickness(8);
            }

            if (KeepSpacing)
            {
                return IsConstructorMode ? new Thickness(4) : new Thickness(2);
            }

            return IsConstructorMode ? new Thickness(4) : new Thickness(0);
        }
    }

    private Thickness EdgeAwareMargin(double gap)
    {
        if (gap <= 0)
        {
            return new Thickness(0);
        }

        return new Thickness(
            FlushLeft ? 0 : gap,
            FlushTop ? 0 : gap,
            FlushRight ? 0 : gap,
            FlushBottom ? 0 : gap);
    }

    public double ChromeMinWidth => HideBorder ? 0 : 120;

    public double ChromeMinHeight => HideBorder ? 0 : 80;

    public Media.Brush ChromeBackground =>
        HideBorder
            ? IsConstructorMode
                ? new Media.SolidColorBrush(Media.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF))
                : Media.Brushes.Transparent
            : new Media.SolidColorBrush(Media.Color.FromArgb(0x18, 0x00, 0x00, 0x00));

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value)
            {
                return;
            }

            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHeaderChrome)));
        }
    }

    public bool HasLabel => !string.IsNullOrWhiteSpace(_label);

    /// <summary>True when the title row should stay visible (label and/or live drop hint).</summary>
    public bool HasHeaderChrome => HasLabel || HasDropHint;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowDeleteButton)));
        }
    }

    /// <summary>Constructor delete chrome — only on the selected area (avoids stacked/accidental ×).</summary>
    public bool ShowDeleteButton => IsConstructorMode && IsSelected;

    /// <summary>Live DnD zone hint (Left/Right/Top/Bottom/Inside).</summary>
    public string DropHint
    {
        get => _dropHint;
        set
        {
            if (_dropHint == value)
            {
                return;
            }

            _dropHint = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DropHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDropHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHeaderChrome)));
        }
    }

    public bool HasDropHint => !string.IsNullOrWhiteSpace(_dropHint);

    public ObservableCollection<object> Children { get; } = [];

    public Media.Brush BorderBrush =>
        IsSelected
            ? new Media.SolidColorBrush(Media.Color.FromRgb(0x60, 0xCD, 0xFF))
            : new Media.SolidColorBrush(Media.Color.FromRgb(0x66, 0x66, 0x66));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Temporary dashed insert slot shown while dragging in the layout constructor.</summary>
public sealed class BlueprintDropSlotVm : IBlueprintFormCell
{
    private int _row;
    private int _col;
    private int _rowSpan = 1;
    private int _colSpan = 1;

    public string Id { get; } = "__drop_slot__";

    public bool IsDropSlot => true;

    public int Row
    {
        get => _row;
        set
        {
            if (_row == value)
            {
                return;
            }

            _row = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Row)));
        }
    }

    public int Col
    {
        get => _col;
        set
        {
            if (_col == value)
            {
                return;
            }

            _col = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Col)));
        }
    }

    public int RowSpan
    {
        get => _rowSpan;
        set
        {
            var v = Math.Max(1, value);
            if (_rowSpan == v)
            {
                return;
            }

            _rowSpan = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowSpan)));
        }
    }

    public int ColSpan
    {
        get => _colSpan;
        set
        {
            var v = Math.Max(1, value);
            if (_colSpan == v)
            {
                return;
            }

            _colSpan = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColSpan)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MidiDeviceListItemVm : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _isHidden;

    public required string Name { get; init; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameForeground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        }
    }

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value)
            {
                return;
            }

            _isHidden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameForeground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        }
    }

    public string StatusLabel =>
        IsHidden
            ? "Hidden"
            : IsEnabled
                ? "In use"
                : "Not used";

    public Media.Brush NameForeground =>
        IsHidden
            ? new Media.SolidColorBrush(Media.Color.FromRgb(0x5A, 0x5A, 0x5A))
            : IsEnabled
                ? new Media.SolidColorBrush(Media.Color.FromRgb(0xFF, 0xFF, 0xFF))
                : new Media.SolidColorBrush(Media.Color.FromRgb(0x7A, 0x7A, 0x7A));

    public event PropertyChangedEventHandler? PropertyChanged;
}
