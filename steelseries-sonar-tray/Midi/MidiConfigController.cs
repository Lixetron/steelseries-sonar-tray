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

public sealed class MidiConfigController : INotifyPropertyChanged, IDisposable
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

    public void RefreshDevices()
    {
        var available = _midi.Hub.GetAvailableDeviceNames().ToList();
        var migrated = _midi.MappingStore.MigrateSecondaryPortBindings(available);
        if (_midi.MappingStore.DisableHiddenEnabledDevices(available) > 0 || migrated > 0)
        {
            _midi.ApplyEnabledDevicesFromStore();
        }

        var enabled = _midi.MappingStore.EnabledDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _dispatcher.Invoke(() =>
        {
            Devices.Clear();
            foreach (var name in available)
            {
                var hidden = _midi.MappingStore.IsEffectivelyHidden(name, available);
                if (hidden && !_showHiddenDevices)
                {
                    continue;
                }

                Devices.Add(new MidiDeviceListItemVm
                {
                    Name = name,
                    IsEnabled = enabled.Contains(name),
                    IsHidden = hidden
                });
            }

            var visibleNames = Devices.Select(d => d.Name).ToList();
            if (SelectedDeviceName is null
                || !visibleNames.Contains(SelectedDeviceName, StringComparer.OrdinalIgnoreCase))
            {
                var preferredSource = enabled.Count > 0
                    ? enabled.Where(n => visibleNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                    : visibleNames;
                SelectedDeviceName = MidiDevicePortNaming.PreferPrimaryDeviceName(preferredSource)
                                    ?? visibleNames.FirstOrDefault();
            }
            else
            {
                OnPropertyChanged(nameof(IsSelectedDeviceInUse));
                OnPropertyChanged(nameof(UseDeviceButtonText));
                OnPropertyChanged(nameof(HasSelectedDevice));
            }
        });
    }

    /// <summary>Enable or disable the currently previewed device for MIDI input.</summary>
    public void ToggleSelectedDeviceUse()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a MIDI device in the list first.";
            return;
        }

        if (IsSelectedDeviceInUse)
        {
            StopUsingSelectedDevice();
        }
        else
        {
            UseSelectedDevice();
        }
    }

    public void UseSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a MIDI device in the list first.";
            return;
        }

        var available = _midi.Hub.GetAvailableDeviceNames().ToList();
        if (_midi.MappingStore.IsEffectivelyHidden(SelectedDeviceName, available))
        {
            // Using a hidden/duplicate port: keep it visible after Use.
            _midi.MappingStore.RevealDevice(SelectedDeviceName);
        }

        var device = Devices.FirstOrDefault(d =>
            string.Equals(d.Name, SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            RefreshDevices();
            device = Devices.FirstOrDefault(d =>
                string.Equals(d.Name, SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        }

        if (device is null)
        {
            StatusText = "Device is no longer available.";
            return;
        }

        device.IsEnabled = true;
        device.IsHidden = false;
        PersistEnabledDevices();
        StatusText = $"Using {SelectedDeviceName}. Assign Sonar channels or Learn MIDI as needed.";
    }

    public void StopUsingSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            return;
        }

        CancelLearn();
        var device = Devices.FirstOrDefault(d =>
            string.Equals(d.Name, SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (device is not null)
        {
            device.IsEnabled = false;
        }

        PersistEnabledDevices();
        StatusText = $"{SelectedDeviceName} is preview-only (not used). Click Use device to activate.";
    }

    public void HideSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a device to hide.";
            return;
        }

        var name = SelectedDeviceName;
        CancelLearn();
        _midi.MappingStore.HideDevice(name);
        _midi.ApplyEnabledDevicesFromStore();
        StatusText = $"Hidden {name}. Enable \"Show hidden\" to bring it back.";
        RefreshDevices();
    }

    public void RevealSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a device to show.";
            return;
        }

        var name = SelectedDeviceName;
        _midi.MappingStore.RevealDevice(name);
        StatusText = $"Showing {name}. You can Use device to activate it.";
        if (!_showHiddenDevices)
        {
            // Ensure it appears even when the filter is off (user explicitly revealed it).
            RefreshDevices();
        }
        else
        {
            RefreshDevices();
        }
    }

    public void PersistEnabledDevices()
    {
        var available = _midi.Hub.GetAvailableDeviceNames().ToList();
        var enabled = Devices
            .Where(d => d.IsEnabled)
            .Select(d => d.Name)
            .Where(name => !_midi.MappingStore.IsEffectivelyHidden(name, available))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Keep an explicitly revealed+used port even if the list item still shows Hidden briefly.
        var selectedInUse = Devices.FirstOrDefault(d =>
            d.IsEnabled
            && string.Equals(d.Name, SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (selectedInUse is not null
            && _midi.MappingStore.RevealedDevices.Any(d =>
                string.Equals(d, selectedInUse.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (!enabled.Contains(selectedInUse.Name, StringComparer.OrdinalIgnoreCase))
            {
                enabled.Add(selectedInUse.Name);
            }
        }

        _midi.MappingStore.SetEnabledDevices(enabled);
        _midi.ApplyEnabledDevicesFromStore();

        OnPropertyChanged(nameof(IsSelectedDeviceInUse));
        OnPropertyChanged(nameof(UseDeviceButtonText));
        OnPropertyChanged(nameof(CanLearnHardware));
        OnPropertyChanged(nameof(IsBlueprintInteractive));
        OnPropertyChanged(nameof(CanEditBindings));

        if (enabled.Count == 0)
        {
            ReloadLayoutForSelectedDevice();
            return;
        }

        var seeded = SeedFactoryBindingsForDevices(enabled);
        ReloadLayoutForSelectedDevice();
        if (seeded > 0 && IsSelectedDeviceInUse)
        {
            StatusText =
                $"Listening to {enabled.Count} device(s). Applied {seeded} factory mapping(s) from preset — assign Sonar channels.";
        }
    }

    /// <summary>
    /// Seeds missing factory hardware from the official preset without overwriting Learn/routes.
    /// </summary>
    public int SeedFactoryBindingsForDevices(IEnumerable<string> deviceNames)
    {
        var seeded = 0;
        foreach (var deviceName in deviceNames
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var layout = _presets.Resolve(deviceName);
            foreach (var factory in PresetCatalog.BuildFactoryBindings(layout, deviceName))
            {
                var controlId = factory.ControlId ?? string.Empty;
                var existingByControl = _midi.MappingStore.FindByControlId(deviceName, controlId)
                    ?? _midi.MappingStore.Bindings.FirstOrDefault(b =>
                        string.Equals(b.ControlId, controlId, StringComparison.OrdinalIgnoreCase)
                        && DevicesShareProduct(b.DeviceName, deviceName));

                if (existingByControl is not null)
                {
                    continue;
                }

                var existingHw = _midi.MappingStore.FindByController(
                    deviceName,
                    factory.Controller,
                    factory.IsNote,
                    factory.IsPitchBend)
                    ?? _midi.MappingStore.Bindings.FirstOrDefault(b =>
                        b.Controller == factory.Controller
                        && b.IsNote == factory.IsNote
                        && b.IsPitchBend == factory.IsPitchBend
                        && DevicesShareProduct(b.DeviceName, deviceName));

                if (existingHw is not null)
                {
                    continue;
                }

                _midi.MappingStore.Upsert(factory);
                seeded++;
            }
        }

        return seeded;
    }

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

    /// <summary>
    /// Stages channel / mode / action on the blueprint without writing midi-mappings.json.
    /// Call <see cref="SaveBindingDraftsAsync"/> to persist all staged assignments.
    /// </summary>
    public void StageBindingAssignment(
        BlueprintControlVm control,
        string? channelIdOrDisplayName,
        MidiValueMode mode,
        MidiBindingAction action)
    {
        if (!CanEditBindings || control.IsPlaceholder)
        {
            return;
        }

        var channelId = ResolveChannelId(channelIdOrDisplayName);
        action = MidiBindingActions.Normalize(control.Type, action);
        ApplyAssignmentToVm(control, channelId, mode, action);

        var persisted = !string.IsNullOrWhiteSpace(SelectedDeviceName)
            ? FindBindingForControl(SelectedDeviceName, control.Id)
            : null;

        var matchesPersisted = persisted is not null
                               && string.Equals(NormalizeChannelCompare(persisted.ChannelId), NormalizeChannelCompare(channelId), StringComparison.OrdinalIgnoreCase)
                               && persisted.Mode == mode
                               && MidiBindingActions.Normalize(control.Type, persisted.Action) == action
                               && control.Controller is int cc
                               && persisted.Controller == cc
                               && persisted.IsNote == control.IsNote
                               && persisted.IsPitchBend == control.IsPitchBend;

        if (matchesPersisted
            || (persisted is null
                && control.Controller is null
                && string.IsNullOrWhiteSpace(channelId)
                && mode == MidiValueMode.Absolute
                && action == MidiBindingAction.None))
        {
            _bindingDrafts.Remove(control.Id);
            if (persisted is not null)
            {
                ApplyBindingToVm(control, persisted);
            }
        }
        else
        {
            _bindingDrafts[control.Id] = new BindingAssignmentDraft(control.Id, channelId, mode, action);
        }

        SetBindingDraftDirty(_bindingDrafts.Count > 0);
        RefreshControlUnsavedIndicators(control.Id);
        StatusText = _hasUnsavedBindingDrafts || _hasUnsavedLayoutChanges
            ? $"Staged {control.Label} — assign the rest, then Save changes."
            : $"{control.Label} matches the saved mapping.";
    }

    public async Task<bool> SaveBindingDraftsAsync()
    {
        if (!CanEditBindings)
        {
            StatusText = "Enable the device to save assignments.";
            return false;
        }

        if (_bindingDrafts.Count == 0 && !_hasUnsavedLayoutChanges)
        {
            StatusText = "Nothing to save.";
            return true;
        }

        var deviceName = SelectedDeviceName;
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            StatusText = "Select a device first.";
            return false;
        }

        var saved = 0;
        var skipped = 0;
        foreach (var draft in _bindingDrafts.Values.ToList())
        {
            var control = Controls.FirstOrDefault(c =>
                string.Equals(c.Id, draft.ControlId, StringComparison.OrdinalIgnoreCase));
            if (control is null || control.Controller is null)
            {
                skipped++;
                continue;
            }

            var storageDevice = ResolveStorageDeviceName(
                control.Controller.Value,
                control.IsNote,
                control.IsPitchBend,
                control.Id) ?? deviceName;

            var binding = new MidiBinding
            {
                DeviceName = storageDevice,
                Controller = control.Controller.Value,
                IsNote = control.IsNote,
                IsPitchBend = control.IsPitchBend,
                ChannelId = draft.ChannelId,
                Path = SonarMixerPath.Monitoring,
                Mode = draft.Mode,
                Action = MidiBindingActions.Normalize(control.Type, draft.Action),
                ControlId = control.Id,
                IsMotorized = false
            };

            _midi.MappingStore.RemoveMatching(b =>
                b.Controller == binding.Controller
                && b.IsNote == binding.IsNote
                && b.IsPitchBend == binding.IsPitchBend
                && DevicesShareProduct(b.DeviceName, binding.DeviceName)
                && !string.Equals(b.DeviceName, binding.DeviceName, StringComparison.OrdinalIgnoreCase));

            if (MidiConflictValidator.RequiresConflictConfirmation(_midi.MappingStore, binding, out var conflicts))
            {
                var confirmed = await ConfirmConflictAsync(conflicts).ConfigureAwait(true);
                if (!confirmed)
                {
                    StatusText = "Save cancelled — conflict not confirmed.";
                    return false;
                }
            }

            _midi.MappingStore.Upsert(binding);
            ApplyBindingToVm(control, binding);
            saved++;
        }

        _bindingDrafts.Clear();
        SetBindingDraftDirty(false);

        var layoutSaved = false;
        if (_hasUnsavedLayoutChanges)
        {
            if (!PersistActiveLayoutToUserPreset())
            {
                return false;
            }

            SetLayoutDraftDirty(false);
            layoutSaved = true;
        }

        CapturePersistedFeedbackSnapshot();
        RefreshControlUnsavedIndicators();

        if (skipped > 0 && saved == 0 && !layoutSaved)
        {
            StatusText = "Could not save — discover hardware for those controls first (or open Edit layout → Learn).";
            return false;
        }

        StatusText = (saved, skipped, layoutSaved) switch
        {
            (0, _, true) => "Saved layout preset (LED feedback).",
            (_, > 0, true) => $"Saved {saved} assignment(s) + layout; skipped {skipped} without hardware.",
            (_, > 0, false) => $"Saved {saved} assignment(s); skipped {skipped} without hardware mapping.",
            (_, _, true) => $"Saved {saved} assignment(s) and layout preset.",
            _ => $"Saved {saved} assignment(s)."
        };
        _midi.ClearStagedControlFeedback(deviceName);
        _ = _midi.RefreshHardwareFeedbackAsync(force: true);
        return true;
    }

    public void DiscardBindingDrafts(bool reloadLayout = true)
    {
        _bindingDrafts.Clear();
        SetBindingDraftDirty(false);
        SetLayoutDraftDirty(false);
        if (!string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            _midi.ClearStagedControlFeedback(SelectedDeviceName);
        }

        if (reloadLayout)
        {
            ReloadLayoutForSelectedDevice();
            StatusText = "Discarded unsaved assignments / preset edits.";
            _ = _midi.RefreshHardwareFeedbackAsync(force: true);
        }
    }

    public bool ConfirmDiscardBindingDraftsIfNeeded(string message)
    {
        if (!_hasUnsavedBindingDrafts && !_hasUnsavedLayoutChanges)
        {
            return true;
        }

        var proceed = ConfirmDiscardUnsavedAssignments?.Invoke(message) ?? true;
        if (!proceed)
        {
            return false;
        }

        DiscardBindingDrafts(reloadLayout: false);
        return true;
    }

    public void UpdateBindingDetails(BlueprintControlVm control, string? channelIdOrDisplayName, MidiValueMode mode, MidiBindingAction action)
    {
        StageBindingAssignment(control, channelIdOrDisplayName, mode, action);
    }

    public Task ConfirmAndUpdateBindingAsync(
        BlueprintControlVm control,
        string? channelIdOrDisplayName,
        MidiValueMode mode,
        MidiBindingAction action)
    {
        StageBindingAssignment(control, channelIdOrDisplayName, mode, action);
        return Task.CompletedTask;
    }

    /// <summary>Removes hardware discovery and Sonar assignment for this blueprint slot (persisted immediately).</summary>
    public void ClearBinding(BlueprintControlVm control)
    {
        if (control.IsPlaceholder)
        {
            return;
        }

        _bindingDrafts.Remove(control.Id);
        SetBindingDraftDirty(_bindingDrafts.Count > 0);

        _midi.MappingStore.RemoveMatching(b =>
            string.Equals(b.ControlId, control.Id, StringComparison.OrdinalIgnoreCase)
            || (control.Controller is int cc
                && b.Controller == cc
                && b.IsNote == control.IsNote
                && b.IsPitchBend == control.IsPitchBend
                && (string.IsNullOrWhiteSpace(SelectedDeviceName)
                    || DevicesShareProduct(b.DeviceName, SelectedDeviceName))));

        ResetControlVm(control);
        RefreshControlUnsavedIndicators(control.Id);
        StatusText = $"Reset {control.Label}: recognition and channel cleared.";
    }

    /// <summary>
    /// Wipes hardware mappings and Sonar routes for the selected device (incl. sibling ports),
    /// then re-applies the factory preset map for that device.
    /// </summary>
    public int ClearBindingsForSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a device first.";
            return 0;
        }

        CancelLearn();
        var deviceName = SelectedDeviceName;
        _bindingDrafts.Clear();
        SetBindingDraftDirty(false);
        var removed = _midi.MappingStore.RemoveMatching(b =>
            DevicesShareProduct(b.DeviceName, deviceName));
        _ccProbes.Clear();

        foreach (var control in Controls.Where(c => !c.IsPlaceholder))
        {
            ResetControlVm(control);
        }

        var seeded = SeedFactoryBindingsForDevices([deviceName]);
        ReloadLayoutForSelectedDevice();

        if (removed == 0 && seeded == 0)
        {
            StatusText = $"No bindings to clear on {deviceName}.";
        }
        else if (seeded > 0)
        {
            StatusText =
                $"Cleared {removed} binding(s) on {deviceName}, restored {seeded} factory mapping(s). Assign Sonar channels when ready.";
        }
        else
        {
            StatusText = $"Cleared {removed} binding(s) on {deviceName}. Move controls to discover them again.";
        }

        return removed;
    }

    public void EnterLayoutConstructor()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a device first.";
            return;
        }

        if (!ConfirmDiscardBindingDraftsIfNeeded("Opening the layout constructor will discard unsaved channel assignments."))
        {
            return;
        }

        CancelLearn();
        _draftLayout = PresetCatalog.CloneLayout(_layout);
        EnsureUniqueDraftCells(_draftLayout);

        _controlIdsAtConstructorEnter = _draftLayout.Controls
            .Select(c => c.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DraftControlLabel = string.Empty;
        DraftRowSpan = 1;
        DraftColSpan = 1;
        DraftHideBorder = false;
        DraftKeepSpacing = false;
        DraftContentJustify = MidiContentJustify.Pack;
        DraftContentAlign = MidiContentJustify.Pack;
        SetDraftHideBorderEnabled(false);
        IsLayoutConstructorMode = true;
        RebuildConstructorView();
        StatusText =
            "Layout constructor — drag areas/controls anywhere; Learn maps hardware here.";
    }

    public void CancelLayoutConstructor(bool silent = false)
    {
        if (!IsLayoutConstructorMode)
        {
            return;
        }

        _draftLayout = null;
        _controlIdsAtConstructorEnter = null;
        IsLayoutConstructorMode = false;
        ReloadLayoutForSelectedDevice();
        if (!silent)
        {
            StatusText = "Layout edits discarded.";
        }
    }

    public bool SaveLayoutConstructor()
    {
        if (!IsLayoutConstructorMode || _draftLayout is null || string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            return false;
        }

        var deviceName = SelectedDeviceName;
        var core = MidiDevicePortNaming.CoreProductName(deviceName);
        _draftLayout.DeviceMatch =
        [
            deviceName,
            ..(string.Equals(core, deviceName, StringComparison.OrdinalIgnoreCase) ? Array.Empty<string>() : [core])
        ];

        if (string.IsNullOrWhiteSpace(_draftLayout.Name) || _draftLayout.Name == "Generic Custom Grid")
        {
            _draftLayout.Name = $"{core} (custom)";
        }

        try
        {
            var activeKey = _presets.GetActivePresetKey(deviceName);
            var targetFile = MidiPresetSelectionStore.UserFileNameFromKey(activeKey);
            string path;
            if (!string.IsNullOrWhiteSpace(targetFile))
            {
                path = _presets.SaveUserLayout(_draftLayout, targetFileName: targetFile, createNewFile: false);
            }
            else
            {
                // Editing official / generic → create a new user preset.
                path = _presets.SaveUserLayout(_draftLayout, createNewFile: true);
                _presets.SetActivePresetKey(deviceName, MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));
            }
        }
        catch
        {
            StatusText = "Failed to save user layout.";
            return false;
        }

        CleanupOrphanBindingsAfterLayoutSave(deviceName, _draftLayout);
        _layout = PresetCatalog.CloneLayout(_draftLayout);
        _draftLayout = null;
        _controlIdsAtConstructorEnter = null;
        IsLayoutConstructorMode = false;
        OnPropertyChanged(nameof(HasUserLayoutOverride));
        ReloadLayoutForSelectedDevice();
        StatusText = $"Saved custom layout for {deviceName}.";
        _midi.ClearStagedControlFeedback(deviceName);
        _ = _midi.RefreshHardwareFeedbackAsync(force: true);
        return true;
    }

    /// <summary>Switch to the official (or built-in) preset without deleting user presets.</summary>
    public bool UseOfficialLayoutPreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a device first.";
            return false;
        }

        if (IsLayoutConstructorMode)
        {
            StatusText = "Finish or cancel layout editing first.";
            return false;
        }

        CancelLearn();
        _presets.SetActivePresetKey(SelectedDeviceName, MidiPresetSelectionStore.OfficialKey);
        ReloadLayoutForSelectedDevice();
        StatusText = "Using official / built-in layout preset.";
        return true;
    }

    /// <summary>Deletes the currently selected user preset file.</summary>
    public bool DeleteSelectedUserPreset()
    {
        if (SelectedLayoutPreset is not { IsUser: true, FileName: { } fileName }
            || string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a user layout preset to delete.";
            return false;
        }

        if (IsLayoutConstructorMode)
        {
            StatusText = "Finish or cancel layout editing first.";
            return false;
        }

        CancelLearn();
        if (!_presets.DeleteUserPresetFile(fileName))
        {
            StatusText = $"Could not delete preset “{fileName}”.";
            return false;
        }

        _presets.SetActivePresetKey(SelectedDeviceName, MidiPresetSelectionStore.OfficialKey);
        ReloadLayoutForSelectedDevice();
        StatusText = $"Deleted user preset “{fileName}”. Switched to official layout.";
        return true;
    }

    [Obsolete("Use UseOfficialLayoutPreset / DeleteSelectedUserPreset for multi-preset support.")]
    public bool RestoreFactoryLayout()
    {
        return UseOfficialLayoutPreset();
    }

    /// <summary>
    /// Saves the current layout (constructor draft or active) as a new named user preset and selects it.
    /// </summary>
    public bool TrySaveLayoutPresetAs(string presetName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            error = "Select a MIDI device first.";
            return false;
        }

        var name = presetName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Enter a preset name.";
            return false;
        }

        var deviceName = SelectedDeviceName;
        var layout = IsLayoutConstructorMode && _draftLayout is not null
            ? PresetCatalog.CloneLayout(_draftLayout)
            : PresetCatalog.CloneLayout(_layout);

        layout.Name = name;
        EnsureDeviceMatchIncludes(layout, deviceName);

        try
        {
            var path = _presets.SaveUserLayout(layout, createNewFile: true);
            _presets.SetActivePresetKey(deviceName, MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));

            if (IsLayoutConstructorMode && _draftLayout is not null)
            {
                CleanupOrphanBindingsAfterLayoutSave(deviceName, _draftLayout);
                _layout = PresetCatalog.CloneLayout(_draftLayout);
                _draftLayout = null;
                _controlIdsAtConstructorEnter = null;
                IsLayoutConstructorMode = false;
            }

            OnPropertyChanged(nameof(HasUserLayoutOverride));
            ReloadLayoutForSelectedDevice();
            StatusText = $"Saved preset “{name}”.";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            StatusText = "Failed to save named preset.";
            return false;
        }
    }

    /// <summary>
    /// Renames the active user preset in place (updates <c>name</c> in the same JSON file).
    /// Official / built-in presets cannot be renamed — use Save as… first.
    /// </summary>
    public bool TryRenameCurrentLayoutPreset(string presetName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            error = "Select a MIDI device first.";
            return false;
        }

        if (SelectedLayoutPreset is not { IsUser: true, FileName: { } fileName })
        {
            error = "Select a user layout preset to rename. Official presets are read-only — use Save as… first.";
            return false;
        }

        var name = presetName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Enter a preset name.";
            return false;
        }

        if (string.Equals(LayoutName, name, StringComparison.Ordinal))
        {
            StatusText = "Preset name unchanged.";
            return true;
        }

        var deviceName = SelectedDeviceName;
        var layout = IsLayoutConstructorMode && _draftLayout is not null
            ? PresetCatalog.CloneLayout(_draftLayout)
            : PresetCatalog.CloneLayout(_layout);

        layout.Name = name;
        EnsureDeviceMatchIncludes(layout, deviceName);

        try
        {
            var path = _presets.SaveUserLayout(layout, targetFileName: fileName, createNewFile: false);
            var key = MidiPresetSelectionStore.UserKey(Path.GetFileName(path));
            _presets.SetActivePresetKey(deviceName, key);

            if (IsLayoutConstructorMode && _draftLayout is not null)
            {
                _draftLayout.Name = name;
            }

            _layout.Name = name;
            OnPropertyChanged(nameof(LayoutName));
            RefreshLayoutPresets(key);
            StatusText = $"Renamed preset to “{name}”.";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            StatusText = "Failed to rename preset.";
            return false;
        }
    }

    /// <summary>Current layout JSON (constructor draft if editing, otherwise resolved layout).</summary>
    public string ExportLayoutJson()
    {
        var layout = IsLayoutConstructorMode && _draftLayout is not null
            ? PresetCatalog.CloneLayout(_draftLayout)
            : PresetCatalog.CloneLayout(_layout);
        return MidiLayoutJson.Serialize(layout);
    }

    /// <summary>
    /// Suggested file stem for SaveFileDialog (layout name or device).
    /// </summary>
    public string GetExportFileStem()
    {
        var layout = IsLayoutConstructorMode && _draftLayout is not null ? _draftLayout : _layout;
        var seed = layout.DeviceMatch.FirstOrDefault()
                   ?? SelectedDeviceName
                   ?? layout.Name
                   ?? "midi-layout";
        var safe = System.Text.RegularExpressions.Regex.Replace(seed.Trim(), @"[^\w\-]+", "-");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"-+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "midi-layout" : safe.ToLowerInvariant();
    }

    /// <summary>
    /// Imports a layout JSON. In constructor mode replaces the draft; otherwise saves as a new user preset.
    /// </summary>
    public bool TryImportLayoutJson(string json, out string error)
    {
        error = string.Empty;
        if (!MidiLayoutJson.TryParse(json, out var parsed, out error) || parsed is null)
        {
            return false;
        }

        var layout = PresetCatalog.CloneLayout(parsed);

        if (IsLayoutConstructorMode)
        {
            if (_draftLayout is null)
            {
                error = "Layout constructor is not ready.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(SelectedDeviceName))
            {
                EnsureDeviceMatchIncludes(layout, SelectedDeviceName);
            }

            _draftLayout = layout;
            RebuildConstructorView();
            StatusText = $"Imported layout “{layout.Name}” into the editor (not saved yet).";
            return true;
        }

        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            error = "Select a MIDI device before importing a layout.";
            return false;
        }

        var deviceName = SelectedDeviceName;
        EnsureDeviceMatchIncludes(layout, deviceName);
        if (string.IsNullOrWhiteSpace(layout.Name) || layout.Name == "Custom")
        {
            layout.Name = $"{MidiDevicePortNaming.CoreProductName(deviceName)} (imported)";
        }

        try
        {
            var path = _presets.SaveUserLayout(layout, createNewFile: true);
            _presets.SetActivePresetKey(deviceName, MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            error = $"Failed to save imported layout: {ex.Message}";
            return false;
        }

        CancelLearn();
        OnPropertyChanged(nameof(HasUserLayoutOverride));
        ReloadLayoutForSelectedDevice();
        StatusText = $"Imported layout “{layout.Name}” as a new user preset.";
        return true;
    }

    private static void EnsureDeviceMatchIncludes(MidiDeviceLayout layout, string deviceName)
    {
        layout.DeviceMatch ??= [];
        var core = MidiDevicePortNaming.CoreProductName(deviceName);
        if (!layout.DeviceMatch.Any(m => string.Equals(m, deviceName, StringComparison.OrdinalIgnoreCase)))
        {
            layout.DeviceMatch.Insert(0, deviceName);
        }

        if (!string.Equals(core, deviceName, StringComparison.OrdinalIgnoreCase)
            && !layout.DeviceMatch.Any(m => string.Equals(m, core, StringComparison.OrdinalIgnoreCase)))
        {
            layout.DeviceMatch.Add(core);
        }
    }

    public bool PlacePaletteControlAt(int row, int col, MidiControlType? type = null)
    {
        if (_draftLayout is null || !IsLayoutConstructorMode)
        {
            return false;
        }

        return ConstructorDrop(
            $"palette:{type ?? PaletteSelectedType}",
            targetRegionId: null,
            targetControlId: null,
            MidiLayoutDropZone.Inside);
    }

    /// <summary>
    /// Constructor DnD entry: payload palette:Type|palette:Area|control:id|region:id.
    /// Prefer <see cref="ConstructorDropToSlot"/> for insert-slot placement.
    /// </summary>
    public bool ConstructorDrop(
        string payload,
        string? targetRegionId,
        string? targetControlId,
        MidiLayoutDropZone zone)
    {
        ClearDropSlotPreview();
        if (_draftLayout is null || !IsLayoutConstructorMode || string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        if (payload.StartsWith("palette:Area", StringComparison.OrdinalIgnoreCase)
            || payload.Equals("palette:Region", StringComparison.OrdinalIgnoreCase))
        {
            var ok = MidiLayoutTreeOps.PlaceNewRegion(
                _draftLayout,
                targetRegionId,
                zone,
                DraftControlLabel,
                out var regionId);
            if (ok)
            {
                var region = _draftLayout.Regions.First(r =>
                    string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
                region.RowSpan = DraftRowSpan;
                region.ColSpan = DraftColSpan;
                region.HideBorder = DraftHideBorder;
                region.KeepSpacing = DraftKeepSpacing;
                region.ContentJustify = DraftContentJustify;
                region.ContentAlign = DraftContentAlign;
                RebuildConstructorView();
                StatusText = $"Added area {regionId} ({zone}).";
            }

            return ok;
        }

        if (payload.StartsWith("palette:", StringComparison.OrdinalIgnoreCase))
        {
            var typeName = payload["palette:".Length..];
            if (!Enum.TryParse<MidiControlType>(typeName, out var type))
            {
                return false;
            }

            var control = CreatePaletteControl(type);
            var ok = MidiLayoutTreeOps.PlaceNewControl(
                _draftLayout,
                control,
                targetRegionId,
                targetControlId,
                zone);
            if (ok)
            {
                RebuildConstructorView();
                StatusText = $"Added {control.Label} ({zone}).";
            }

            return ok;
        }

        if (payload.StartsWith("control:", StringComparison.OrdinalIgnoreCase))
        {
            var id = payload["control:".Length..];
            var ok = MidiLayoutTreeOps.MoveControl(_draftLayout, id, targetRegionId, targetControlId, zone);
            if (ok)
            {
                RebuildConstructorView();
                StatusText = $"Moved control ({zone}).";
            }

            return ok;
        }

        if (payload.StartsWith("region:", StringComparison.OrdinalIgnoreCase))
        {
            var id = payload["region:".Length..];
            var ok = MidiLayoutTreeOps.MoveRegion(_draftLayout, id, targetRegionId, zone);
            if (ok)
            {
                RebuildConstructorView();
                StatusText = $"Moved area ({zone}).";
            }

            return ok;
        }

        return false;
    }

    /// <summary>Place/move payload into an explicit insert slot (with sibling shift).</summary>
    public bool ConstructorDropToSlot(string payload, MidiDropSlot slot)
    {
        ClearDropSlotPreview();
        if (_draftLayout is null || !IsLayoutConstructorMode || string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        if (payload.StartsWith("palette:Area", StringComparison.OrdinalIgnoreCase)
            || payload.Equals("palette:Region", StringComparison.OrdinalIgnoreCase))
        {
            var regionId = GeneratePreviewRegionId(_draftLayout);
            var region = new MidiLayoutRegion
            {
                Id = regionId,
                Label = string.IsNullOrWhiteSpace(DraftControlLabel) ? string.Empty : DraftControlLabel.Trim(),
                RowSpan = DraftRowSpan,
                ColSpan = DraftColSpan,
                HideBorder = DraftHideBorder,
                KeepSpacing = DraftKeepSpacing,
                ContentJustify = DraftContentJustify,
                ContentAlign = DraftContentAlign
            };
            var ok = MidiLayoutTreeOps.InsertRegion(_draftLayout, region, slot with
            {
                RowSpan = region.RowSpan,
                ColSpan = region.ColSpan
            });
            if (ok)
            {
                RebuildConstructorView();
                StatusText = $"Added area {region.Id} at ({slot.Row},{slot.Col}).";
            }

            return ok;
        }

        if (payload.StartsWith("palette:", StringComparison.OrdinalIgnoreCase))
        {
            var typeName = payload["palette:".Length..];
            if (!Enum.TryParse<MidiControlType>(typeName, out var type))
            {
                return false;
            }

            var control = CreatePaletteControl(type);
            var ok = MidiLayoutTreeOps.InsertControl(_draftLayout, control, slot with
            {
                RowSpan = control.RowSpan,
                ColSpan = control.ColSpan
            });
            if (ok)
            {
                RebuildConstructorView();
                StatusText = $"Added {control.Label} at ({slot.Row},{slot.Col}).";
            }

            return ok;
        }

        if (payload.StartsWith("control:", StringComparison.OrdinalIgnoreCase))
        {
            var id = payload["control:".Length..];
            var ok = MidiLayoutTreeOps.MoveControlToSlot(_draftLayout, id, slot);
            if (ok)
            {
                RebuildConstructorView();
                StatusText = $"Moved control to ({slot.Row},{slot.Col}).";
            }

            return ok;
        }

        if (payload.StartsWith("region:", StringComparison.OrdinalIgnoreCase))
        {
            var id = payload["region:".Length..];
            var ok = MidiLayoutTreeOps.MoveRegionToSlot(_draftLayout, id, slot);
            if (ok)
            {
                RebuildConstructorView();
                StatusText = $"Moved area to ({slot.Row},{slot.Col}).";
            }

            return ok;
        }

        return false;
    }

    private static string GeneratePreviewRegionId(MidiDeviceLayout layout)
    {
        var existing = layout.Regions.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < 1000; i++)
        {
            var id = $"area_{i}";
            if (!existing.Contains(id))
            {
                return id;
            }
        }

        return $"area_{Guid.NewGuid():N}"[..12];
    }

    public bool DeleteDraftControl(string controlId)
    {
        if (_draftLayout is null || !IsLayoutConstructorMode || string.IsNullOrWhiteSpace(controlId))
        {
            return false;
        }

        var removed = _draftLayout.Controls.RemoveAll(c =>
            string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return false;
        }

        RebuildConstructorView();
        StatusText = "Removed control from layout (bindings cleaned on Save).";
        return true;
    }

    public bool DeleteDraftRegion(string regionId, bool deleteContents = false)
    {
        if (_draftLayout is null || !IsLayoutConstructorMode)
        {
            return false;
        }

        if (!MidiLayoutTreeOps.DeleteRegion(_draftLayout, regionId, deleteContents))
        {
            return false;
        }

        RebuildConstructorView();
        StatusText = deleteContents
            ? "Removed area and its contents."
            : "Removed area (children moved to parent).";
        return true;
    }

    /// <summary>
    /// Applies label + spans (+ optional area chrome flags) for the current constructor selection.
    /// </summary>
    public bool ApplyDraftSelection(
        string? controlId,
        string? regionId,
        string label,
        int rowSpan,
        int colSpan,
        bool? hideBorder = null,
        bool? keepSpacing = null,
        MidiContentJustify? contentJustify = null,
        MidiContentJustify? contentAlign = null)
    {
        if (_draftLayout is null || !IsLayoutConstructorMode)
        {
            return false;
        }

        rowSpan = Math.Clamp(rowSpan, 1, 16);
        colSpan = Math.Clamp(colSpan, 1, 16);
        var trimmed = label?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(controlId))
        {
            var control = _draftLayout.Controls.FirstOrDefault(c =>
                string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));
            if (control is null)
            {
                return false;
            }

            control.Label = trimmed;
            control.RowSpan = rowSpan;
            control.ColSpan = colSpan;
            RebuildConstructorView();
            StatusText = $"Updated {control.Id}: span {rowSpan}×{colSpan}.";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(regionId))
        {
            var region = _draftLayout.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
            if (region is null)
            {
                return false;
            }

            region.Label = trimmed;
            region.RowSpan = rowSpan;
            region.ColSpan = colSpan;
            if (hideBorder is bool hide)
            {
                region.HideBorder = hide;
            }

            if (keepSpacing is bool keep)
            {
                region.KeepSpacing = keep;
            }

            if (contentJustify is MidiContentJustify justify)
            {
                region.ContentJustify = justify;
            }

            if (contentAlign is MidiContentJustify align)
            {
                region.ContentAlign = align;
            }

            RebuildConstructorView();
            StatusText =
                $"Updated area {region.Id}: span {rowSpan}×{colSpan}, H={region.ContentJustify}, V={region.ContentAlign}.";
            return true;
        }

        return false;
    }

    public bool RenameDraftControl(string controlId, string label) =>
        ApplyDraftSelection(controlId, null, label, DraftRowSpan, DraftColSpan);

    public bool RenameDraftRegion(string regionId, string label) =>
        ApplyDraftSelection(
            null,
            regionId,
            label,
            DraftRowSpan,
            DraftColSpan,
            DraftHideBorder,
            DraftKeepSpacing,
            DraftContentJustify,
            DraftContentAlign);

    /// <summary>
    /// Sets row/col span for a selected control or area. Spans are 1..16 (how many grid cells the item occupies).
    /// </summary>
    public bool SetDraftSpans(string? controlId, string? regionId, int rowSpan, int colSpan)
    {
        var label = !string.IsNullOrWhiteSpace(controlId)
            ? _draftLayout?.Controls.FirstOrDefault(c =>
                  string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase))?.Label ?? DraftControlLabel
            : _draftLayout?.Regions.FirstOrDefault(r =>
                  string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase))?.Label ?? DraftControlLabel;
        var isRegion = !string.IsNullOrWhiteSpace(regionId);
        return ApplyDraftSelection(
            controlId,
            regionId,
            label ?? string.Empty,
            rowSpan,
            colSpan,
            isRegion ? DraftHideBorder : null,
            isRegion ? DraftKeepSpacing : null,
            isRegion ? DraftContentJustify : null,
            isRegion ? DraftContentAlign : null);
    }

    /// <summary>Reads LED feedback UI tag from the active layout (draft or saved).</summary>
    public string GetControlFeedbackTag(string controlId)
    {
        var control = FindLayoutControl(controlId);
        var spec = control?.Feedback;
        if (spec is null || spec.Source == MidiFeedbackSource.None)
        {
            return MidiFeedbackUi.TagOff;
        }

        return MidiFeedbackUi.ToTag(spec.Source, spec.Style);
    }

    /// <summary>
    /// Stages LED feedback on the layout (constructor draft or normal in-memory layout).
    /// Does not write disk and does not drive hardware — call Save to apply.
    /// </summary>
    public bool SetControlFeedbackFromTag(string controlId, string tag)
    {
        if (!MidiFeedbackUi.TryParseTag(tag, out var source, out var style))
        {
            StatusText = "Unknown LED feedback option.";
            return false;
        }

        return SetControlFeedback(controlId, source, style);
    }

    public bool SetControlFeedbackSourceFromTag(string controlId, string sourceTag)
    {
        if (!MidiFeedbackUi.TryParseSourceTag(sourceTag, out var source))
        {
            StatusText = "Unknown LED feedback source.";
            return false;
        }

        return SetControlFeedback(controlId, source, _draftFeedbackStyle);
    }

    public bool SetControlFeedbackStyleFromTag(string controlId, string styleTag)
    {
        if (!MidiFeedbackUi.TryParseStyleTag(styleTag, out var style))
        {
            StatusText = "Unknown LED feedback style.";
            return false;
        }

        return SetControlFeedback(controlId, _draftFeedbackSource, style);
    }

    public bool SetControlFeedback(string controlId, MidiFeedbackSource source, MidiFeedbackStyle style)
    {
        var layout = _draftLayout ?? _layout;
        var control = layout.Controls.FirstOrDefault(c =>
            string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));
        if (control is null)
        {
            StatusText = "Select a control first.";
            return false;
        }

        source = NormalizeFeedbackSource(control, source);
        style = MidiFeedbackUi.NormalizeStyle(control, source, style);
        var nextTag = MidiFeedbackUi.ToTag(source, style);
        if (string.Equals(GetControlFeedbackTag(controlId), nextTag, StringComparison.OrdinalIgnoreCase))
        {
            RememberDraftFeedback(control, source, style);
            return true;
        }

        ApplyFeedbackToLayoutControl(control, source, style);
        RememberDraftFeedback(control, source, style);

        if (IsLayoutConstructorMode)
        {
            StatusText = source == MidiFeedbackSource.None
                ? $"LED feedback off for {control.Label} (save layout to keep)."
                : $"LED feedback → {DescribeFeedback(source, style)} for {control.Label} (save layout to keep).";
            return true;
        }

        SyncLayoutFeedbackDirtyFlag();
        RefreshControlUnsavedIndicators(controlId);
        StatusText = IsFeedbackDrafted(controlId)
            ? $"Staged LED feedback → {DescribeFeedback(source, style)} for {control.Label} — Save changes to apply."
            : source == MidiFeedbackSource.None
                ? $"LED feedback off for {control.Label} (matches saved preset)."
                : $"LED feedback → {DescribeFeedback(source, style)} for {control.Label} (matches saved preset).";
        return true;
    }

    private void RememberDraftFeedback(
        MidiLayoutControl control,
        MidiFeedbackSource source,
        MidiFeedbackStyle style)
    {
        _draftFeedbackCanChooseStyle = MidiFeedbackUi.AllowsMuteSource(control);
        DraftFeedbackSource = source;
        DraftFeedbackStyle = style;
        DraftFeedbackEnabled = true;
        OnPropertyChanged(nameof(DraftFeedbackTag));
        OnPropertyChanged(nameof(DraftFeedbackSourceTag));
        OnPropertyChanged(nameof(DraftFeedbackStyleTag));
        OnPropertyChanged(nameof(DraftFeedbackStyleEnabled));
    }

    private static string DescribeFeedback(MidiFeedbackSource source, MidiFeedbackStyle style) =>
        source == MidiFeedbackSource.None
            ? "Off"
            : style == MidiFeedbackStyle.Blink
                ? $"{source} (blink)"
                : $"{source} (solid)";

    /// <summary>Faders only support Off / Channel assigned (soft takeover).</summary>
    private static MidiFeedbackSource NormalizeFeedbackSource(
        MidiLayoutControl control,
        MidiFeedbackSource source)
    {
        if (source == MidiFeedbackSource.None || MidiFeedbackUi.AllowsMuteSource(control))
        {
            return source;
        }

        return source == MidiFeedbackSource.Mute
            ? MidiFeedbackSource.ChannelAssigned
            : source;
    }

    [Obsolete("Use SetControlFeedbackFromTag.")]
    public bool SetControlFeedbackSource(string controlId, MidiFeedbackSource source) =>
        SetControlFeedback(controlId, source, MidiFeedbackStyle.Solid);

    private static void ApplyFeedbackToLayoutControl(
        MidiLayoutControl control,
        MidiFeedbackSource source,
        MidiFeedbackStyle style)
    {
        if (source == MidiFeedbackSource.None)
        {
            control.Feedback = null;
            return;
        }

        var previous = control.Feedback;
        control.Feedback = new MidiControlFeedbackSpec
        {
            Source = source,
            Style = MidiFeedbackUi.NormalizeStyle(control, source, style),
            On = previous?.On,
            Off = previous?.Off
        };
    }

    private MidiLayoutControl? FindLayoutControl(string controlId)
    {
        var layout = _draftLayout ?? _layout;
        return layout.Controls.FirstOrDefault(c =>
            string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes the current (non-draft) layout to the active user preset file.</summary>
    private bool PersistActiveLayoutToUserPreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a device first.";
            return false;
        }

        var deviceName = SelectedDeviceName;
        var layout = PresetCatalog.CloneLayout(_layout);
        EnsureDeviceMatchIncludes(layout, deviceName);

        try
        {
            var activeKey = _presets.GetActivePresetKey(deviceName);
            var targetFile = MidiPresetSelectionStore.UserFileNameFromKey(activeKey);
            string path;
            if (!string.IsNullOrWhiteSpace(targetFile))
            {
                path = _presets.SaveUserLayout(layout, targetFileName: targetFile, createNewFile: false);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(layout.Name) || layout.Name == "Generic Custom Grid")
                {
                    layout.Name = $"{MidiDevicePortNaming.CoreProductName(deviceName)} (custom)";
                }

                path = _presets.SaveUserLayout(layout, createNewFile: true);
                _presets.SetActivePresetKey(deviceName, MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));
            }

            _layout = PresetCatalog.CloneLayout(layout);
            OnPropertyChanged(nameof(HasUserLayoutOverride));
            OnPropertyChanged(nameof(LayoutName));
            RefreshLayoutPresets(MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));
            CapturePersistedFeedbackSnapshot();
            return true;
        }
        catch
        {
            StatusText = "Failed to save layout feedback.";
            return false;
        }
    }

    private void CapturePersistedFeedbackSnapshot()
    {
        _persistedFeedbackTags.Clear();
        foreach (var control in _layout.Controls)
        {
            _persistedFeedbackTags[control.Id] = FeedbackTagFromSpec(control.Feedback);
        }
    }

    private static string FeedbackTagFromSpec(MidiControlFeedbackSpec? spec) =>
        spec is null || spec.Source == MidiFeedbackSource.None
            ? MidiFeedbackUi.TagOff
            : MidiFeedbackUi.ToTag(spec.Source, spec.Style);

    private string GetPersistedFeedbackTag(string controlId) =>
        _persistedFeedbackTags.TryGetValue(controlId, out var tag)
            ? tag
            : MidiFeedbackUi.TagOff;

    private bool IsFeedbackDrafted(string controlId) =>
        !string.Equals(
            GetControlFeedbackTag(controlId),
            GetPersistedFeedbackTag(controlId),
            StringComparison.OrdinalIgnoreCase);

    private void SyncLayoutFeedbackDirtyFlag()
    {
        var any = _layout.Controls.Any(c => IsFeedbackDrafted(c.Id));
        SetLayoutDraftDirty(any);
    }

    private void RefreshControlUnsavedIndicators(string? onlyControlId = null)
    {
        foreach (var vm in Controls)
        {
            if (onlyControlId is not null
                && !string.Equals(vm.Id, onlyControlId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ApplyUnsavedFieldFlags(vm);
        }

        NotifyInspectorDirtyFlags();
    }

    private void ApplyUnsavedFieldFlags(BlueprintControlVm vm)
    {
        var persisted = !string.IsNullOrWhiteSpace(SelectedDeviceName)
            ? FindBindingForControl(SelectedDeviceName, vm.Id)
            : null;

        string baseChannel;
        MidiValueMode baseMode;
        MidiBindingAction baseAction;
        if (persisted is not null)
        {
            baseChannel = NormalizeChannelCompare(persisted.ChannelId);
            baseMode = persisted.Mode;
            baseAction = MidiBindingActions.Normalize(vm.Type, persisted.Action);
        }
        else
        {
            baseChannel = string.Empty;
            baseMode = MidiValueMode.Absolute;
            baseAction = MidiBindingAction.None;
        }

        if (_bindingDrafts.TryGetValue(vm.Id, out var draft))
        {
            vm.HasUnsavedChannel = !string.Equals(
                NormalizeChannelCompare(draft.ChannelId),
                baseChannel,
                StringComparison.OrdinalIgnoreCase);
            vm.HasUnsavedMode = draft.Mode != baseMode;
            vm.HasUnsavedAction = MidiBindingActions.Normalize(vm.Type, draft.Action) != baseAction;
        }
        else
        {
            vm.HasUnsavedChannel = false;
            vm.HasUnsavedMode = false;
            vm.HasUnsavedAction = false;
        }

        MidiFeedbackUi.TryParseTag(GetControlFeedbackTag(vm.Id), out var curSource, out var curStyle);
        MidiFeedbackUi.TryParseTag(GetPersistedFeedbackTag(vm.Id), out var baseSource, out var baseStyle);

        vm.HasUnsavedFeedbackSource = curSource != baseSource;
        vm.HasUnsavedFeedbackStyle = curStyle != baseStyle;
        vm.HasUnsavedChanges = vm.HasUnsavedChannel
                               || vm.HasUnsavedMode
                               || vm.HasUnsavedAction
                               || vm.HasUnsavedFeedbackSource
                               || vm.HasUnsavedFeedbackStyle;
    }

    private void SetLayoutDraftDirty(bool dirty)
    {
        if (_hasUnsavedLayoutChanges == dirty)
        {
            OnPropertyChanged(nameof(HasUnsavedBindingDrafts));
            OnPropertyChanged(nameof(CanSaveBindingDrafts));
            return;
        }

        _hasUnsavedLayoutChanges = dirty;
        OnPropertyChanged(nameof(HasUnsavedBindingDrafts));
        OnPropertyChanged(nameof(CanSaveBindingDrafts));
    }

    public void SyncDraftSpansFromControl(BlueprintControlVm control)
    {
        DraftRowSpan = Math.Max(1, control.RowSpan);
        DraftColSpan = Math.Max(1, control.ColSpan);
        DraftHideBorder = false;
        DraftKeepSpacing = false;
        DraftContentJustify = MidiContentJustify.Pack;
        DraftContentAlign = MidiContentJustify.Pack;
        SetDraftHideBorderEnabled(false);
        var layoutControl = FindLayoutControl(control.Id);
        var tag = GetControlFeedbackTag(control.Id);
        MidiFeedbackUi.TryParseTag(tag, out var source, out var style);
        if (layoutControl is not null)
        {
            source = NormalizeFeedbackSource(layoutControl, source);
            style = MidiFeedbackUi.NormalizeStyle(layoutControl, source, style);
            _draftFeedbackCanChooseStyle = MidiFeedbackUi.AllowsMuteSource(layoutControl);
        }
        else
        {
            _draftFeedbackCanChooseStyle = control.Type != MidiControlType.Fader;
        }

        _draftFeedbackSource = source;
        _draftFeedbackStyle = style;
        OnPropertyChanged(nameof(DraftFeedbackSource));
        OnPropertyChanged(nameof(DraftFeedbackStyle));
        OnPropertyChanged(nameof(DraftFeedbackTag));
        OnPropertyChanged(nameof(DraftFeedbackSourceTag));
        OnPropertyChanged(nameof(DraftFeedbackStyleTag));
        DraftFeedbackEnabled = !control.IsPlaceholder;
        OnPropertyChanged(nameof(DraftFeedbackStyleEnabled));
    }

    public void SyncDraftSpansFromRegion(string regionId)
    {
        var layout = _draftLayout ?? _layout;
        var region = layout.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            DraftRowSpan = 1;
            DraftColSpan = 1;
            DraftHideBorder = false;
            DraftKeepSpacing = false;
            DraftContentJustify = MidiContentJustify.Pack;
            DraftContentAlign = MidiContentJustify.Pack;
            SetDraftHideBorderEnabled(false);
            DraftFeedbackSource = MidiFeedbackSource.None;
            DraftFeedbackEnabled = false;
            return;
        }

        DraftRowSpan = Math.Max(1, region.RowSpan);
        DraftColSpan = Math.Max(1, region.ColSpan);
        DraftHideBorder = region.HideBorder;
        DraftKeepSpacing = region.KeepSpacing;
        DraftContentJustify = region.ContentJustify;
        DraftContentAlign = region.ContentAlign;
        SetDraftHideBorderEnabled(true);
        DraftFeedbackSource = MidiFeedbackSource.None;
        DraftFeedbackEnabled = false;
    }

    /// <summary>Clears Hide-border / Keep-spacing when nothing (or a control) is selected.</summary>
    public void DisableDraftHideBorderEditor()
    {
        DraftHideBorder = false;
        DraftKeepSpacing = false;
        DraftContentJustify = MidiContentJustify.Pack;
        DraftContentAlign = MidiContentJustify.Pack;
        SetDraftHideBorderEnabled(false);
        DraftFeedbackSource = MidiFeedbackSource.None;
        DraftFeedbackEnabled = false;
    }

    private void SetDraftHideBorderEnabled(bool enabled)
    {
        if (DraftHideBorderEnabled == enabled)
        {
            return;
        }

        DraftHideBorderEnabled = enabled;
        OnPropertyChanged(nameof(DraftHideBorderEnabled));
        OnPropertyChanged(nameof(DraftKeepSpacingEnabled));
        OnPropertyChanged(nameof(DraftContentJustifyEnabled));
    }

    private MidiLayoutControl CreatePaletteControl(MidiControlType controlType) => new()
    {
        Id = GenerateUniqueControlId(controlType),
        Type = controlType,
        Label = string.IsNullOrWhiteSpace(DraftControlLabel)
            ? DefaultLabelForType(controlType, 0, 0)
            : DraftControlLabel.Trim(),
        RowSpan = DraftRowSpan,
        ColSpan = DraftColSpan,
        DefaultMode = controlType switch
        {
            MidiControlType.Encoder => MidiValueMode.Relative,
            MidiControlType.Button => MidiValueMode.Absolute,
            _ => MidiValueMode.Absolute
        },
        DefaultAction = MidiBindingActions.DefaultFor(controlType),
        RelativeEncoding = controlType == MidiControlType.Encoder
            ? MidiRelativeEncoding.OffsetBinary
            : null
    };

    private void RebuildConstructorView()
    {
        if (_draftLayout is null)
        {
            return;
        }

        RebuildLayoutTree(_draftLayout);
        OnPropertyChanged(nameof(UseRegionTreeLayout));
        OnPropertyChanged(nameof(UseConstructorTree));
    }

    /// <summary>
    /// Nested areas + controls. Used for both normal preview and the constructor (same model).
    /// </summary>
    private void RebuildLayoutTree(MidiDeviceLayout layout)
    {
        MidiLayoutTreeOps.SyncRootGridExtent(layout);

        ConstructorRoots.Clear();
        Controls.Clear();

        var regionVms = new Dictionary<string, BlueprintRegionVm>(StringComparer.OrdinalIgnoreCase);
        var extentByParent = layout.Regions
            .GroupBy(r => r.ParentRegionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var cols = 0;
                    var rows = 0;
                    foreach (var r in g)
                    {
                        cols = Math.Max(cols, r.Col + Math.Max(1, r.ColSpan));
                        rows = Math.Max(rows, r.Row + Math.Max(1, r.RowSpan));
                    }

                    return (Cols: Math.Max(1, cols), Rows: Math.Max(1, rows));
                },
                StringComparer.OrdinalIgnoreCase);

        foreach (var region in layout.Regions)
        {
            var rowSpan = Math.Max(1, region.RowSpan);
            var colSpan = Math.Max(1, region.ColSpan);
            var parentKey = region.ParentRegionId ?? string.Empty;
            var extent = extentByParent.TryGetValue(parentKey, out var e) ? e : (Cols: 1, Rows: 1);
            regionVms[region.Id] = new BlueprintRegionVm
            {
                Id = region.Id,
                ParentRegionId = region.ParentRegionId,
                Label = region.Label,
                Row = region.Row,
                Col = region.Col,
                RowSpan = rowSpan,
                ColSpan = colSpan,
                HideBorder = region.HideBorder,
                KeepSpacing = region.KeepSpacing,
                ContentJustify = region.ContentJustify,
                ContentAlign = region.ContentAlign,
                FlushLeft = region.Col <= 0,
                FlushTop = region.Row <= 0,
                FlushRight = region.Col + colSpan >= extent.Cols,
                FlushBottom = region.Row + rowSpan >= extent.Rows,
                IsConstructorMode = IsLayoutConstructorMode
            };
        }

        foreach (var region in layout.Regions.Where(r => string.IsNullOrWhiteSpace(r.ParentRegionId)))
        {
            ConstructorRoots.Add(regionVms[region.Id]);
        }

        foreach (var region in layout.Regions
                     .Where(r => !string.IsNullOrWhiteSpace(r.ParentRegionId))
                     .OrderBy(r => r.Row)
                     .ThenBy(r => r.Col)
                     .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (region.ParentRegionId is not null
                && regionVms.TryGetValue(region.ParentRegionId, out var parent)
                && regionVms.TryGetValue(region.Id, out var child))
            {
                parent.Children.Add(child);
            }
            else if (regionVms.TryGetValue(region.Id, out var orphanRegion))
            {
                ConstructorRoots.Add(orphanRegion);
            }
        }

        foreach (var control in layout.Controls
                     .OrderBy(c => c.Row)
                     .ThenBy(c => c.Col)
                     .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            var vm = CreateControlVm(
                control,
                compact: control.Type == MidiControlType.Button,
                tallFader: control.Type == MidiControlType.Fader);
            Controls.Add(vm);
            if (!string.IsNullOrWhiteSpace(control.RegionId)
                && regionVms.TryGetValue(control.RegionId, out var parent))
            {
                parent.Children.Add(vm);
            }
            else
            {
                ConstructorRoots.Add(vm);
            }
        }

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(LayoutName));
        OnPropertyChanged(nameof(LayoutHint));
        SyncRegionSelection();
    }

    public void SetDropPreview(object? target, MidiLayoutDropZone? zone)
    {
        // Legacy edge hints on regions (Inside) — slot preview replaces control edge bands.
        if (zone is null || target is null)
        {
            return;
        }

        if (target is BlueprintRegionVm region && zone == MidiLayoutDropZone.Inside)
        {
            ClearDropPreviews();
            region.DropHint = nameof(MidiLayoutDropZone.Inside);
        }
    }

    public bool TryGetControlCell(string id, out int row, out int col, out int rowSpan, out int colSpan)
    {
        row = col = 0;
        rowSpan = colSpan = 1;
        var layout = _draftLayout ?? _layout;
        var control = layout.Controls.FirstOrDefault(c =>
            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (control is null)
        {
            return false;
        }

        row = control.Row;
        col = control.Col;
        rowSpan = Math.Max(1, control.RowSpan);
        colSpan = Math.Max(1, control.ColSpan);
        return true;
    }

    public bool TryGetRegionCell(string id, out int row, out int col, out int rowSpan, out int colSpan)
    {
        row = col = 0;
        rowSpan = colSpan = 1;
        var layout = _draftLayout ?? _layout;
        var region = layout.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return false;
        }

        row = region.Row;
        col = region.Col;
        rowSpan = Math.Max(1, region.RowSpan);
        colSpan = Math.Max(1, region.ColSpan);
        return true;
    }

    /// <summary>
    /// Show dashed insert slot under <paramref name="parentRegionId"/> (null = root),
    /// temporarily shifting sibling VMs so the slot occupies space.
    /// </summary>
    /// <param name="shiftRegions">True when inserting an area (shift region siblings); false for controls.</param>
    public void SetDropSlotPreview(
        MidiDropSlot slot,
        string? excludeControlOrRegionId = null,
        bool shiftRegions = false)
    {
        if (!IsLayoutConstructorMode)
        {
            return;
        }

        if (_activeDropSlot is { } current
            && current.ParentRegionId == slot.ParentRegionId
            && current.Row == slot.Row
            && current.Col == slot.Col
            && current.Axis == slot.Axis
            && current.RowSpan == slot.RowSpan
            && current.ColSpan == slot.ColSpan)
        {
            return;
        }

        ClearDropSlotPreview(restoreOnly: true);

        var parentChildren = ResolveParentChildren(slot.ParentRegionId);
        if (parentChildren is null)
        {
            return;
        }

        // Snapshot + shift siblings on the insert axis.
        foreach (var node in parentChildren.ToList())
        {
            if (node is not IBlueprintFormCell cell || cell.IsDropSlot)
            {
                continue;
            }

            var isRegion = cell is BlueprintRegionVm;
            if (shiftRegions != isRegion)
            {
                continue;
            }

            if (excludeControlOrRegionId is not null
                && string.Equals(cell.Id, excludeControlOrRegionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _dropPreviewBases.Add((cell, cell.Row, cell.Col));
            if (slot.Axis == MidiLayoutShiftAxis.Horizontal && cell.Col >= slot.Col)
            {
                cell.Col++;
            }
            else if (slot.Axis == MidiLayoutShiftAxis.Vertical && cell.Row >= slot.Row)
            {
                cell.Row++;
            }
        }

        _dropSlotVm = new BlueprintDropSlotVm
        {
            Row = slot.Row,
            Col = slot.Col,
            RowSpan = Math.Max(1, slot.RowSpan),
            ColSpan = Math.Max(1, slot.ColSpan)
        };
        parentChildren.Add(_dropSlotVm);
        _dropSlotParentChildren = parentChildren;
        _activeDropSlot = slot;
        BlueprintLayoutRefreshRequested?.Invoke();
    }

    public void ClearDropSlotPreview(bool restoreOnly = false)
    {
        foreach (var (node, row, col) in _dropPreviewBases)
        {
            if (node is IBlueprintFormCell cell)
            {
                cell.Row = row;
                cell.Col = col;
            }
        }

        _dropPreviewBases.Clear();

        if (_dropSlotVm is not null && _dropSlotParentChildren is not null)
        {
            _dropSlotParentChildren.Remove(_dropSlotVm);
        }

        _dropSlotVm = null;
        _dropSlotParentChildren = null;
        _activeDropSlot = null;

        if (!restoreOnly)
        {
            ClearDropPreviews();
        }

        BlueprintLayoutRefreshRequested?.Invoke();
    }

    private ObservableCollection<object>? ResolveParentChildren(string? parentRegionId)
    {
        if (string.IsNullOrWhiteSpace(parentRegionId))
        {
            return ConstructorRoots;
        }

        return FindRegionVm(ConstructorRoots, parentRegionId)?.Children;
    }

    private static BlueprintRegionVm? FindRegionVm(IEnumerable<object> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node is BlueprintRegionVm region)
            {
                if (string.Equals(region.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return region;
                }

                var nested = FindRegionVm(region.Children, id);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    public void ClearDropPreviews()
    {
        foreach (var control in Controls)
        {
            control.DropPreviewZone = null;
        }

        void ClearTree(IEnumerable<object> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is BlueprintRegionVm region)
                {
                    region.DropHint = string.Empty;
                    ClearTree(region.Children);
                }
                else if (node is BlueprintControlVm control)
                {
                    control.DropPreviewZone = null;
                }
            }
        }

        ClearTree(ConstructorRoots);
    }

    /// <summary>
    /// Ensure controls in the same parent don't share the same (row,col)
    /// (legacy generics / overlapping coordinates). Nested regions keep independent grids.
    /// </summary>
    private static void EnsureUniqueDraftCells(MidiDeviceLayout layout)
    {
        var ordered = layout.Controls
            .OrderBy(c => c.RegionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Row)
            .ThenBy(c => c.Col)
            .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var occupied = new HashSet<(string Region, int Row, int Col)>();

        foreach (var control in ordered)
        {
            var regionKey = control.RegionId ?? string.Empty;
            var key = (regionKey, control.Row, control.Col);
            if (control.Row >= 0 && control.Col >= 0 && occupied.Add(key))
            {
                continue;
            }

            var placed = false;
            for (var row = 0; row < 32 && !placed; row++)
            {
                for (var col = 0; col < 24 && !placed; col++)
                {
                    if (!occupied.Add((regionKey, row, col)))
                    {
                        continue;
                    }

                    control.Row = row;
                    control.Col = col;
                    placed = true;
                }
            }
        }

        MidiLayoutTreeOps.SyncRootGridExtent(layout);
    }

    private void CleanupOrphanBindingsAfterLayoutSave(string deviceName, MidiDeviceLayout newLayout)
    {
        var keepIds = newLayout.Controls
            .Select(c => c.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = _controlIdsAtConstructorEnter ?? [];
        var orphanIds = candidates.Where(id => !keepIds.Contains(id)).ToList();
        if (orphanIds.Count == 0)
        {
            // Also drop bindings whose controlId is unknown to the new layout entirely.
            _midi.MappingStore.RemoveMatching(b =>
                DevicesShareProduct(b.DeviceName, deviceName)
                && !string.IsNullOrWhiteSpace(b.ControlId)
                && !keepIds.Contains(b.ControlId));
            return;
        }

        _midi.MappingStore.RemoveMatching(b =>
            DevicesShareProduct(b.DeviceName, deviceName)
            && !string.IsNullOrWhiteSpace(b.ControlId)
            && (orphanIds.Contains(b.ControlId) || !keepIds.Contains(b.ControlId)));
    }

    private string GenerateUniqueControlId(MidiControlType type)
    {
        var prefix = type switch
        {
            MidiControlType.Fader => "f",
            MidiControlType.Encoder => "e",
            _ => "b"
        };

        var existing = (_draftLayout?.Controls ?? [])
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < 1000; i++)
        {
            var id = $"{prefix}_custom_{i}";
            if (!existing.Contains(id))
            {
                return id;
            }
        }

        return $"{prefix}_custom_{Guid.NewGuid():N}"[..16];
    }

    private static string DefaultLabelForType(MidiControlType type, int row, int col) =>
        type switch
        {
            MidiControlType.Fader => $"F{col + 1}",
            MidiControlType.Encoder => $"E{col + 1}",
            _ => $"B{row + 1}{col + 1}"
        };

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

    private MidiBinding? FindStoredBindingForControl(BlueprintControlVm control)
    {
        if (control.Controller is int cc)
        {
            var byCc = _midi.MappingStore.Bindings.FirstOrDefault(b =>
                b.Controller == cc
                && b.IsNote == control.IsNote
                && b.IsPitchBend == control.IsPitchBend
                && (string.Equals(b.ControlId, control.Id, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(SelectedDeviceName)
                        && DevicesShareProduct(b.DeviceName, SelectedDeviceName))));
            if (byCc is not null)
            {
                return byCc;
            }
        }

        if (!string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            return FindBindingForControl(SelectedDeviceName, control.Id);
        }

        return _midi.MappingStore.Bindings.FirstOrDefault(b =>
            string.Equals(b.ControlId, control.Id, StringComparison.OrdinalIgnoreCase));
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
