using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SonarQuickMixer.Sonar;
using WpfApp = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;

namespace SonarQuickMixer.Midi;

public sealed partial class MidiConfigController : INotifyPropertyChanged, IDisposable
{
    private readonly MidiControlService _midi;
    private readonly PresetCatalog _presets;
    private readonly Dispatcher _dispatcher;
    private MidiDeviceLayout _layout;
    private string? _selectedDeviceName;
    private string _statusText = "Move faders/knobs to discover them, then assign Sonar channels.";
    private BlueprintControlVm? _learningControl;
    private CancellationTokenSource? _learnCts;
    private readonly Dictionary<string, CcProbe> _ccProbes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _showHiddenDevices;
    private bool _isLayoutConstructorMode;
    private MidiDeviceLayout? _draftLayout;
    private HashSet<string>? _controlIdsAtConstructorEnter;
    private MidiControlType _paletteSelectedType = MidiControlType.Encoder;
    private string _draftControlLabel = string.Empty;
    private int _draftRowSpan = 1;
    private int _draftColSpan = 1;
    private bool _draftHideBorder;
    private bool _draftKeepSpacing;
    private MidiContentJustify _draftContentJustify = MidiContentJustify.Pack;
    private MidiContentJustify _draftContentAlign = MidiContentJustify.Pack;
    private MidiFeedbackSource _draftFeedbackSource = MidiFeedbackSource.None;
    private MidiFeedbackStyle _draftFeedbackStyle = MidiFeedbackStyle.Solid;
    private bool _draftFeedbackEnabled;
    /// <summary>True for pads/encoders (not Pitch Bend / fader soft-takeover lamps).</summary>
    private bool _draftFeedbackCanChooseStyle;
    private string? _selectedConstructorRegionId;
    private double _blueprintZoom = 1.0;
    private BlueprintDropSlotVm? _dropSlotVm;
    private ObservableCollection<object>? _dropSlotParentChildren;
    private MidiDropSlot? _activeDropSlot;
    private readonly List<(object Node, int Row, int Col)> _dropPreviewBases = [];
    private MidiPresetOption? _selectedLayoutPreset;
    private bool _suppressPresetSelection;
    private bool _hasUnsavedBindingDrafts;
    private bool _hasUnsavedLayoutChanges;
    private readonly Dictionary<string, BindingAssignmentDraft> _bindingDrafts =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last saved LED feedback tags by control id (baseline for yellow chrome / Discard).</summary>
    private readonly Dictionary<string, string> _persistedFeedbackTags =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional UI confirm when discarding unsaved channel assignments. Return true to proceed.</summary>
    public Func<string, bool>? ConfirmDiscardUnsavedAssignments { get; set; }

    private sealed record BindingAssignmentDraft(
        string ControlId,
        string ChannelId,
        MidiValueMode Mode,
        MidiBindingAction Action);

    public const double BlueprintZoomMin = 0.5;
    public const double BlueprintZoomMax = 3.0;
    public const double BlueprintZoomStep = 0.1;

    public MidiConfigController(MidiControlService midi, PresetCatalog? presets = null)
    {
        _midi = midi;
        // Must share the runtime catalog — otherwise Save writes feedback the MIDI
        // service never sees (separate Resolve / selection store).
        _presets = presets ?? midi.Presets;
        _dispatcher = WpfApp.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _layout = PresetCatalog.CreateGenericLayout();
        Controls = [];
        ConstructorRoots = [];
        Devices = [];
        LayoutPresets = [];

        _midi.ControlFeedback += OnControlFeedback;
        _midi.Hub.DevicesChanged += OnDevicesChanged;
        _midi.RawEventReceived += OnRawEventForDiscovery;
        RefreshDevices();
    }

    public ObservableCollection<BlueprintControlVm> Controls { get; }




    /// <summary>Root nodes for constructor tree (regions and/or controls).</summary>
    public ObservableCollection<object> ConstructorRoots { get; }



    public ObservableCollection<MidiDeviceListItemVm> Devices { get; }

    /// <summary>Official + user layout presets for the selected device.</summary>
    public ObservableCollection<MidiPresetOption> LayoutPresets { get; }

    public MidiPresetOption? SelectedLayoutPreset
    {
        get => _selectedLayoutPreset;
        set
        {
            if (ReferenceEquals(_selectedLayoutPreset, value)
                || (value is not null
                    && _selectedLayoutPreset is not null
                    && string.Equals(_selectedLayoutPreset.Key, value.Key, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedLayoutPreset = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanDeleteSelectedLayoutPreset));
                OnPropertyChanged(nameof(CanRenameSelectedLayoutPreset));
                return;
            }

            if (_suppressPresetSelection)
            {
                _selectedLayoutPreset = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanDeleteSelectedLayoutPreset));
                OnPropertyChanged(nameof(CanRenameSelectedLayoutPreset));
                return;
            }

            if (IsLayoutConstructorMode)
            {
                StatusText = "Finish or cancel layout editing before switching presets.";
                RefreshLayoutPresets(selectKey: _presets.GetActivePresetKey(SelectedDeviceName));
                return;
            }

            if (!ConfirmDiscardBindingDraftsIfNeeded("Switching layout preset will discard unsaved channel assignments."))
            {
                RefreshLayoutPresets(selectKey: _selectedLayoutPreset?.Key
                                               ?? _presets.GetActivePresetKey(SelectedDeviceName));
                return;
            }

            _selectedLayoutPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDeleteSelectedLayoutPreset));
            OnPropertyChanged(nameof(CanRenameSelectedLayoutPreset));

            if (value is null || string.IsNullOrWhiteSpace(SelectedDeviceName))
            {
                return;
            }

            _presets.SetActivePresetKey(SelectedDeviceName, value.Key);
            ReloadLayoutForSelectedDevice();
            StatusText = $"Layout preset: {value.DisplayName}";
        }
    }

    public bool CanDeleteSelectedLayoutPreset =>
        SelectedLayoutPreset is { IsUser: true };

    /// <summary>Rename is only for user presets (official name is read-only).</summary>
    public bool CanRenameSelectedLayoutPreset => CanDeleteSelectedLayoutPreset;

    /// <summary>Control whose Channel/Mode/Action/LED inspector is shown (normal mode).</summary>
    private BlueprintControlVm? _inspectorControl;

    public bool InspectorChannelDirty => _inspectorControl?.HasUnsavedChannel == true;
    public bool InspectorModeDirty => _inspectorControl?.HasUnsavedMode == true;
    public bool InspectorActionDirty => _inspectorControl?.HasUnsavedAction == true;
    public bool InspectorFeedbackSourceDirty => _inspectorControl?.HasUnsavedFeedbackSource == true;
    public bool InspectorFeedbackStyleDirty => _inspectorControl?.HasUnsavedFeedbackStyle == true;

    public void SetInspectorControl(BlueprintControlVm? control)
    {
        _inspectorControl = control;
        NotifyInspectorDirtyFlags();
    }

    private void NotifyInspectorDirtyFlags()
    {
        OnPropertyChanged(nameof(InspectorChannelDirty));
        OnPropertyChanged(nameof(InspectorModeDirty));
        OnPropertyChanged(nameof(InspectorActionDirty));
        OnPropertyChanged(nameof(InspectorFeedbackSourceDirty));
        OnPropertyChanged(nameof(InspectorFeedbackStyleDirty));
    }

    public bool HasUnsavedBindingDrafts => _hasUnsavedBindingDrafts || _hasUnsavedLayoutChanges;

    public bool CanSaveBindingDrafts => CanEditBindings && HasUnsavedBindingDrafts;

    public int Columns => (_draftLayout ?? _layout).Columns;

    public int Rows => (_draftLayout ?? _layout).Rows;

    public string LayoutName => (_draftLayout ?? _layout).Name;

    public string LayoutHint => IsLayoutConstructorMode
        ? "Layout constructor — drop onto the dashed + slot (beside / between / inside areas). Learn MIDI maps hardware here."
        : string.IsNullOrWhiteSpace(_layout.Hint)
            ? "Assign Sonar channels / LED feedback, then Save changes."
            : _layout.Hint!;

    /// <summary>Raised when insert-slot preview mutates VM grid coords (host should invalidate layout).</summary>
    public event Action? BlueprintLayoutRefreshRequested;

    /// <summary>Current insert-slot preview, if any.</summary>
    public MidiDropSlot? ActiveDropSlot => _activeDropSlot;

    private bool IsLearningActive => _learningControl is not null || _midi.IsLearning;

    public string? SelectedDeviceName
    {
        get => _selectedDeviceName;
        set
        {
            if (_selectedDeviceName == value)
            {
                return;
            }

            if (!ConfirmDiscardBindingDraftsIfNeeded("Switching device will discard unsaved channel assignments."))
            {
                OnPropertyChanged(); // nudge UI to resync selection
                return;
            }

            _selectedDeviceName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelectedDeviceInUse));
            OnPropertyChanged(nameof(UseDeviceButtonText));
            OnPropertyChanged(nameof(HasSelectedDevice));
            OnPropertyChanged(nameof(CanSaveBindingDrafts));
            if (_isLayoutConstructorMode)
            {
                CancelLayoutConstructor(silent: true);
            }

            ReloadLayoutForSelectedDevice();
        }
    }

    public bool HasSelectedDevice => !string.IsNullOrWhiteSpace(_selectedDeviceName);

    public bool IsSelectedDeviceInUse =>
        !string.IsNullOrWhiteSpace(_selectedDeviceName)
        && Devices.Any(d =>
            d.IsEnabled
            && string.Equals(d.Name, _selectedDeviceName, StringComparison.OrdinalIgnoreCase));

    public string UseDeviceButtonText => IsSelectedDeviceInUse ? "Stop using" : "Use device";

    public bool IsLayoutConstructorMode
    {
        get => _isLayoutConstructorMode;
        private set
        {
            if (_isLayoutConstructorMode == value)
            {
                return;
            }

            _isLayoutConstructorMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBlueprintInteractive));
            OnPropertyChanged(nameof(CanEditBindings));
            OnPropertyChanged(nameof(CanLearnHardware));
            OnPropertyChanged(nameof(HasUserLayoutOverride));
            OnPropertyChanged(nameof(UseRegionTreeLayout));
            OnPropertyChanged(nameof(UseConstructorTree));
            OnPropertyChanged(nameof(CanSaveBindingDrafts));
        }
    }

    /// <summary>Blueprint accepts clicks when device is in use, or always while editing layout.</summary>
    public bool IsBlueprintInteractive => IsSelectedDeviceInUse || IsLayoutConstructorMode;

    public bool CanEditBindings => IsSelectedDeviceInUse && !IsLayoutConstructorMode;

    /// <summary>Hardware Learn lives in the layout constructor only.</summary>
    public bool CanLearnHardware => IsLayoutConstructorMode && IsSelectedDeviceInUse;

    public bool HasUserLayoutOverride =>
        !string.IsNullOrWhiteSpace(_selectedDeviceName) && _presets.HasUserOverride(_selectedDeviceName);

    /// <summary>Always the nested cell tree (root areas and/or root controls).</summary>
    public bool UseRegionTreeLayout => true;

    /// <summary>Constructor uses the same tree with DnD enabled.</summary>
    public bool UseConstructorTree => IsLayoutConstructorMode;


    /// <summary>Currently selected constructor region id.</summary>
    public string? SelectedConstructorRegionId
    {
        get => _selectedConstructorRegionId;
        private set
        {
            if (string.Equals(_selectedConstructorRegionId, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedConstructorRegionId = value;
            OnPropertyChanged();
            SyncRegionSelection();
        }
    }


    public void SelectConstructorRegion(string? regionId)
    {
        SelectedConstructorRegionId = string.IsNullOrWhiteSpace(regionId) ? null : regionId;
    }

    public void ClearConstructorRegionSelection() => SelectedConstructorRegionId = null;

    public string? GetConstructorRegionLabel(string regionId)
    {
        var layout = _draftLayout ?? _layout;
        return layout.Regions
            .FirstOrDefault(r => string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase))
            ?.Label;
    }

    private void SyncRegionSelection()
    {
        void SyncTree(IEnumerable<object> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is BlueprintRegionVm region)
                {
                    region.IsSelected = string.Equals(
                        region.Id,
                        _selectedConstructorRegionId,
                        StringComparison.OrdinalIgnoreCase);
                    SyncTree(region.Children);
                }
            }
        }

        SyncTree(ConstructorRoots);
    }

    public MidiControlType PaletteSelectedType
    {
        get => _paletteSelectedType;
        set
        {
            if (_paletteSelectedType == value)
            {
                return;
            }

            _paletteSelectedType = value;
            OnPropertyChanged();
        }
    }

    public string DraftControlLabel
    {
        get => _draftControlLabel;
        set
        {
            var next = value ?? string.Empty;
            if (_draftControlLabel == next)
            {
                return;
            }

            _draftControlLabel = next;
            OnPropertyChanged();
        }
    }

    /// <summary>RowSpan editor for the selected control/area in the constructor (1..16).</summary>
    public int DraftRowSpan
    {
        get => _draftRowSpan;
        set
        {
            var next = Math.Clamp(value, 1, 16);
            if (_draftRowSpan == next)
            {
                return;
            }

            _draftRowSpan = next;
            OnPropertyChanged();
        }
    }

    /// <summary>ColSpan editor for the selected control/area in the constructor (1..16).</summary>
    public int DraftColSpan
    {
        get => _draftColSpan;
        set
        {
            var next = Math.Clamp(value, 1, 16);
            if (_draftColSpan == next)
            {
                return;
            }

            _draftColSpan = next;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// When checked for an area: solid border hidden in normal view; dashed outline in the constructor.
    /// </summary>
    public bool DraftHideBorder
    {
        get => _draftHideBorder;
        set
        {
            if (_draftHideBorder == value)
            {
                return;
            }

            _draftHideBorder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DraftKeepSpacingEnabled));
        }
    }

    /// <summary>True when the constructor selection is an area (not a control) — enables Hide border.</summary>
    public bool DraftHideBorderEnabled { get; private set; }

    /// <summary>
    /// With Hide border: keep a modest gap (channel strips / transport) instead of collapsing to zero.
    /// </summary>
    public bool DraftKeepSpacing
    {
        get => _draftKeepSpacing;
        set
        {
            if (_draftKeepSpacing == value)
            {
                return;
            }

            _draftKeepSpacing = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Keep spacing only makes sense for an area with Hide border on.</summary>
    public bool DraftKeepSpacingEnabled => DraftHideBorderEnabled && DraftHideBorder;

    /// <summary>Flex-like horizontal child distribution for the selected area.</summary>
    public MidiContentJustify DraftContentJustify
    {
        get => _draftContentJustify;
        set
        {
            if (_draftContentJustify == value)
            {
                return;
            }

            _draftContentJustify = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Flex-like vertical child distribution for the selected area.</summary>
    public MidiContentJustify DraftContentAlign
    {
        get => _draftContentAlign;
        set
        {
            if (_draftContentAlign == value)
            {
                return;
            }

            _draftContentAlign = value;
            OnPropertyChanged();
        }
    }

    /// <summary>LED feedback source for the selected control (None / Mute / ChannelAssigned).</summary>
    public string DraftFeedbackSourceTag
    {
        get => MidiFeedbackUi.ToSourceTag(_draftFeedbackSource);
        set
        {
            if (!MidiFeedbackUi.TryParseSourceTag(value, out var source))
            {
                return;
            }

            if (_draftFeedbackSource == source)
            {
                return;
            }

            _draftFeedbackSource = source;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DraftFeedbackTag));
            OnPropertyChanged(nameof(DraftFeedbackStyleEnabled));
        }
    }

    /// <summary>LED feedback style for the selected control (Solid / Blink).</summary>
    public string DraftFeedbackStyleTag
    {
        get => MidiFeedbackUi.ToStyleTag(_draftFeedbackStyle);
        set
        {
            if (!MidiFeedbackUi.TryParseStyleTag(value, out var style))
            {
                return;
            }

            if (_draftFeedbackStyle == style)
            {
                return;
            }

            _draftFeedbackStyle = style;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DraftFeedbackTag));
        }
    }

    /// <summary>LED feedback combo tag for the selected control (legacy combined tag).</summary>
    public string DraftFeedbackTag
    {
        get => MidiFeedbackUi.ToTag(
            _draftFeedbackSource,
            _draftFeedbackStyle);
        set
        {
            if (!MidiFeedbackUi.TryParseTag(value, out var source, out var style))
            {
                return;
            }

            if (_draftFeedbackSource == source && _draftFeedbackStyle == style)
            {
                return;
            }

            _draftFeedbackSource = source;
            _draftFeedbackStyle = style;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DraftFeedbackSource));
            OnPropertyChanged(nameof(DraftFeedbackSourceTag));
            OnPropertyChanged(nameof(DraftFeedbackStyle));
            OnPropertyChanged(nameof(DraftFeedbackStyleTag));
            OnPropertyChanged(nameof(DraftFeedbackStyleEnabled));
        }
    }

    public MidiFeedbackSource DraftFeedbackSource
    {
        get => _draftFeedbackSource;
        set
        {
            if (_draftFeedbackSource == value)
            {
                return;
            }

            _draftFeedbackSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DraftFeedbackTag));
            OnPropertyChanged(nameof(DraftFeedbackSourceTag));
            OnPropertyChanged(nameof(DraftFeedbackStyleEnabled));
        }
    }

    public MidiFeedbackStyle DraftFeedbackStyle
    {
        get => _draftFeedbackStyle;
        set
        {
            if (_draftFeedbackStyle == value)
            {
                return;
            }

            _draftFeedbackStyle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DraftFeedbackTag));
            OnPropertyChanged(nameof(DraftFeedbackStyleTag));
        }
    }

    /// <summary>True when a control (not an area) is selected for feedback editing.</summary>
    public bool DraftFeedbackEnabled
    {
        get => _draftFeedbackEnabled;
        private set
        {
            if (_draftFeedbackEnabled == value)
            {
                return;
            }

            _draftFeedbackEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DraftFeedbackStyleEnabled));
        }
    }

    /// <summary>Style (solid/blink) only for pads/encoders when source ≠ Off. Faders use soft-takeover only.</summary>
    public bool DraftFeedbackStyleEnabled =>
        _draftFeedbackEnabled
        && _draftFeedbackSource != MidiFeedbackSource.None
        && _draftFeedbackCanChooseStyle;

    /// <summary>True when an area is selected — enables H/V content justify.</summary>
    public bool DraftContentJustifyEnabled => DraftHideBorderEnabled;

    public bool ShowHiddenDevices
    {
        get => _showHiddenDevices;
        set
        {
            if (_showHiddenDevices == value)
            {
                return;
            }

            _showHiddenDevices = value;
            OnPropertyChanged();
            RefreshDevices();
        }
    }


    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Layout map scale (1.0 = 100%). Ctrl+wheel and +/- buttons adjust this.</summary>
    public double BlueprintZoom
    {
        get => _blueprintZoom;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), BlueprintZoomMin, BlueprintZoomMax);
            if (Math.Abs(_blueprintZoom - clamped) < 0.001)
            {
                return;
            }

            _blueprintZoom = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BlueprintZoomPercent));
            OnPropertyChanged(nameof(CanZoomBlueprintIn));
            OnPropertyChanged(nameof(CanZoomBlueprintOut));
        }
    }

    public string BlueprintZoomPercent => $"{(int)Math.Round(BlueprintZoom * 100)}%";

    public bool CanZoomBlueprintIn => BlueprintZoom < BlueprintZoomMax - 0.001;

    public bool CanZoomBlueprintOut => BlueprintZoom > BlueprintZoomMin + 0.001;

    public void ZoomBlueprintIn() => BlueprintZoom += BlueprintZoomStep;

    public void ZoomBlueprintOut() => BlueprintZoom -= BlueprintZoomStep;

    public void ResetBlueprintZoom() => BlueprintZoom = 1.0;

    /// <summary>
    /// Optional UI hook for absolute-fader conflict confirmation. Defaults to MessageBox.
    /// </summary>
    public Func<IReadOnlyList<MidiBinding>, Task<bool>>? ConflictConfirmationRequested { get; set; }

    public IReadOnlyList<string> ChannelOptions { get; } = SonarChannels.All
        .Select(SonarChannels.GetDisplayName)
        .ToList();

    public event PropertyChangedEventHandler? PropertyChanged;


    public async Task StartLearnAsync(BlueprintControlVm control)
    {
        if (!CanLearnHardware)
        {
            StatusText = "Use device and open Edit layout to Learn hardware.";
            return;
        }

        if (Devices.All(d => !d.IsEnabled) && string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Enable a MIDI device first.";
            return;
        }

        CancelLearn();
        _learningControl = control;
        control.IsLearning = true;
        StatusText = "Listening… move the hardware control now (or Cancel Learn).";

        _learnCts = new CancellationTokenSource();
        try
        {
            var mode = control.DefaultMode ?? MidiValueMode.Absolute;
            // null device filter → any enabled port (avoids MIDIIN2 vs primary mismatch)
            var evt = await _midi.BeginLearnAsync(
                deviceName: null,
                control.Id,
                mode,
                _learnCts.Token).ConfigureAwait(true);

            ApplyLearnedBinding(control, evt, mode);
        }
        catch (OperationCanceledException)
        {
            StatusText = "MIDI Learn cancelled.";
        }
        finally
        {
            control.IsLearning = false;
            if (ReferenceEquals(_learningControl, control))
            {
                _learningControl = null;
            }
        }
    }

    public void CancelLearn()
    {
        _learnCts?.Cancel();
        _learnCts?.Dispose();
        _learnCts = null;
        _midi.CancelLearn();
        if (_learningControl is not null)
        {
            _learningControl.IsLearning = false;
            _learningControl = null;
        }
    }



    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelLearn();
        _midi.ControlFeedback -= OnControlFeedback;
        _midi.Hub.DevicesChanged -= OnDevicesChanged;
        _midi.RawEventReceived -= OnRawEventForDiscovery;
    }

    private void ApplyLearnedBinding(BlueprintControlVm control, MidiIncomingEvent evt, MidiValueMode mode)
    {
        var action = MidiBindingActions.DefaultFor(control.Type);

        // Explicit Learn may steal hardware from another slot (needed when devices share CCs across controls).
        var previousOwner = Controls.FirstOrDefault(c =>
            !c.IsPlaceholder
            && !ReferenceEquals(c, control)
            && c.Controller == evt.Controller
            && c.IsNote == evt.IsNote
            && c.IsPitchBend == evt.IsPitchBend);
        if (previousOwner is not null)
        {
            ResetControlVm(previousOwner);
        }

        _midi.MappingStore.RemoveMatching(b =>
            b.Controller == evt.Controller
            && b.IsNote == evt.IsNote
            && b.IsPitchBend == evt.IsPitchBend
            && DevicesShareProduct(b.DeviceName, evt.DeviceName)
            && !string.Equals(b.ControlId, control.Id, StringComparison.OrdinalIgnoreCase));

        var previous = _midi.MappingStore.FindByControlId(evt.DeviceName, control.Id)
                       ?? (!string.IsNullOrWhiteSpace(SelectedDeviceName)
                           ? _midi.MappingStore.FindByControlId(SelectedDeviceName, control.Id)
                           : null);

        var resolvedMode = control.Type == MidiControlType.Button
            ? MidiValueMode.Absolute
            : mode;
        if (control.Type == MidiControlType.Encoder && !evt.IsNote && !evt.IsPitchBend)
        {
            var probe = GetProbe(evt);
            if (probe.LooksLikeRelativeTicks)
            {
                resolvedMode = MidiValueMode.Relative;
            }
        }

        if (evt.IsPitchBend)
        {
            resolvedMode = MidiValueMode.Absolute;
        }

        var binding = new MidiBinding
        {
            DeviceName = evt.DeviceName,
            Controller = evt.Controller,
            IsNote = evt.IsNote,
            IsPitchBend = evt.IsPitchBend,
            ChannelId = previous is { HasSonarChannel: true }
                ? previous.ChannelId
                : MidiBinding.UnassignedChannelId,
            Path = previous?.Path ?? SonarMixerPath.Monitoring,
            Mode = resolvedMode,
            Action = action,
            ControlId = control.Id,
            IsMotorized = previous?.IsMotorized ?? false,
            RelativeEncoding = previous?.RelativeEncoding ?? MidiRelativeEncoding.OffsetBinary,
            RelativeStep = previous?.RelativeStep
        };

        _midi.MappingStore.Upsert(binding);
        ApplyBindingToVm(control, binding);
        control.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
        var stolen = previousOwner is null ? string.Empty : $" (moved from {previousOwner.Label})";
        var hw = MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend);
        StatusText = binding.HasSonarChannel
            ? $"Linked {control.Label} to {hw} → {MidiBinding.FormatChannelLabel(binding.ChannelId)}.{stolen}"
            : $"Discovered {control.Label} as {hw} on {evt.DeviceName}.{stolen}";
    }

    private BlueprintControlVm? FindControlOwningHardware(MidiIncomingEvent evt) =>
        Controls.FirstOrDefault(c =>
            !c.IsPlaceholder
            && c.Controller == evt.Controller
            && c.IsNote == evt.IsNote
            && c.IsPitchBend == evt.IsPitchBend);

    private void OnRawEventForDiscovery(MidiIncomingEvent evt)
    {
        if (_disposed || IsLearningActive)
        {
            return;
        }

        if (!IsEventFromActiveDevice(evt.DeviceName))
        {
            return;
        }

        if (evt.IsNote && !evt.IsNoteOn)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            var hw = MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend);
            var raw = MidiValueParser.FormatRawDisplay(evt.RawValue, evt.IsPitchBend);
            StatusText = $"MIDI ← {evt.DeviceName}: {hw} = {raw}";
            TryAutoDiscover(evt);
        });
    }

    private void TryAutoDiscover(MidiIncomingEvent evt)
    {
        if (IsLearningActive)
        {
            return;
        }

        ObserveCc(evt);
        var probe = GetProbe(evt);

        var existingVm = Controls.FirstOrDefault(c =>
            !c.IsPlaceholder
            && c.Controller == evt.Controller
            && c.IsNote == evt.IsNote
            && c.IsPitchBend == evt.IsPitchBend);

        if (existingVm is not null)
        {
            existingVm.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
            if (existingVm.Type == MidiControlType.Button)
            {
                existingVm.IsPressed = evt.RawValue > 0;
            }

            // Shared-CC hardware: a button may share a controller with an encoder on the same device.
            if (existingVm.Type is MidiControlType.Encoder or MidiControlType.Fader
                && !evt.IsPitchBend
                && (evt.IsNote || probe.LooksLikeButton || evt.RawValue is 0 or 127))
            {
                StatusText =
                    $"{MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend)} is already on {existingVm.Label}. " +
                    "Same CC as this button. If your device has a Note/Pitch Bend mode for unique buttons+faders, use that, " +
                    "or Reset that encoder and Learn the button instead.";
            }

            return;
        }

        // Another blueprint slot already owns this hardware — do not silently create a duplicate.
        var owner = FindControlOwningHardware(evt);
        if (owner is not null)
        {
            owner.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
            StatusText =
                $"{MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend)} already bound to {owner.Label}. " +
                "Click a free slot and Learn to reassign, or Reset the owner first.";
            return;
        }

        // Wait until we know if this CC is continuous (encoder/fader) or a 0/127 button.
        // Pitch Bend faders (SMC-Mixer DAW Mode / Mode A) are ready immediately.
        if (!evt.IsNote && !evt.IsPitchBend && !probe.IsReadyToClassify)
        {
            return;
        }

        var storeBinding = FindStoredBinding(evt);
        if (storeBinding is not null)
        {
            var byId = !string.IsNullOrWhiteSpace(storeBinding.ControlId)
                ? Controls.FirstOrDefault(c =>
                    string.Equals(c.Id, storeBinding.ControlId, StringComparison.OrdinalIgnoreCase)
                    && c.Controller is null
                    && !c.IsPlaceholder)
                : null;

            var target = byId ?? PickUnboundControl(evt, probe);
            if (target is null)
            {
                return;
            }

            storeBinding.ControlId = target.Id;
            storeBinding.DeviceName = evt.DeviceName;
            storeBinding.IsPitchBend = evt.IsPitchBend;
            storeBinding.Action = MidiBindingActions.DefaultFor(target.Type);
            storeBinding.Mode = target.Type == MidiControlType.Encoder
                ? (target.DefaultMode ?? MidiValueMode.Absolute)
                : MidiValueMode.Absolute;
            if (target.Type == MidiControlType.Button || evt.IsPitchBend)
            {
                storeBinding.Mode = MidiValueMode.Absolute;
            }

            _midi.MappingStore.Upsert(storeBinding);
            ApplyBindingToVm(target, storeBinding);
            target.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
            if (target.Type == MidiControlType.Button)
            {
                target.IsPressed = evt.RawValue > 0;
            }

            return;
        }

        var free = PickUnboundControl(evt, probe);
        if (free is null)
        {
            return;
        }

        var mode = free.Type switch
        {
            MidiControlType.Button => MidiValueMode.Absolute,
            _ when evt.IsPitchBend => MidiValueMode.Absolute,
            MidiControlType.Encoder when probe.LooksLikeRelativeTicks => MidiValueMode.Relative,
            MidiControlType.Encoder => free.DefaultMode ?? MidiValueMode.Absolute,
            _ => free.DefaultMode ?? MidiValueMode.Absolute
        };

        var binding = new MidiBinding
        {
            DeviceName = evt.DeviceName,
            Controller = evt.Controller,
            IsNote = evt.IsNote,
            IsPitchBend = evt.IsPitchBend,
            ChannelId = MidiBinding.UnassignedChannelId,
            Path = SonarMixerPath.Monitoring,
            Mode = mode,
            Action = MidiBindingActions.DefaultFor(free.Type),
            ControlId = free.Id,
            IsMotorized = false
        };

        _midi.MappingStore.Upsert(binding);
        ApplyBindingToVm(free, binding);
        free.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
        if (free.Type == MidiControlType.Button)
        {
            free.IsPressed = evt.RawValue > 0;
        }

        StatusText =
            $"Discovered {free.Label} as {MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend)}. Assign a Sonar channel when ready.";
    }

    private MidiBinding? FindStoredBinding(MidiIncomingEvent evt)
    {
        var direct = _midi.MappingStore.FindByController(
            evt.DeviceName,
            evt.Controller,
            evt.IsNote,
            evt.IsPitchBend);
        if (direct is not null)
        {
            return direct;
        }

        // Same physical box may appear under a secondary port name vs the primary product name.
        return _midi.MappingStore.Bindings.FirstOrDefault(b =>
            b.Controller == evt.Controller
            && b.IsNote == evt.IsNote
            && b.IsPitchBend == evt.IsPitchBend
            && DevicesShareProduct(b.DeviceName, evt.DeviceName));
    }

    private void OnControlFeedback(MidiControlFeedback feedback)
    {
        if (_disposed)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            if (!IsEventFromActiveDevice(feedback.DeviceName))
            {
                return;
            }

            var matched = false;
            foreach (var control in Controls)
            {
                if (control.Controller == feedback.Controller
                    && control.IsNote == feedback.IsNote
                    && control.IsPitchBend == feedback.IsPitchBend)
                {
                    if (control.UsesRelativeNeedle)
                    {
                        control.ApplyIncomingVisual(feedback.RawValue, feedback.IsPitchBend);
                    }
                    else
                    {
                        // Prefer normalized (restore / absolute) over re-deriving from raw.
                        control.NormalizedValue = feedback.NormalizedValue;
                    }

                    if (control.Type == MidiControlType.Button)
                    {
                        control.IsPressed = feedback.RawValue > 0;
                    }

                    matched = true;
                }
            }

            if (!matched)
            {
                TryAutoDiscover(new MidiIncomingEvent(
                    feedback.DeviceName,
                    feedback.Controller,
                    feedback.RawValue,
                    feedback.IsNote,
                    IsNoteOn: feedback.IsNote && feedback.RawValue > 0,
                    feedback.IsPitchBend));
            }
        });
    }

    private bool IsEventFromActiveDevice(string deviceName)
    {
        var enabled = _midi.MappingStore.EnabledDevices;
        if (enabled.Count > 0)
        {
            return enabled.Any(d => string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase));
        }

        return !string.IsNullOrWhiteSpace(SelectedDeviceName)
               && string.Equals(deviceName, SelectedDeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DevicesShareProduct(string left, string right) =>
        MidiDevicePortNaming.DevicesShareProduct(left, right);

    private BlueprintControlVm? PickUnboundControl(MidiIncomingEvent evt, CcProbe probe)
    {
        var unbound = Controls.Where(c => !c.IsPlaceholder && c.Controller is null).ToList();
        if (unbound.Count == 0)
        {
            return null;
        }

        // DAW Mode faders: Pitch Bend channel 0–7 → CH1–CH8 by column.
        if (evt.IsPitchBend)
        {
            if (evt.Controller is >= 0 and <= 7)
            {
                var byCol = unbound.FirstOrDefault(c =>
                    c.Type == MidiControlType.Fader && c.Col == evt.Controller);
                if (byCol is not null)
                {
                    return byCol;
                }
            }

            return unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
                   ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Encoder)
                   ?? unbound[0];
        }

        if (evt.IsNote || probe.LooksLikeButton)
        {
            return unbound.FirstOrDefault(c => c.Type == MidiControlType.Button)
                   ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
                   ?? unbound[0];
        }

        if (probe.LooksLikeRelativeTicks || probe.LooksLikeEncoderBurst)
        {
            return unbound.FirstOrDefault(c => c.Type == MidiControlType.Encoder)
                   ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
                   ?? unbound[0];
        }

        // Continuous absolute stream (knobs/faders sending repeating absolute values).
        if (probe.LooksLikeContinuous)
        {
            return unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
                   ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Encoder)
                   ?? unbound[0];
        }

        return unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
               ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Encoder)
               ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Button)
               ?? unbound[0];
    }

    private void ObserveCc(MidiIncomingEvent evt)
    {
        if (evt.IsNote || evt.IsPitchBend)
        {
            return;
        }

        var probe = GetProbe(evt);
        var now = DateTime.UtcNow;
        if (probe.Events > 0 && (now - probe.LastEventUtc).TotalMilliseconds <= 40)
        {
            probe.RapidBursts++;
        }

        probe.LastEventUtc = now;
        probe.Events++;
        if (evt.RawValue is 1 or 2 or 3)
        {
            probe.SeenRelativePlus = true;
        }

        if (evt.RawValue is >= 65 and <= 67)
        {
            probe.SeenRelativeMinus = true;
        }

        if (evt.RawValue is > 0 and < 127)
        {
            probe.SeenIntermediate = true;
            if (evt.RawValue is not (1 or 2 or 3 or >= 65 and <= 67))
            {
                probe.SeenBroadAbsolute = true;
            }
        }

        if (evt.RawValue == 0)
        {
            probe.SeenZero = true;
        }

        if (evt.RawValue == 127)
        {
            probe.SeenMax = true;
        }
    }

    private CcProbe GetProbe(MidiIncomingEvent evt)
    {
        var kind = evt.IsPitchBend ? "P" : evt.IsNote ? "N" : "C";
        var key = $"{evt.DeviceName}|{kind}|{evt.Controller}";
        if (!_ccProbes.TryGetValue(key, out var probe))
        {
            probe = new CcProbe();
            _ccProbes[key] = probe;
        }

        return probe;
    }

    private sealed class CcProbe
    {
        public int Events { get; set; }
        public int RapidBursts { get; set; }
        public DateTime LastEventUtc { get; set; }
        public bool SeenIntermediate { get; set; }
        public bool SeenBroadAbsolute { get; set; }
        public bool SeenRelativePlus { get; set; }
        public bool SeenRelativeMinus { get; set; }
        public bool SeenZero { get; set; }
        public bool SeenMax { get; set; }

        public bool LooksLikeContinuous => SeenIntermediate;

        public bool LooksLikeEncoderBurst => SeenIntermediate && RapidBursts >= 2;

        /// <summary>DAW Mode knobs: only +ticks (1..3) and/or -ticks (65..67).</summary>
        public bool LooksLikeRelativeTicks =>
            !SeenBroadAbsolute
            && !SeenZero
            && !SeenMax
            && (SeenRelativePlus || SeenRelativeMinus)
            && Events >= 1;

        public bool LooksLikeButton =>
            !SeenIntermediate && SeenZero && SeenMax && Events >= 2;

        public bool IsReadyToClassify =>
            LooksLikeContinuous || LooksLikeButton || LooksLikeRelativeTicks;
    }

    private string? ResolveStorageDeviceName(int controller, bool isNote, bool isPitchBend, string controlId)
    {
        if (!string.IsNullOrWhiteSpace(SelectedDeviceName)
            && !MidiDevicePortNaming.IsSecondaryPortName(SelectedDeviceName))
        {
            return SelectedDeviceName;
        }

        var available = _midi.Hub.GetAvailableDeviceNames();
        var enabledPrimary = MidiDevicePortNaming.PreferPrimaryDeviceName(_midi.MappingStore.EnabledDevices);
        if (!string.IsNullOrWhiteSpace(enabledPrimary)
            && !MidiDevicePortNaming.IsSecondaryPortName(enabledPrimary))
        {
            return enabledPrimary;
        }

        if (!string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            var primaryForSelected = available.FirstOrDefault(a =>
                !MidiDevicePortNaming.IsSecondaryPortName(a)
                && MidiDevicePortNaming.DevicesShareProduct(a, SelectedDeviceName));
            if (!string.IsNullOrWhiteSpace(primaryForSelected))
            {
                return primaryForSelected;
            }

            return SelectedDeviceName;
        }

        var stored = _midi.MappingStore.Bindings.FirstOrDefault(b =>
            b.Controller == controller
            && b.IsNote == isNote
            && b.IsPitchBend == isPitchBend
            && (string.Equals(b.ControlId, controlId, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(SelectedDeviceName)
                    && DevicesShareProduct(b.DeviceName, SelectedDeviceName))));

        if (stored is not null && MidiDevicePortNaming.IsSecondaryPortName(stored.DeviceName))
        {
            var primary = available.FirstOrDefault(a =>
                !MidiDevicePortNaming.IsSecondaryPortName(a)
                && MidiDevicePortNaming.DevicesShareProduct(a, stored.DeviceName));
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary;
            }
        }

        return stored?.DeviceName
               ?? MidiDevicePortNaming.PreferPrimaryDeviceName(available);
    }

    private static string ResolveChannelId(string? channelIdOrDisplayName)
    {
        if (string.IsNullOrWhiteSpace(channelIdOrDisplayName)
            || string.Equals(channelIdOrDisplayName, "Not assigned", StringComparison.OrdinalIgnoreCase)
            || string.Equals(channelIdOrDisplayName, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return MidiBinding.UnassignedChannelId;
        }

        if (SonarChannels.IsValidChannel(channelIdOrDisplayName))
        {
            return SonarChannels.NormalizeChannel(channelIdOrDisplayName);
        }

        var byDisplay = SonarChannels.All.FirstOrDefault(c =>
            string.Equals(SonarChannels.GetDisplayName(c), channelIdOrDisplayName, StringComparison.OrdinalIgnoreCase));
        return byDisplay ?? MidiBinding.UnassignedChannelId;
    }

    private Task<bool> ConfirmConflictAsync(IReadOnlyList<MidiBinding> conflicts)
    {
        if (ConflictConfirmationRequested is not null)
        {
            return ConflictConfirmationRequested(conflicts);
        }

        var tcs = new TaskCompletionSource<bool>();
        _dispatcher.Invoke(() =>
        {
            var owners = string.Join(", ", conflicts.Select(c => $"{c.DeviceName} CC{c.Controller}"));
            var result = WpfMessageBox.Show(
                $"Are you sure? Multiple non-motorized faders on one channel will cause volume fighting.\n\nExisting: {owners}",
                "MIDI mapping conflict",
                WpfMessageBoxButton.YesNo,
                WpfMessageBoxImage.Warning);
            tcs.SetResult(result == WpfMessageBoxResult.Yes);
        });
        return tcs.Task;
    }

    private void ReloadLayoutForSelectedDevice()
    {
        _bindingDrafts.Clear();
        SetBindingDraftDirty(false);
        SetLayoutDraftDirty(false);

        _layout = _presets.Resolve(_selectedDeviceName);

        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(LayoutName));
        OnPropertyChanged(nameof(LayoutHint));
        OnPropertyChanged(nameof(UseRegionTreeLayout));
        OnPropertyChanged(nameof(UseConstructorTree));
        OnPropertyChanged(nameof(HasUserLayoutOverride));
        OnPropertyChanged(nameof(IsBlueprintInteractive));
        OnPropertyChanged(nameof(CanEditBindings));
        OnPropertyChanged(nameof(CanSaveBindingDrafts));

        if (!string.IsNullOrWhiteSpace(_selectedDeviceName) && IsSelectedDeviceInUse)
        {
            SeedFactoryBindingsForDevices([_selectedDeviceName]);
        }

        Controls.Clear();
        ConstructorRoots.Clear();
        RebuildLayoutTree(_layout);
        CapturePersistedFeedbackSnapshot();
        RefreshLayoutPresets();
        RefreshControlUnsavedIndicators();
    }

    private void RefreshLayoutPresets(string? selectKey = null)
    {
        var key = selectKey
                  ?? (!string.IsNullOrWhiteSpace(_selectedDeviceName)
                      ? _presets.GetActivePresetKey(_selectedDeviceName)
                      : MidiPresetSelectionStore.OfficialKey);

        _suppressPresetSelection = true;
        try
        {
            LayoutPresets.Clear();
            if (string.IsNullOrWhiteSpace(_selectedDeviceName))
            {
                _selectedLayoutPreset = null;
                OnPropertyChanged(nameof(SelectedLayoutPreset));
                OnPropertyChanged(nameof(CanDeleteSelectedLayoutPreset));
                OnPropertyChanged(nameof(CanRenameSelectedLayoutPreset));
                return;
            }

            foreach (var option in _presets.ListPresetsForDevice(_selectedDeviceName))
            {
                LayoutPresets.Add(option);
            }

            _selectedLayoutPreset = LayoutPresets.FirstOrDefault(p =>
                                       string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                                   ?? LayoutPresets.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedLayoutPreset));
            OnPropertyChanged(nameof(CanDeleteSelectedLayoutPreset));
            OnPropertyChanged(nameof(CanRenameSelectedLayoutPreset));
        }
        finally
        {
            _suppressPresetSelection = false;
        }
    }

    private BlueprintControlVm CreateControlVm(MidiLayoutControl control, bool compact = false, bool tallFader = false)
    {
        var vm = new BlueprintControlVm
        {
            Id = control.Id,
            RegionId = control.RegionId,
            Label = control.Label,
            Type = control.Type,
            Row = control.Row,
            Col = control.Col,
            RowSpan = Math.Max(1, control.RowSpan),
            ColSpan = Math.Max(1, control.ColSpan),
            DefaultMode = control.DefaultMode,
            Compact = compact,
            TallFader = tallFader,
            IsConstructorMode = IsLayoutConstructorMode
        };

        if (!string.IsNullOrWhiteSpace(_selectedDeviceName))
        {
            var existing = FindBindingForControl(_selectedDeviceName, control.Id);
            if (existing is not null)
            {
                ApplyBindingToVm(vm, existing);
            }
            else if (control.HasFactoryHardware)
            {
                var preview = PresetCatalog.CreateFactoryBinding(control, _selectedDeviceName);
                ApplyBindingToVm(vm, preview);
            }
        }

        return vm;
    }

    private static void ResetControlVm(BlueprintControlVm control)
    {
        control.HasBinding = false;
        control.Controller = null;
        control.IsNote = false;
        control.IsPitchBend = false;
        control.ChannelId = MidiBinding.UnassignedChannelId;
        control.NormalizedValue = 0f;
        control.IsPressed = false;
        control.IsLearning = false;
        control.Mode = control.DefaultMode ?? MidiValueMode.Absolute;
        control.RelativeEncoding = MidiRelativeEncoding.OffsetBinary;
        control.ResetRelativeNeedle();
        control.Action = MidiBindingActions.DefaultFor(control.Type);
        control.BindingSummary = "Unmapped — move control to discover";
    }

    private MidiBinding? FindBindingForControl(string deviceName, string controlId)
    {
        var direct = _midi.MappingStore.FindByControlId(deviceName, controlId);
        if (direct is not null)
        {
            return direct;
        }

        return _midi.MappingStore.Bindings.FirstOrDefault(b =>
            string.Equals(b.ControlId, controlId, StringComparison.OrdinalIgnoreCase)
            && DevicesShareProduct(b.DeviceName, deviceName));
    }

    private void ApplyBindingToVm(BlueprintControlVm vm, MidiBinding binding)
    {
        var modeChangedToRelative = binding.Mode == MidiValueMode.Relative && vm.Mode != MidiValueMode.Relative;
        vm.HasBinding = true;
        vm.Controller = binding.Controller;
        vm.IsNote = binding.IsNote;
        vm.IsPitchBend = binding.IsPitchBend;
        vm.ChannelId = binding.ChannelId;
        vm.Mode = binding.Mode;
        vm.RelativeEncoding = binding.RelativeEncoding;
        vm.Action = MidiBindingActions.Normalize(vm.Type, binding.Action);
        if (modeChangedToRelative)
        {
            vm.ResetRelativeNeedle();
        }

        var hardware = MidiBinding.FormatHardwareLabel(binding.IsNote, binding.Controller, binding.IsPitchBend);
        vm.BindingSummary = binding.HasSonarChannel
            ? $"{hardware} → {MidiBinding.FormatChannelLabel(binding.ChannelId)} ({binding.Mode})"
            : $"{hardware} — not assigned";

        // Restore fader/absolute encoder chrome from persisted or cached volume.
        if (!vm.UsesRelativeNeedle
            && MidiControlStateStore.IsPersistableAbsoluteVolume(binding)
            && _midi.TryGetAbsoluteVisual(binding, out var volume))
        {
            vm.NormalizedValue = volume;
        }
    }

    private void ApplyAssignmentToVm(
        BlueprintControlVm control,
        string channelId,
        MidiValueMode mode,
        MidiBindingAction action)
    {
        var modeChangedToRelative = mode == MidiValueMode.Relative && control.Mode != MidiValueMode.Relative;
        control.ChannelId = channelId;
        control.Mode = mode;
        control.Action = action;
        if (modeChangedToRelative)
        {
            control.ResetRelativeNeedle();
        }

        if (control.Controller is int cc)
        {
            var hardware = MidiBinding.FormatHardwareLabel(control.IsNote, cc, control.IsPitchBend);
            control.BindingSummary = !string.IsNullOrWhiteSpace(channelId) && SonarChannels.IsValidChannel(channelId)
                ? $"{hardware} → {MidiBinding.FormatChannelLabel(channelId)} ({mode}) · draft"
                : $"{hardware} — not assigned · draft";
        }
        else
        {
            control.BindingSummary = !string.IsNullOrWhiteSpace(channelId) && SonarChannels.IsValidChannel(channelId)
                ? $"{MidiBinding.FormatChannelLabel(channelId)} ({mode}) · draft (no hardware yet)"
                : "Unmapped — move control to discover";
        }
    }

    private void SetBindingDraftDirty(bool dirty)
    {
        if (_hasUnsavedBindingDrafts == dirty)
        {
            OnPropertyChanged(nameof(HasUnsavedBindingDrafts));
            OnPropertyChanged(nameof(CanSaveBindingDrafts));
            return;
        }

        _hasUnsavedBindingDrafts = dirty;
        OnPropertyChanged(nameof(HasUnsavedBindingDrafts));
        OnPropertyChanged(nameof(CanSaveBindingDrafts));
    }

    private static string NormalizeChannelCompare(string? channelId) =>
        string.IsNullOrWhiteSpace(channelId) ? string.Empty : SonarChannels.NormalizeChannel(channelId);

    private void OnDevicesChanged() => RefreshDevices();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
