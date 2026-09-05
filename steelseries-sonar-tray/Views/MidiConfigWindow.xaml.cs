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
    private readonly MidiConfigController _controller;
    private BlueprintControlVm? _selectedControl;
    private BlueprintRegionVm? _selectedRegion;
    private TaskCompletionSource<bool>? _conflictTcs;
    private bool _learnInProgress;
    private bool _syncingDeviceList;
    private bool _syncingBindingCombos;

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
