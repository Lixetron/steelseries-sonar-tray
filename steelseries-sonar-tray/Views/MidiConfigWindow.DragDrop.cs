using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SonarQuickMixer.Controls;
using SonarQuickMixer.Midi;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragDrop = System.Windows.DragDrop;
using WpfDataObject = System.Windows.DataObject;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfPoint = System.Windows.Point;

namespace SonarQuickMixer.Views;

/// <summary>
/// Blueprint layout-constructor drag &amp; drop: hit-testing against the visual tree,
/// drop-zone geometry (via <see cref="MidiLayoutTreeOps"/>), and drag-payload parsing.
/// Split out of MidiConfigWindow.xaml.cs purely to keep that file a manageable size —
/// same partial class, same fields/state, no behavior change.
/// </summary>
public partial class MidiConfigWindow
{
    private const string PaletteDragPrefix = "palette:";
    private const string ControlDragPrefix = "control:";

    private WpfPoint? _dragStartPoint;
    private BlueprintControlVm? _dragSourceControl;
    private BlueprintRegionVm? _dragSourceRegion;

    private void BlueprintControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode)
        {
            return;
        }

        if (IsOriginOnBlueprintDeleteButton(e.OriginalSource))
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: BlueprintControlVm control })
        {
            _dragStartPoint = e.GetPosition(this);
            _dragSourceControl = control;
            _dragSourceRegion = null;
        }
    }

    private void BlueprintControl_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || e.LeftButton != MouseButtonState.Pressed
            || _dragStartPoint is null
            || _dragSourceControl is null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var payload = _dragSourceControl.IsPlaceholder
            ? PaletteDragPrefix + _controller.PaletteSelectedType
            : ControlDragPrefix + _dragSourceControl.Id;

        var data = new WpfDataObject(System.Windows.DataFormats.StringFormat, payload);
        WpfDragDrop.DoDragDrop((DependencyObject)sender, data, WpfDragDropEffects.Move | WpfDragDropEffects.Copy);
        _dragStartPoint = null;
        _dragSourceControl = null;
    }

    private void BlueprintControl_DragOver(object sender, WpfDragEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || sender is not FrameworkElement { DataContext: BlueprintControlVm control } element
            || e.Data.GetData(System.Windows.DataFormats.StringFormat) is not string payload)
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dragSourceId = GetDragSourceId(payload);
        if (IsPayloadSelfTarget(payload, controlId: control.Id, regionId: null))
        {
            _controller.ClearDropSlotPreview();
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }

        // Resolve beside THIS control (hovered), excluding only the drag-source from preview shift.
        if (!TryPreviewBesideFormCell(payload, element, control, e, dragSourceId))
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = WpfDragDropEffects.Move | WpfDragDropEffects.Copy;
        e.Handled = true;
    }

    private void BlueprintControl_DragLeave(object sender, WpfDragEventArgs e)
    {
        if (sender is FrameworkElement element && IsPointerStillInside(element, e))
        {
            return;
        }

        // Parent / sibling may take over.
    }

    private void BlueprintControl_Drop(object sender, WpfDragEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || sender is not FrameworkElement { DataContext: BlueprintControlVm target } element
            || e.Data.GetData(System.Windows.DataFormats.StringFormat) is not string payload)
        {
            return;
        }

        e.Handled = true;
        if (IsPayloadSelfTarget(payload, controlId: target.Id, regionId: null))
        {
            _controller.ClearDropSlotPreview();
            return;
        }

        if (_controller.ActiveDropSlot is { } slot)
        {
            _controller.ConstructorDropToSlot(payload, slot);
        }
        else if (TryBuildBesideFormCell(payload, element, target, e, out var built))
        {
            _controller.ConstructorDropToSlot(payload, built);
        }

        _dragStartPoint = null;
        _dragSourceControl = null;
        _dragSourceRegion = null;
    }


    private static bool IsPayloadSelfTarget(string payload, string? controlId, string? regionId)
    {
        if (payload.StartsWith(ControlDragPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(controlId))
        {
            var id = payload[ControlDragPrefix.Length..];
            return string.Equals(id, controlId, StringComparison.OrdinalIgnoreCase);
        }

        if (payload.StartsWith("region:", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(regionId))
        {
            var id = payload["region:".Length..];
            return string.Equals(id, regionId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsAreaPayload(string payload) =>
        payload.StartsWith("palette:Area", StringComparison.OrdinalIgnoreCase)
        || payload.Equals("palette:Region", StringComparison.OrdinalIgnoreCase)
        || payload.StartsWith("region:", StringComparison.OrdinalIgnoreCase);

    private static string? GetDragSourceId(string payload)
    {
        if (payload.StartsWith(ControlDragPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return payload[ControlDragPrefix.Length..];
        }

        if (payload.StartsWith("region:", StringComparison.OrdinalIgnoreCase))
        {
            return payload["region:".Length..];
        }

        return null;
    }

    /// <summary>
    /// Primary path: insert slot relative to the hovered form cell (control or area chrome).
    /// Restores any live preview first so draft coords and visuals stay aligned.
    /// </summary>
    private bool TryPreviewBesideFormCell(
        string payload,
        FrameworkElement element,
        IBlueprintFormCell hovered,
        WpfDragEventArgs e,
        string? dragSourceId)
    {
        if (!TryBuildBesideFormCell(payload, element, hovered, e, out var slot))
        {
            _controller.ClearDropSlotPreview();
            return false;
        }

        _controller.SetDropSlotPreview(slot, dragSourceId, shiftRegions: IsAreaPayload(payload));
        return true;
    }

    private bool TryBuildBesideFormCell(
        string payload,
        FrameworkElement element,
        IBlueprintFormCell hovered,
        WpfDragEventArgs e,
        out MidiDropSlot slot)
    {
        slot = default;
        var (rowSpan, colSpan) = ResolvePayloadSpan(payload);
        var local = e.GetPosition(element);
        var zone = MidiLayoutTreeOps.ResolveDropZoneBesideOnly(
            local.X, local.Y, element.ActualWidth, element.ActualHeight);

        if (hovered is BlueprintControlVm control
            && _controller.TryGetControlCell(control.Id, out var cr, out var cc, out var crs, out var ccs))
        {
            var fake = new MidiLayoutControl
            {
                Id = control.Id,
                RegionId = control.RegionId,
                Row = cr,
                Col = cc,
                RowSpan = crs,
                ColSpan = ccs,
                Type = control.Type,
                Label = control.Label
            };
            slot = MidiLayoutTreeOps.SlotBesideControl(fake, zone, rowSpan, colSpan);
            return true;
        }

        if (hovered is BlueprintRegionVm region
            && _controller.TryGetRegionCell(region.Id, out var rr, out var rc, out var rrs, out var rcs))
        {
            var fake = new MidiLayoutRegion
            {
                Id = region.Id,
                ParentRegionId = region.ParentRegionId,
                Row = rr,
                Col = rc,
                RowSpan = rrs,
                ColSpan = rcs
            };
            slot = MidiLayoutTreeOps.SlotBesideRegion(fake, zone, rowSpan, colSpan);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolve insert among children of a parent area (gaps / empty panel).
    /// Excludes only the drag-source id.
    /// </summary>
    private bool TryPreviewAmongChildren(
        string payload,
        string? parentRegionId,
        MidiBlueprintCellPanel panel,
        WpfDragEventArgs e,
        string? dragSourceId,
        bool allowEmptyFreeCell)
    {
        _controller.ClearDropSlotPreview(restoreOnly: true);
        var (rowSpan, colSpan) = ResolvePayloadSpan(payload);
        var hits = BuildHitChildren(panel, dragSourceId, forRegions: IsAreaPayload(payload));
        var pos = e.GetPosition(panel);
        if (!MidiLayoutTreeOps.TryResolveInsertSlot(
                hits,
                pos.X,
                pos.Y,
                parentRegionId,
                rowSpan,
                colSpan,
                excludeId: dragSourceId,
                out var slot,
                allowEmptyFreeCell))
        {
            return false;
        }

        _controller.SetDropSlotPreview(slot, dragSourceId, shiftRegions: IsAreaPayload(payload));
        return true;
    }

    private (int RowSpan, int ColSpan) ResolvePayloadSpan(string payload)
    {
        if (payload.StartsWith(ControlDragPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = payload[ControlDragPrefix.Length..];
            if (_controller.TryGetControlCell(id, out _, out _, out var rs, out var cs))
            {
                return (rs, cs);
            }
        }

        if (payload.StartsWith("region:", StringComparison.OrdinalIgnoreCase))
        {
            var id = payload["region:".Length..];
            if (_controller.TryGetRegionCell(id, out _, out _, out var rs, out var cs))
            {
                return (rs, cs);
            }
        }

        if (IsAreaPayload(payload))
        {
            return (Math.Max(1, _controller.DraftRowSpan), Math.Max(1, _controller.DraftColSpan));
        }

        return (1, 1);
    }

    private static bool TryFindSiblingPanel(
        FrameworkElement origin,
        string? parentRegionId,
        out MidiBlueprintCellPanel panel,
        out string? hostRegionId)
    {
        panel = null!;
        hostRegionId = parentRegionId;

        if (origin.DataContext is BlueprintControlVm control)
        {
            hostRegionId = control.RegionId;
            panel = FindAncestorOfType<MidiBlueprintCellPanel>(origin)!;
            return panel is not null;
        }

        if (origin.DataContext is BlueprintRegionVm region)
        {
            hostRegionId = region.ParentRegionId;
            panel = FindAncestorOfType<MidiBlueprintCellPanel>(VisualTreeHelper.GetParent(origin))!;
            return panel is not null;
        }

        panel = FindDescendantOfType<MidiBlueprintCellPanel>(origin)!;
        hostRegionId = parentRegionId;
        return panel is not null;
    }

    private static bool TryGetChildrenPanel(FrameworkElement regionElement, out MidiBlueprintCellPanel panel)
    {
        // Inner ItemsControl panel that lays out this area's children.
        panel = FindDescendantOfType<MidiBlueprintCellPanel>(regionElement)!;
        return panel is not null;
    }

    private static T? FindAncestorOfType<T>(DependencyObject? start) where T : class
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindDescendantOfType<T>(DependencyObject? root) where T : class
    {
        if (root is null)
        {
            return null;
        }

        if (root is T match)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendantOfType<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private List<MidiDropHitChild> BuildHitChildren(
        MidiBlueprintCellPanel panel,
        string? excludeDragSourceId,
        bool forRegions)
    {
        var list = new List<MidiDropHitChild>();
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement { DataContext: IBlueprintFormCell cell } fe
                || cell.IsDropSlot)
            {
                continue;
            }

            if (excludeDragSourceId is not null
                && string.Equals(cell.Id, excludeDragSourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (forRegions)
            {
                if (cell is not BlueprintRegionVm region
                    || !_controller.TryGetRegionCell(region.Id, out var row, out var col, out var rs, out var cs))
                {
                    continue;
                }

                var tl = fe.TranslatePoint(new WpfPoint(0, 0), panel);
                list.Add(new MidiDropHitChild
                {
                    Id = region.Id,
                    Row = row,
                    Col = col,
                    RowSpan = rs,
                    ColSpan = cs,
                    Bounds = new Rect(tl.X, tl.Y, Math.Max(1, fe.ActualWidth), Math.Max(1, fe.ActualHeight))
                });
            }
            else if (cell is BlueprintControlVm control
                     && _controller.TryGetControlCell(control.Id, out var crow, out var ccol, out var crs, out var ccs))
            {
                var tl = fe.TranslatePoint(new WpfPoint(0, 0), panel);
                list.Add(new MidiDropHitChild
                {
                    Id = control.Id,
                    Row = crow,
                    Col = ccol,
                    RowSpan = crs,
                    ColSpan = ccs,
                    Bounds = new Rect(tl.X, tl.Y, Math.Max(1, fe.ActualWidth), Math.Max(1, fe.ActualHeight))
                });
            }
        }

        return list;
    }

    private void BlueprintDropSlot_DragOver(object sender, WpfDragEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || !e.Data.GetDataPresent(System.Windows.DataFormats.StringFormat))
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = WpfDragDropEffects.Move | WpfDragDropEffects.Copy;
        e.Handled = true;
    }

    private void BlueprintDropSlot_DragLeave(object sender, WpfDragEventArgs e)
    {
        // Keep slot while pointer moves to siblings; region leave clears.
    }

    private void BlueprintDropSlot_Drop(object sender, WpfDragEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || e.Data.GetData(System.Windows.DataFormats.StringFormat) is not string payload)
        {
            return;
        }

        e.Handled = true;
        if (_controller.ActiveDropSlot is { } slot)
        {
            _controller.ConstructorDropToSlot(payload, slot);
        }

        _dragStartPoint = null;
        _dragSourceControl = null;
        _dragSourceRegion = null;
    }

    private static bool IsPointerStillInside(FrameworkElement element, WpfDragEventArgs e)
    {
        var pos = e.GetPosition(element);
        return pos.X >= 0
               && pos.Y >= 0
               && pos.X <= element.ActualWidth
               && pos.Y <= element.ActualHeight;
    }

    private void BlueprintControl_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BlueprintControlVm control })
        {
            return;
        }

        if (_controller.IsLayoutConstructorMode)
        {
            if (control.IsPlaceholder)
            {
                _controller.PlacePaletteControlAt(control.Row, control.Col);
            }
            else
            {
                SelectControl(control);
                _controller.DraftControlLabel = control.Label ?? string.Empty;
                _controller.SyncDraftSpansFromControl(control);
            }

            UpdateLearnButtons();
            e.Handled = true;
            return;
        }

        if (!_controller.IsSelectedDeviceInUse)
        {
            return;
        }

        if (control.IsPlaceholder)
        {
            return;
        }

        // Selection only — Learn is explicit via the Learn MIDI button.
        if (_learnInProgress && !ReferenceEquals(_selectedControl, control))
        {
            _controller.CancelLearn();
            _learnInProgress = false;
        }

        SelectControl(control);
        UpdateLearnButtons();
        e.Handled = true;
    }

    private void PaletteItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string typeName })
        {
            return;
        }

        if (typeName is "Area" or "Region")
        {
            var areaData = new WpfDataObject(System.Windows.DataFormats.StringFormat, "palette:Area");
            WpfDragDrop.DoDragDrop((DependencyObject)sender, areaData, WpfDragDropEffects.Copy);
            e.Handled = true;
            return;
        }

        if (!Enum.TryParse<MidiControlType>(typeName, out var type))
        {
            return;
        }

        _controller.PaletteSelectedType = type;
        var data = new WpfDataObject(System.Windows.DataFormats.StringFormat, PaletteDragPrefix + type);
        WpfDragDrop.DoDragDrop((DependencyObject)sender, data, WpfDragDropEffects.Copy);
        e.Handled = true;
    }

    private void PaletteItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string typeName })
        {
            return;
        }

        if (typeName is "Area" or "Region")
        {
            return;
        }

        if (Enum.TryParse<MidiControlType>(typeName, out var type))
        {
            _controller.PaletteSelectedType = type;
        }
    }

    private void ConstructorRegion_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || sender is not FrameworkElement regionElement
            || regionElement.DataContext is not BlueprintRegionVm region)
        {
            return;
        }

        if (IsOriginOnBlueprintDeleteButton(e.OriginalSource))
        {
            return;
        }

        // Clicks on nested controls/areas belong to those children — don't start a parent-area drag.
        if (IsOriginInsideNestedBlueprintChild(e.OriginalSource, regionElement, region))
        {
            return;
        }

        _dragStartPoint = e.GetPosition(this);
        _dragSourceRegion = region;
        _dragSourceControl = null;
    }

    private void ConstructorRegion_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || e.LeftButton != MouseButtonState.Pressed
            || _dragStartPoint is null
            || _dragSourceRegion is null
            || _dragSourceControl is not null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new WpfDataObject(System.Windows.DataFormats.StringFormat, "region:" + _dragSourceRegion.Id);
        WpfDragDrop.DoDragDrop((DependencyObject)sender, data, WpfDragDropEffects.Move);
        _dragStartPoint = null;
        _dragSourceRegion = null;
    }

    private void ConstructorRegion_DragOver(object sender, WpfDragEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || sender is not FrameworkElement { DataContext: BlueprintRegionVm region } element
            || e.Data.GetData(System.Windows.DataFormats.StringFormat) is not string payload)
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }

        // Prefer the innermost control/area under the pointer for drop targeting.
        if (FindNestedBlueprintChildUnder(e.OriginalSource, element, region) is { } nested
            && nested is not BlueprintDropSlotVm)
        {
            return;
        }

        if (IsPayloadSelfTarget(payload, controlId: null, regionId: region.Id))
        {
            _controller.ClearDropSlotPreview();
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dragSourceId = GetDragSourceId(payload);

        // 1) Among this area's children: edges of children + gaps between them.
        if (TryGetChildrenPanel(element, out var childrenPanel)
            && TryPreviewAmongChildren(
                payload,
                parentRegionId: region.Id,
                childrenPanel,
                e,
                dragSourceId,
                allowEmptyFreeCell: false))
        {
            e.Effects = WpfDragDropEffects.Move | WpfDragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(element);
        var zone = MidiLayoutTreeOps.ResolveDropZone(pos.X, pos.Y, element.ActualWidth, element.ActualHeight);

        // 2) Edge of this area chrome → insert beside it among siblings.
        if (zone != MidiLayoutDropZone.Inside)
        {
            if (TryPreviewBesideFormCell(payload, element, region, e, dragSourceId))
            {
                e.Effects = WpfDragDropEffects.Move | WpfDragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }

        // 3) Empty interior → nest Inside.
        _controller.ClearDropSlotPreview();
        _controller.SetDropPreview(region, MidiLayoutDropZone.Inside);
        e.Effects = WpfDragDropEffects.Move | WpfDragDropEffects.Copy;
        e.Handled = true;
    }

    private void ConstructorRegion_DragLeave(object sender, WpfDragEventArgs e)
    {
        if (sender is FrameworkElement element && IsPointerStillInside(element, e))
        {
            return;
        }

        _controller.ClearDropSlotPreview();
    }

    private void ConstructorRegion_Drop(object sender, WpfDragEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || sender is not FrameworkElement { DataContext: BlueprintRegionVm region } element
            || e.Data.GetData(System.Windows.DataFormats.StringFormat) is not string payload)
        {
            return;
        }

        // Let nested control/area handlers own the drop when the pointer is over them.
        if (FindNestedBlueprintChildUnder(e.OriginalSource, element, region) is { } nested
            && nested is not BlueprintDropSlotVm)
        {
            return;
        }

        e.Handled = true;
        if (IsPayloadSelfTarget(payload, controlId: null, regionId: region.Id))
        {
            _controller.ClearDropSlotPreview();
            return;
        }

        if (_controller.ActiveDropSlot is { } slot)
        {
            _controller.ConstructorDropToSlot(payload, slot);
        }
        else
        {
            _controller.ClearDropSlotPreview();
            _controller.ConstructorDrop(payload, region.Id, targetControlId: null, MidiLayoutDropZone.Inside);
        }

        _dragStartPoint = null;
        _dragSourceControl = null;
        _dragSourceRegion = null;
    }

    private void ConstructorRegion_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || sender is not FrameworkElement regionElement
            || regionElement.DataContext is not BlueprintRegionVm region)
        {
            return;
        }

        if (IsOriginOnBlueprintDeleteButton(e.OriginalSource))
        {
            return;
        }

        // Nested M/S/fader/Transport buttons (and child areas) must keep their own selection.
        if (IsOriginInsideNestedBlueprintChild(e.OriginalSource, regionElement, region))
        {
            return;
        }

        SelectRegion(region);
        e.Handled = true;
    }

    private static bool IsOriginOnBlueprintDeleteButton(object? originalSource)
    {
        for (var current = originalSource as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Button { Tag: "BlueprintDelete" })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="originalSource"/> is inside a nested control or child area
    /// of <paramref name="regionElement"/> (not the region's own chrome).
    /// </summary>
    private static bool IsOriginInsideNestedBlueprintChild(
        object? originalSource,
        FrameworkElement regionElement,
        BlueprintRegionVm selfRegion)
    {
        return FindNestedBlueprintChildUnder(originalSource, regionElement, selfRegion) is not null;
    }

    private static object? FindNestedBlueprintChildUnder(
        object? originalSource,
        FrameworkElement regionElement,
        BlueprintRegionVm selfRegion)
    {
        if (originalSource is not DependencyObject start)
        {
            return null;
        }

        for (var current = start;
             current is not null && !ReferenceEquals(current, regionElement);
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement { DataContext: { } dc })
            {
                continue;
            }

            if (dc is BlueprintControlVm control)
            {
                return control;
            }

            if (dc is BlueprintDropSlotVm)
            {
                return dc;
            }

            if (dc is BlueprintRegionVm nested && !ReferenceEquals(nested, selfRegion))
            {
                return nested;
            }
        }

        return null;
    }

    private void SelectRegion(BlueprintRegionVm region)
    {
        if (_selectedControl is not null)
        {
            _selectedControl.IsSelected = false;
            _selectedControl = null;
        }

        if (_selectedRegion is not null && !ReferenceEquals(_selectedRegion, region))
        {
            _selectedRegion.IsSelected = false;
        }

        _selectedRegion = region;
        region.IsSelected = true;
        _controller.SelectConstructorRegion(region.Id);
        _controller.DraftControlLabel = region.Label ?? string.Empty;
        _controller.SyncDraftSpansFromRegion(region.Id);
        UpdateLearnButtons();
    }

    private void BlueprintCanvas_DragOver(object sender, WpfDragEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || e.Data.GetData(System.Windows.DataFormats.StringFormat) is not string payload)
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dragSourceId = GetDragSourceId(payload);
        if (TryGetChildrenPanel(ConstructorTree, out var rootPanel)
            && TryPreviewAmongChildren(
                payload,
                parentRegionId: null,
                rootPanel,
                e,
                dragSourceId,
                allowEmptyFreeCell: true))
        {
            e.Effects = WpfDragDropEffects.Move | WpfDragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.Effects = WpfDragDropEffects.Move | WpfDragDropEffects.Copy;
        e.Handled = true;
    }

    private void BlueprintCanvas_DragLeave(object sender, WpfDragEventArgs e)
    {
        if (sender is FrameworkElement element && IsPointerStillInside(element, e))
        {
            return;
        }

        _controller.ClearDropSlotPreview();
    }

    private void BlueprintCanvas_Drop(object sender, WpfDragEventArgs e)
    {
        if (!_controller.IsLayoutConstructorMode
            || e.Data.GetData(System.Windows.DataFormats.StringFormat) is not string payload)
        {
            return;
        }

        // Nested region/control handlers own drops when the pointer is over them.
        if (e.OriginalSource is DependencyObject origin
            && FindAncestorDataContext<BlueprintRegionVm>(origin) is not null)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject originControl
            && FindAncestorDataContext<BlueprintControlVm>(originControl) is { IsPlaceholder: false })
        {
            return;
        }

        if (e.OriginalSource is DependencyObject originSlot
            && FindAncestorDataContext<BlueprintDropSlotVm>(originSlot) is not null)
        {
            return;
        }

        e.Handled = true;
        if (_controller.ActiveDropSlot is { } slot)
        {
            _controller.ConstructorDropToSlot(payload, slot);
        }
        else
        {
            _controller.ClearDropSlotPreview();
            _controller.ConstructorDrop(payload, targetRegionId: null, targetControlId: null, MidiLayoutDropZone.Inside);
        }

        _dragStartPoint = null;
        _dragSourceControl = null;
        _dragSourceRegion = null;
    }

    private static T? FindAncestorDataContext<T>(DependencyObject? start) where T : class
    {
        for (var current = start; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: T match })
            {
                return match;
            }
        }

        return null;
    }
}
