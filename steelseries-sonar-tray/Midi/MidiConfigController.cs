using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SonarQuickMixer.Localization;
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
    private LocalizedText _statusMessage = Loc.Text("midi.status.ready");
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
                SetStatus("midi.status.finishEditingFirst");
                RefreshLayoutPresets(selectKey: _presets.GetActivePresetKey(SelectedDeviceName));
                return;
            }

            if (!ConfirmDiscardBindingDraftsIfNeeded(Loc.Get("midi.confirm.switchPreset")))
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
            SetStatus(Loc.Text("midi.status.presetSelected", value.DisplayName));
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

    /// <summary>UI name; the persisted generic layout name is shown in the active language.</summary>
    public string LayoutDisplayName =>
        string.Equals(LayoutName, PresetCatalog.GenericLayoutName, StringComparison.Ordinal)
            ? Loc.Get("midi.preset.builtIn")
            : LayoutName;

    public string LayoutHint => IsLayoutConstructorMode
        ? Loc.Get("midi.status.constructorHint")
        : !string.IsNullOrWhiteSpace(_layout.HintKey) && Loc.Manager.HasKey(_layout.HintKey!)
            ? Loc.Get(_layout.HintKey!)
            : string.IsNullOrWhiteSpace(_layout.Hint)
                ? Loc.Get("midi.status.assignHint")
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

            if (!ConfirmDiscardBindingDraftsIfNeeded(Loc.Get("midi.confirm.switchDevice")))
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

    public string UseDeviceButtonText =>
        IsSelectedDeviceInUse ? Loc.Get("midi.stopUsing") : Loc.Get("midi.useDevice");

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


    public string StatusText => _statusMessage.ToString();

    private void SetStatus(LocalizedText message)
    {
        _statusMessage = message;
        OnPropertyChanged(nameof(StatusText));
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
            || string.Equals(channelIdOrDisplayName, Loc.Get("midi.binding.notAssigned"), StringComparison.OrdinalIgnoreCase)
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
                Loc.Format("midi.conflict.messageWithOwners", owners),
                Loc.Get("midi.conflict.mappingTitle"),
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
        OnPropertyChanged(nameof(LayoutDisplayName));
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
        control.IsBindingDraft = false;
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

        vm.IsBindingDraft = false;

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

        control.IsBindingDraft = true;
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

    /// <summary>
    /// Re-renders every code-generated string after a language switch. XAML text updates itself
    /// through the localization bindings; this covers status lines, combo items and view models.
    /// </summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LayoutHint));
        OnPropertyChanged(nameof(LayoutDisplayName));
        OnPropertyChanged(nameof(UseDeviceButtonText));

        foreach (var device in Devices)
        {
            device.RefreshLocalization();
        }

        foreach (var control in Controls)
        {
            control.RefreshLocalization();
        }

        RefreshLayoutPresets(selectKey: _selectedLayoutPreset?.Key);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
