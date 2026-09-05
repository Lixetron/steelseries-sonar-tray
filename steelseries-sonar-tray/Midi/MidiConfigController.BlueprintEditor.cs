using System.Collections.ObjectModel;
using System.IO;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Layout-constructor drag-drop tree editing (palette placement, moves, deletes, span/label
/// edits, drop-slot previews), LED feedback tag editing, and the unsaved/dirty-flag tracking
/// that ties both to the yellow-chrome UI. These stayed together because they share the same
/// draft state (draft layout, draft feedback fields) — see docs/architecture.md.
/// Split out of MidiConfigController.cs purely to keep that file a manageable size — same
/// partial class, same fields/state, no behavior change.
/// </summary>
public partial class MidiConfigController
{
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
}
