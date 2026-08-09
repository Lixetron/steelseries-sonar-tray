using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SonarQuickMixer.Controls;
using SonarQuickMixer.Midi;
using SonarQuickMixer.Sonar;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragDrop = System.Windows.DragDrop;
using WpfDataObject = System.Windows.DataObject;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfPoint = System.Windows.Point;

namespace SonarQuickMixer.Views;

public partial class MidiConfigWindow : Window
{
    private const string PaletteDragPrefix = "palette:";
    private const string ControlDragPrefix = "control:";

    private readonly MidiConfigController _controller;
    private BlueprintControlVm? _selectedControl;
    private BlueprintRegionVm? _selectedRegion;
    private TaskCompletionSource<bool>? _conflictTcs;
    private bool _learnInProgress;
    private bool _syncingDeviceList;
    private bool _syncingBindingCombos;
    private WpfPoint? _dragStartPoint;
    private BlueprintControlVm? _dragSourceControl;
    private BlueprintRegionVm? _dragSourceRegion;

    public MidiConfigWindow(MidiControlService midiService)
    {
        InitializeComponent();
        WindowDarkMode.TryEnable(this);
        _controller = new MidiConfigController(midiService);
        _controller.ConflictConfirmationRequested = ShowConflictOverlayAsync;
        _controller.PropertyChanged += Controller_PropertyChanged;
        _controller.BlueprintLayoutRefreshRequested += OnBlueprintLayoutRefreshRequested;
        DataContext = _controller;

        ChannelCombo.Items.Add(new ComboBoxItem
        {
            Content = "Not assigned",
            Tag = MidiBinding.UnassignedChannelId
        });

        foreach (var channel in SonarChannels.All)
        {
            ChannelCombo.Items.Add(new ComboBoxItem
            {
                Content = SonarChannels.GetDisplayName(channel),
                Tag = channel
            });
        }

        ChannelCombo.SelectedIndex = 0;
        ModeCombo.SelectedIndex = 0;
        RebuildActionCombo(MidiControlType.Fader, MidiBindingAction.Volume);
        RebuildFeedbackSourceCombo(forFader: true, FeedbackSourceCombo);
        FeedbackSourceCombo.SelectedIndex = 0;
        FeedbackSourceCombo.IsEnabled = false;
        FeedbackStyleCombo.SelectedIndex = 0;
        FeedbackStyleCombo.IsEnabled = false;
        RebuildFeedbackSourceCombo(forFader: true, DraftFeedbackSourceCombo);
        DraftFeedbackSourceCombo.SelectedIndex = 0;
        DraftFeedbackStyleCombo.SelectedIndex = 0;
        SyncDeviceListSelection();
        UpdateLearnButtons();

        _controller.ConfirmDiscardUnsavedAssignments = message =>
        {
            var result = WpfMessageBox.Show(
                this,
                message + "\n\nDiscard unsaved assignments?",
                "Unsaved assignments",
                WpfMessageBoxButton.YesNo,
                WpfMessageBoxImage.Warning);
            return result == WpfMessageBoxResult.Yes;
        };

        ChannelCombo.SelectionChanged += BindingCombo_SelectionChanged;
        ModeCombo.SelectionChanged += BindingCombo_SelectionChanged;
        ActionCombo.SelectionChanged += BindingCombo_SelectionChanged;

        Closing += (_, e) =>
        {
            if (!_controller.ConfirmDiscardBindingDraftsIfNeeded(
                    "Closing MIDI Setup will discard unsaved channel assignments."))
            {
                e.Cancel = true;
            }
        };

        Closed += (_, _) =>
        {
            _conflictTcs?.TrySetResult(false);
            _controller.PropertyChanged -= Controller_PropertyChanged;
            _controller.BlueprintLayoutRefreshRequested -= OnBlueprintLayoutRefreshRequested;
            _controller.ClearDropSlotPreview();
            _controller.CancelLearn();
            if (_controller.IsLayoutConstructorMode)
            {
                _controller.CancelLayoutConstructor(silent: true);
            }

            _controller.Dispose();
        };
    }

    private void OnBlueprintLayoutRefreshRequested()
    {
        InvalidateBlueprintPanels(ConstructorTree);
    }

    private static void InvalidateBlueprintPanels(DependencyObject? root)
    {
        if (root is null)
        {
            return;
        }

        if (root is MidiBlueprintCellPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            InvalidateBlueprintPanels(VisualTreeHelper.GetChild(root, i));
        }
    }

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MidiConfigController.SelectedDeviceName)
            or nameof(MidiConfigController.IsSelectedDeviceInUse)
            or nameof(MidiConfigController.UseDeviceButtonText)
            or nameof(MidiConfigController.Devices)
            or nameof(MidiConfigController.IsLayoutConstructorMode)
            or nameof(MidiConfigController.CanEditBindings)
            or nameof(MidiConfigController.CanLearnHardware)
            or nameof(MidiConfigController.UseConstructorTree)
            or nameof(MidiConfigController.UseRegionTreeLayout))
        {
            SyncDeviceListSelection();
            UpdateLearnButtons();
        }
    }

    private void SyncDeviceListSelection()
    {
        if (string.IsNullOrWhiteSpace(_controller.SelectedDeviceName))
        {
            return;
        }

        var match = _controller.Devices.FirstOrDefault(d =>
            string.Equals(d.Name, _controller.SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (match is null || ReferenceEquals(DevicesList.SelectedItem, match))
        {
            return;
        }

        _syncingDeviceList = true;
        try
        {
            DevicesList.SelectedItem = match;
            DevicesList.ScrollIntoView(match);
        }
        finally
        {
            _syncingDeviceList = false;
        }
    }

    private void BlueprintZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomBlueprintAroundViewportCenter(() => _controller.ZoomBlueprintIn());

    private void BlueprintZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomBlueprintAroundViewportCenter(() => _controller.ZoomBlueprintOut());

    private void BlueprintZoomReset_Click(object sender, RoutedEventArgs e) =>
        ZoomBlueprintAroundViewportCenter(() => _controller.ResetBlueprintZoom());

    private void BlueprintScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var steps = Math.Max(1, Math.Abs(e.Delta) / 120);
        ZoomBlueprintAt(
            scrollViewer,
            e.GetPosition(scrollViewer),
            () =>
            {
                for (var i = 0; i < steps; i++)
                {
                    if (e.Delta > 0)
                    {
                        _controller.ZoomBlueprintIn();
                    }
                    else
                    {
                        _controller.ZoomBlueprintOut();
                    }
                }
            });
    }

    private void ZoomBlueprintAroundViewportCenter(Action applyZoom)
    {
        var scrollViewer = BlueprintScrollViewer;
        var center = new WpfPoint(scrollViewer.ViewportWidth / 2, scrollViewer.ViewportHeight / 2);
        ZoomBlueprintAt(scrollViewer, center, applyZoom);
    }

    private void ZoomBlueprintAt(ScrollViewer scrollViewer, WpfPoint viewportAnchor, Action applyZoom)
    {
        // LayoutTransform does not change the host's local coordinate space — keep the
        // pre-zoom local point, then scroll so it stays under the same viewport pixel.
        WpfPoint localOnHost;
        try
        {
            localOnHost = scrollViewer.TranslatePoint(viewportAnchor, BlueprintZoomHost);
        }
        catch (InvalidOperationException)
        {
            applyZoom();
            return;
        }

        var oldZoom = _controller.BlueprintZoom;
        applyZoom();
        if (Math.Abs(_controller.BlueprintZoom - oldZoom) < 0.001)
        {
            return;
        }

        scrollViewer.UpdateLayout();
        BlueprintZoomHost.UpdateLayout();

        WpfPoint afterInViewport;
        try
        {
            afterInViewport = BlueprintZoomHost.TranslatePoint(localOnHost, scrollViewer);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        scrollViewer.ScrollToHorizontalOffset(Math.Clamp(
            scrollViewer.HorizontalOffset + (afterInViewport.X - viewportAnchor.X),
            0,
            scrollViewer.ScrollableWidth));
        scrollViewer.ScrollToVerticalOffset(Math.Clamp(
            scrollViewer.VerticalOffset + (afterInViewport.Y - viewportAnchor.Y),
            0,
            scrollViewer.ScrollableHeight));
    }

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

    private void BlueprintDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!_controller.IsLayoutConstructorMode || sender is not FrameworkElement fe)
        {
            return;
        }

        // × only appears on the selected item — delete that item alone (no ancestor menu).
        if (fe.DataContext is BlueprintControlVm { IsPlaceholder: false } control)
        {
            ClearSelection();
            _controller.DeleteDraftControl(control.Id);
            UpdateLearnButtons();
            return;
        }

        if (fe.DataContext is BlueprintRegionVm region)
        {
            ClearSelection();
            _controller.DeleteDraftRegion(region.Id, deleteContents: false);
            UpdateLearnButtons();
        }
    }

    private void SelectControl(BlueprintControlVm control)
    {
        if (_selectedRegion is not null)
        {
            _selectedRegion.IsSelected = false;
            _selectedRegion = null;
        }

        _controller.ClearConstructorRegionSelection();

        if (_selectedControl is not null && !ReferenceEquals(_selectedControl, control))
        {
            _selectedControl.IsSelected = false;
        }

        _selectedControl = control;
        control.IsSelected = true;
        _controller.SetInspectorControl(control);

        _syncingBindingCombos = true;
        try
        {
            if (_controller.IsLayoutConstructorMode)
            {
                _controller.SyncDraftSpansFromControl(control);
                RebuildAndSyncFeedbackCombos(control);
                return;
            }

            var selected = false;
            for (var i = 0; i < ChannelCombo.Items.Count; i++)
            {
                if (ChannelCombo.Items[i] is not ComboBoxItem item)
                {
                    continue;
                }

                var tag = item.Tag as string ?? string.Empty;
                var matchesUnassigned = string.IsNullOrWhiteSpace(control.ChannelId) && string.IsNullOrWhiteSpace(tag);
                var matchesChannel = !string.IsNullOrWhiteSpace(control.ChannelId)
                                     && string.Equals(tag, control.ChannelId, StringComparison.OrdinalIgnoreCase);
                if (matchesUnassigned || matchesChannel)
                {
                    ChannelCombo.SelectedIndex = i;
                    selected = true;
                    break;
                }
            }

            if (!selected)
            {
                ChannelCombo.SelectedIndex = 0;
            }

            ModeCombo.SelectedIndex = control.Mode == MidiValueMode.Relative ? 1 : 0;
            ModeCombo.IsEnabled = control.Type != MidiControlType.Button;
            RebuildActionCombo(control.Type, control.Action);

            _controller.SyncDraftSpansFromControl(control);
            RebuildAndSyncFeedbackCombos(control);
        }
        finally
        {
            _syncingBindingCombos = false;
        }
    }

    private void RebuildAndSyncFeedbackCombos(BlueprintControlVm control)
    {
        var forFader = control.Type == MidiControlType.Fader || control.IsPitchBend;
        var tag = _controller.GetControlFeedbackTag(control.Id);
        MidiFeedbackUi.TryParseTag(tag, out var source, out _);
        var styleEnabled = !control.IsPlaceholder
                           && source != MidiFeedbackSource.None
                           && !forFader;

        RebuildFeedbackSourceCombo(forFader, FeedbackSourceCombo);
        SyncFeedbackSourceCombo(FeedbackSourceCombo, control.Id);
        SyncFeedbackStyleCombo(FeedbackStyleCombo, control.Id);
        FeedbackSourceCombo.IsEnabled = !control.IsPlaceholder && _controller.CanEditBindings;
        FeedbackStyleCombo.IsEnabled = styleEnabled && _controller.CanEditBindings;

        RebuildFeedbackSourceCombo(forFader, DraftFeedbackSourceCombo);
        SyncFeedbackSourceCombo(DraftFeedbackSourceCombo, control.Id);
        SyncFeedbackStyleCombo(DraftFeedbackStyleCombo, control.Id);
    }

    private static void RebuildFeedbackSourceCombo(bool forFader, System.Windows.Controls.ComboBox combo)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "Off", Tag = "None" });
        if (!forFader)
        {
            combo.Items.Add(new ComboBoxItem { Content = "On mute", Tag = "Mute" });
        }

        combo.Items.Add(new ComboBoxItem
        {
            Content = forFader ? "Channel assigned (soft takeover)" : "On channel selected",
            Tag = "ChannelAssigned"
        });
    }

    private void SyncFeedbackSourceCombo(System.Windows.Controls.ComboBox combo, string controlId)
    {
        var tag = _controller.GetControlFeedbackTag(controlId);
        MidiFeedbackUi.TryParseTag(tag, out var source, out _);
        var sourceTag = MidiFeedbackUi.ToSourceTag(source);
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: string itemTag }
                && string.Equals(itemTag, sourceTag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private void SyncFeedbackStyleCombo(System.Windows.Controls.ComboBox combo, string controlId)
    {
        var tag = _controller.GetControlFeedbackTag(controlId);
        MidiFeedbackUi.TryParseTag(tag, out _, out var style);
        var styleTag = MidiFeedbackUi.ToStyleTag(style);
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: string itemTag }
                && string.Equals(itemTag, styleTag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private void ClearSelection()
    {
        if (_selectedControl is not null)
        {
            _selectedControl.IsSelected = false;
            _selectedControl = null;
        }

        if (_selectedRegion is not null)
        {
            _selectedRegion.IsSelected = false;
            _selectedRegion = null;
        }

        _controller.ClearConstructorRegionSelection();
        _controller.DisableDraftHideBorderEditor();
        _controller.SetInspectorControl(null);

        if (!_controller.IsLayoutConstructorMode)
        {
            _syncingBindingCombos = true;
            try
            {
                ChannelCombo.SelectedIndex = 0;
                ModeCombo.SelectedIndex = 0;
                ModeCombo.IsEnabled = true;
                RebuildActionCombo(MidiControlType.Fader, MidiBindingAction.None);
                RebuildFeedbackSourceCombo(forFader: true, FeedbackSourceCombo);
                FeedbackSourceCombo.SelectedIndex = 0;
                FeedbackSourceCombo.IsEnabled = false;
                FeedbackStyleCombo.SelectedIndex = 0;
                FeedbackStyleCombo.IsEnabled = false;
            }
            finally
            {
                _syncingBindingCombos = false;
            }
        }

        UpdateLearnButtons();
    }

    private void DraftFeedbackSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBindingCombos
            || !_controller.IsLayoutConstructorMode
            || !_controller.DraftFeedbackEnabled
            || _selectedControl is null
            || _selectedControl.IsPlaceholder
            || sender is not System.Windows.Controls.ComboBox combo
            || combo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _controller.SetControlFeedbackSourceFromTag(_selectedControl.Id, tag);
        FeedbackStyleCombo.IsEnabled = _controller.DraftFeedbackStyleEnabled;
        DraftFeedbackStyleCombo.IsEnabled = _controller.DraftFeedbackStyleEnabled;
    }

    private void DraftFeedbackStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBindingCombos
            || !_controller.IsLayoutConstructorMode
            || !_controller.DraftFeedbackStyleEnabled
            || _selectedControl is null
            || _selectedControl.IsPlaceholder
            || sender is not System.Windows.Controls.ComboBox combo
            || combo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _controller.SetControlFeedbackStyleFromTag(_selectedControl.Id, tag);
    }

    private void FeedbackSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBindingCombos
            || _selectedControl is null
            || _selectedControl.IsPlaceholder
            || !_controller.CanEditBindings)
        {
            return;
        }

        if (FeedbackSourceCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _controller.SetControlFeedbackSourceFromTag(_selectedControl.Id, tag);
        FeedbackStyleCombo.IsEnabled = _controller.DraftFeedbackStyleEnabled;
    }

    private void FeedbackStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBindingCombos
            || _selectedControl is null
            || _selectedControl.IsPlaceholder
            || !_controller.CanEditBindings
            || !_controller.DraftFeedbackStyleEnabled)
        {
            return;
        }

        if (FeedbackStyleCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _controller.SetControlFeedbackStyleFromTag(_selectedControl.Id, tag);
    }

    private void DraftHideBorder_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.DraftHideBorderEnabled && _selectedRegion is not null)
        {
            ApplyDraftInspector();
        }
    }

    private void DraftKeepSpacing_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.DraftKeepSpacingEnabled && _selectedRegion is not null)
        {
            ApplyDraftInspector();
        }
    }

    private void DraftContentJustify_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controller.DraftContentJustifyEnabled && _selectedRegion is not null)
        {
            ApplyDraftInspector();
        }
    }

    private void DraftLabelBox_LostFocus(object sender, RoutedEventArgs e) => ApplyDraftInspector();

    private void DraftLabelBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyDraftInspector();
            e.Handled = true;
        }
    }

    private void DraftSpanBox_LostFocus(object sender, RoutedEventArgs e) => ApplyDraftInspector();

    private void DraftSpanBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyDraftInspector();
            e.Handled = true;
        }
    }

    private void DraftColSpanMinus_Click(object sender, RoutedEventArgs e)
    {
        _controller.DraftColSpan = Math.Max(1, _controller.DraftColSpan - 1);
        ApplyDraftSpansOnly();
    }

    private void DraftColSpanPlus_Click(object sender, RoutedEventArgs e)
    {
        _controller.DraftColSpan = Math.Min(16, _controller.DraftColSpan + 1);
        ApplyDraftSpansOnly();
    }

    private void DraftRowSpanMinus_Click(object sender, RoutedEventArgs e)
    {
        _controller.DraftRowSpan = Math.Max(1, _controller.DraftRowSpan - 1);
        ApplyDraftSpansOnly();
    }

    private void DraftRowSpanPlus_Click(object sender, RoutedEventArgs e)
    {
        _controller.DraftRowSpan = Math.Min(16, _controller.DraftRowSpan + 1);
        ApplyDraftSpansOnly();
    }

    private void ApplyDraftSpansOnly()
    {
        if (!_controller.IsLayoutConstructorMode)
        {
            return;
        }

        if (_selectedRegion is not null)
        {
            var id = _selectedRegion.Id;
            _controller.SetDraftSpans(controlId: null, id, _controller.DraftRowSpan, _controller.DraftColSpan);
            _controller.SelectConstructorRegion(id);
            _selectedRegion = new BlueprintRegionVm
            {
                Id = id,
                Label = _controller.DraftControlLabel,
                HideBorder = _controller.DraftHideBorder,
                KeepSpacing = _controller.DraftKeepSpacing,
                ContentJustify = _controller.DraftContentJustify,
                ContentAlign = _controller.DraftContentAlign
            };
            return;
        }

        if (_selectedControl is not null && !_selectedControl.IsPlaceholder)
        {
            var id = _selectedControl.Id;
            _controller.SetDraftSpans(id, regionId: null, _controller.DraftRowSpan, _controller.DraftColSpan);
            var refreshed = _controller.Controls.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null)
            {
                SelectControl(refreshed);
                _controller.DraftControlLabel = refreshed.Label;
                _controller.SyncDraftSpansFromControl(refreshed);
            }
        }
    }

    private void ApplyDraftInspector()
    {
        if (!_controller.IsLayoutConstructorMode)
        {
            return;
        }

        var label = _controller.DraftControlLabel;
        var rowSpan = _controller.DraftRowSpan;
        var colSpan = _controller.DraftColSpan;

        if (_selectedRegion is not null)
        {
            var id = _selectedRegion.Id;
            _controller.ApplyDraftSelection(
                null,
                id,
                label,
                rowSpan,
                colSpan,
                _controller.DraftHideBorder,
                _controller.DraftKeepSpacing,
                _controller.DraftContentJustify,
                _controller.DraftContentAlign);
            _selectedRegion = new BlueprintRegionVm
            {
                Id = id,
                Label = label?.Trim() ?? string.Empty,
                HideBorder = _controller.DraftHideBorder,
                KeepSpacing = _controller.DraftKeepSpacing,
                ContentJustify = _controller.DraftContentJustify,
                ContentAlign = _controller.DraftContentAlign
            };
            _controller.SelectConstructorRegion(id);
            _controller.SyncDraftSpansFromRegion(id);
            return;
        }

        if (_selectedControl is not null && !_selectedControl.IsPlaceholder)
        {
            var id = _selectedControl.Id;
            _controller.ApplyDraftSelection(id, null, label, rowSpan, colSpan);
            var refreshed = _controller.Controls.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null)
            {
                SelectControl(refreshed);
                _controller.DraftControlLabel = refreshed.Label;
                _controller.SyncDraftSpansFromControl(refreshed);
            }
        }
    }

    private void UpdateLearnButtons()
    {
        var canLearn = _controller.CanLearnHardware && _selectedControl is { IsPlaceholder: false };
        LearnButton.IsEnabled = canLearn && !_learnInProgress;
        CancelLearnButton.IsEnabled = _learnInProgress;
        LearnButton.Content = _learnInProgress ? "Listening…" : "Learn MIDI";
    }

    private async void LearnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedControl is null || _selectedControl.IsPlaceholder || _learnInProgress
            || !_controller.CanLearnHardware)
        {
            return;
        }

        var control = _selectedControl;
        _learnInProgress = true;
        UpdateLearnButtons();

        try
        {
            await _controller.StartLearnAsync(control).ConfigureAwait(true);
            if (ReferenceEquals(_selectedControl, control))
            {
                SelectControl(control);
            }
        }
        finally
        {
            _learnInProgress = false;
            UpdateLearnButtons();
        }
    }

    private void CancelLearnButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.CancelLearn();
        _learnInProgress = false;
        UpdateLearnButtons();
    }

    private void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDeviceList)
        {
            return;
        }

        if (DevicesList.SelectedItem is MidiDeviceListItemVm device)
        {
            if (_learnInProgress)
            {
                _controller.CancelLearn();
                _learnInProgress = false;
            }

            ClearSelection();
            _controller.SelectedDeviceName = device.Name;
            UpdateLearnButtons();
        }
    }

    private void UseDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_learnInProgress)
        {
            _controller.CancelLearn();
            _learnInProgress = false;
        }

        _controller.ToggleSelectedDeviceUse();
        UpdateLearnButtons();
    }

    private void EditLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_learnInProgress)
        {
            _controller.CancelLearn();
            _learnInProgress = false;
        }

        ClearSelection();
        _controller.EnterLayoutConstructor();
        UpdateLearnButtons();
    }

    private void CancelLayoutConstructor_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        _controller.CancelLayoutConstructor();
        UpdateLearnButtons();
    }

    private void SaveLayoutConstructor_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        _controller.SaveLayoutConstructor();
        UpdateLearnButtons();
    }

    private void DeleteUserPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.SelectedLayoutPreset is not { IsUser: true, FileName: { } fileName })
        {
            return;
        }

        var result = WpfMessageBox.Show(
            $"Delete user layout preset “{fileName}”?\n\nThis cannot be undone. Other presets are kept.",
            "Delete user preset",
            WpfMessageBoxButton.YesNo,
            WpfMessageBoxImage.Warning);
        if (result != WpfMessageBoxResult.Yes)
        {
            return;
        }

        ClearSelection();
        _controller.DeleteSelectedUserPreset();
        UpdateLearnButtons();
    }

    private void SavePresetAsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.HasSelectedDevice)
        {
            return;
        }

        var initial = _controller.LayoutName;
        if (string.IsNullOrWhiteSpace(initial) || initial is "Generic Custom Grid" or "Custom")
        {
            initial = MidiDevicePortNaming.CoreProductName(_controller.SelectedDeviceName!) + " (custom)";
        }

        if (!PromptTextWindow.TryPrompt(
                this,
                "Save layout preset as",
                "Name for the new user preset:",
                initial,
                out var name))
        {
            return;
        }

        ClearSelection();
        if (!_controller.TrySaveLayoutPresetAs(name, out var error))
        {
            WpfMessageBox.Show(
                error,
                "Save preset as",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Warning);
            return;
        }

        UpdateLearnButtons();
    }

    private void RenamePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.CanRenameSelectedLayoutPreset)
        {
            return;
        }

        var initial = _controller.LayoutName;
        if (string.IsNullOrWhiteSpace(initial))
        {
            initial = MidiDevicePortNaming.CoreProductName(_controller.SelectedDeviceName!) + " (custom)";
        }

        if (!PromptTextWindow.TryPrompt(
                this,
                "Rename layout preset",
                "New name for this user preset:",
                initial,
                out var name))
        {
            return;
        }

        if (!_controller.TryRenameCurrentLayoutPreset(name, out var error))
        {
            WpfMessageBox.Show(
                error,
                "Rename preset",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Warning);
            return;
        }
    }

    private void ExportLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.HasSelectedDevice)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export MIDI layout",
            Filter = "MIDI layout JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = _controller.GetExportFileStem() + ".json",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = _controller.ExportLayoutJson();
            File.WriteAllText(dialog.FileName, json);
            WpfMessageBox.Show(
                $"Exported layout to:\n{dialog.FileName}",
                "Export layout",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Could not export layout:\n\n{ex.Message}",
                "Export layout",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Error);
        }
    }

    private void ImportLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.HasSelectedDevice)
        {
            return;
        }

        if (!_controller.IsLayoutConstructorMode)
        {
            var confirm = WpfMessageBox.Show(
                "Import a layout JSON as a new user preset for the selected device?\n\n" +
                "Existing presets stay. MIDI channel bindings are not imported.",
                "Import layout",
                WpfMessageBoxButton.YesNo,
                WpfMessageBoxImage.Question);
            if (confirm != WpfMessageBoxResult.Yes)
            {
                return;
            }
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import MIDI layout",
            Filter = "MIDI layout JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Could not read file:\n\n{ex.Message}",
                "Import layout",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Error);
            return;
        }

        ClearSelection();
        if (!_controller.TryImportLayoutJson(json, out var error))
        {
            WpfMessageBox.Show(
                error,
                "Import layout — invalid JSON",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Warning);
            return;
        }

        UpdateLearnButtons();
    }

    private void HideDeviceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_learnInProgress)
        {
            _controller.CancelLearn();
            _learnInProgress = false;
        }

        ClearSelection();
        _controller.HideSelectedDevice();
        UpdateLearnButtons();
    }

    private void RevealDeviceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _controller.RevealSelectedDevice();
        UpdateLearnButtons();
    }

    private void ResetAllBindingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_controller.SelectedDeviceName))
        {
            return;
        }

        var deviceName = _controller.SelectedDeviceName;
        var result = WpfMessageBox.Show(
            $"Clear all MIDI bindings for “{deviceName}”?\n\n" +
            "This removes discovered controls and Sonar channel assignments for this device (and its duplicate ports), then restores the factory map. Other devices are left alone.",
            "Reset all bindings",
            WpfMessageBoxButton.YesNo,
            WpfMessageBoxImage.Warning);

        if (result != WpfMessageBoxResult.Yes)
        {
            return;
        }

        if (_learnInProgress)
        {
            _controller.CancelLearn();
            _learnInProgress = false;
        }

        _controller.ClearBindingsForSelectedDevice();
        ClearSelection();
        UpdateLearnButtons();
    }

    private void BindingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBindingCombos || _selectedControl is null || !_controller.CanEditBindings)
        {
            return;
        }

        StageSelectedControlAssignment();
    }

    private void StageSelectedControlAssignment()
    {
        if (_selectedControl is null || !_controller.CanEditBindings)
        {
            return;
        }

        var channelId = ChannelCombo.SelectedItem is ComboBoxItem channelItem
            ? channelItem.Tag as string ?? MidiBinding.UnassignedChannelId
            : MidiBinding.UnassignedChannelId;
        var mode = ModeCombo.SelectedItem is ComboBoxItem { Tag: string modeTag }
                   && Enum.TryParse<MidiValueMode>(modeTag, out var parsedMode)
            ? parsedMode
            : MidiValueMode.Absolute;
        var action = ActionCombo.SelectedItem is ComboBoxItem { Tag: string actionTag }
                     && Enum.TryParse<MidiBindingAction>(actionTag, out var parsedAction)
            ? parsedAction
            : MidiBindingActions.DefaultFor(_selectedControl.Type);

        action = MidiBindingActions.Normalize(_selectedControl.Type, action);
        _controller.StageBindingAssignment(_selectedControl, channelId, mode, action);
    }

    private void RebuildActionCombo(MidiControlType type, MidiBindingAction selected)
    {
        var normalized = MidiBindingActions.Normalize(type, selected);
        ActionCombo.Items.Clear();
        ActionCombo.Items.Add(new ComboBoxItem { Content = "None", Tag = "None" });
        if (type == MidiControlType.Button)
        {
            ActionCombo.Items.Add(new ComboBoxItem { Content = "Mute toggle", Tag = "MuteToggle" });
        }
        else
        {
            ActionCombo.Items.Add(new ComboBoxItem { Content = "Volume", Tag = "Volume" });
        }

        ActionCombo.SelectedIndex = normalized == MidiBindingAction.None ? 0 : 1;
    }

    private async void SaveBindingDraftsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.CanEditBindings)
        {
            return;
        }

        if (_selectedControl is not null)
        {
            StageSelectedControlAssignment();
        }

        await _controller.SaveBindingDraftsAsync().ConfigureAwait(true);
        if (_selectedControl is not null)
        {
            SelectControl(_selectedControl);
        }
    }

    private void DiscardBindingDraftsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.HasUnsavedBindingDrafts)
        {
            return;
        }

        var result = WpfMessageBox.Show(
            "Discard all unsaved channel / mode / action assignments for this device?",
            "Discard assignments",
            WpfMessageBoxButton.YesNo,
            WpfMessageBoxImage.Question);
        if (result != WpfMessageBoxResult.Yes)
        {
            return;
        }

        ClearSelection();
        _controller.DiscardBindingDrafts();
        UpdateLearnButtons();
    }

    private void ClearBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedControl is null || _selectedControl.IsPlaceholder || !_controller.CanEditBindings)
        {
            return;
        }

        _controller.ClearBinding(_selectedControl);
        SelectControl(_selectedControl);
    }

    private Task<bool> ShowConflictOverlayAsync(IReadOnlyList<MidiBinding> conflicts)
    {
        var owners = string.Join(", ", conflicts.Select(c => $"{c.DeviceName} CC{c.Controller}"));
        ConflictMessageText.Text =
            $"Are you sure? Multiple non-motorized faders on one channel will cause volume fighting.\n\nExisting: {owners}";
        ConflictOverlay.Visibility = Visibility.Visible;

        _conflictTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _conflictTcs.Task;
    }

    private void ConflictCancel_Click(object sender, RoutedEventArgs e)
    {
        ConflictOverlay.Visibility = Visibility.Collapsed;
        _conflictTcs?.TrySetResult(false);
        _conflictTcs = null;
    }

    private void ConflictConfirm_Click(object sender, RoutedEventArgs e)
    {
        ConflictOverlay.Visibility = Visibility.Collapsed;
        _conflictTcs?.TrySetResult(true);
        _conflictTcs = null;
    }
}
