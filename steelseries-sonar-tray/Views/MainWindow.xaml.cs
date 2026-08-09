using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SonarQuickMixer.Audio;
using SonarQuickMixer.Midi;
using SonarQuickMixer.Mixing;
using SonarQuickMixer.Services;
using SonarQuickMixer.Settings;
using SonarQuickMixer.Sonar;
using SonarQuickMixer.Tray;
using SonarQuickMixer.Updates;

namespace SonarQuickMixer.Views;

public partial class MainWindow : Window
{
    private const double VolumeClickJumpThreshold = 2.0;
    private const int LevelPollIntervalMs = 33;
    private const int SettingsSyncIntervalMs = 1000;
    private const int BackgroundSyncIntervalMs = 5000;

    private bool _isShowingSettings;
    private bool _isSlideAnimating;
    private bool _isHiding;
    private bool _isViewTransitionAnimating;
    private double _anchorLeft;
    private double _anchorTop;
    private bool _suppressDeactivateHide;

    private readonly SonarApiClient _apiClient = new();
    private readonly SonarChannelLevelMonitor _levelMonitor = new();
    private readonly GitHubUpdateChecker _updateChecker = new();
    private readonly AppSettings _settings;
    private readonly MediaKeysOverrideService _mediaKeysOverride;
    private readonly MidiControlService? _midiControl;
    private readonly Action? _openMidiSetup;
    private readonly VolumeOverlayService _volumeOverlay;
    private readonly MixerControlRegistry _mixerRegistry = new();
    private readonly MixerSnapshotCoordinator _mixerSnapshot;
    private readonly VolumeSendCoordinator _volumeSend;
    private readonly AudioVisualizerCoordinator _audioVisualizer;
    private readonly OverlayLayoutController _overlayLayout;
    private readonly OverlayAnimationController _overlayAnimation;
    private readonly SettingsPanelController _settingsPanel;
    private readonly UpdateNotificationController _updateNotification;

    private readonly DispatcherTimer _levelPollTimer;
    private readonly DispatcherTimer _settingsSyncTimer;
    private readonly DispatcherTimer _backgroundSyncTimer;

    private bool _isVisibleForUser;
    private System.Drawing.Point? _anchorScreenPoint;

    public MainWindow(
        AppSettings settings,
        MediaKeysOverrideService mediaKeysOverride,
        DiscordScreenshareEchoFixService discordScreenshareEchoFix,
        VolumeOverlayService volumeOverlay,
        Action applyTrayIcon,
        MidiControlService? midiControl = null,
        Action? openMidiSetup = null)
    {
        _settings = settings;
        _mediaKeysOverride = mediaKeysOverride;
        _midiControl = midiControl;
        _openMidiSetup = openMidiSetup;
        _volumeOverlay = volumeOverlay;

        InitializeComponent();

        _overlayLayout = new OverlayLayoutController(
            this,
            MixerHeaderPanel,
            ChannelsPanel,
            ViewHeaderHost,
            ViewContentHost,
            MixerTabPanel,
            SettingsTabPanel);
        _overlayAnimation = new OverlayAnimationController(this, OverlayRoot);
        _settingsPanel = new SettingsPanelController(
            _settings,
            _mediaKeysOverride,
            discordScreenshareEchoFix,
            _volumeOverlay,
            applyTrayIcon,
            RunAtWindowsStartupToggle,
            MediaKeysOverrideToggle,
            VolumeOverlayToggle,
            DiscordEchoFixToggle,
            AudioVisualizerToggle,
            MediaKeysOverrideChannelCombo,
            MediaKeysOverrideChannelPanel,
            TrayIconStyleCombo,
            _midiControl,
            MidiEnabledToggle);
        _updateNotification = new UpdateNotificationController(
            _updateChecker,
            SettingsVersionText,
            UpdateNotificationDot,
            UpdateAvailablePanel,
            UpdateAvailableText,
            OpenSettingsButton);

        _mixerSnapshot = new MixerSnapshotCoordinator(_apiClient, _mixerRegistry, text => StatusText.Text = text);
        _audioVisualizer = new AudioVisualizerCoordinator(
            _mixerRegistry,
            _mixerSnapshot,
            _levelMonitor,
            () => _settingsPanel.AudioVisualizerEnabled);
        _volumeSend = new VolumeSendCoordinator(
            _apiClient,
            text => StatusText.Text = text,
            () => _mixerSnapshot.IsUpdatingFromApi,
            settings => _mixerSnapshot.ApplyChannelSettingsToUi(settings, onLevelsChanged: _audioVisualizer.RefreshAllSliderLevels),
            () => _mixerSnapshot.SyncSnapshotAsync(applyVolumes: false));

        RegisterMixerChannels();
        ShowMixerView(instant: true);

        _levelPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LevelPollIntervalMs) };
        _levelPollTimer.Tick += (_, _) => PollLevelsIfNeeded();

        _settingsSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SettingsSyncIntervalMs) };
        _settingsSyncTimer.Tick += SettingsSyncTimer_Tick;

        _backgroundSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BackgroundSyncIntervalMs) };
        _backgroundSyncTimer.Tick += BackgroundSyncTimer_Tick;
        _backgroundSyncTimer.Start();

        _settingsPanel.InitializeFromSettings();
        _mediaKeysOverride.MixerChanged += ExternalMixerChanged;
        if (_midiControl is not null)
        {
            _midiControl.MixerChanged += ExternalMixerChanged;
        }

        Closed += (_, _) =>
        {
            _mediaKeysOverride.MixerChanged -= ExternalMixerChanged;
            if (_midiControl is not null)
            {
                _midiControl.MixerChanged -= ExternalMixerChanged;
            }

            _settingsPanel.SyncFeatureSettingsFromUi();
            _settings.Save();
            _levelPollTimer.Stop();
            _settingsSyncTimer.Stop();
            _backgroundSyncTimer.Stop();
            _volumeSend.Stop();
            _levelMonitor.Dispose();
            _apiClient.Dispose();
            _updateChecker.Dispose();
        };

        foreach (var slider in _mixerRegistry.SliderBindings.Keys)
        {
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Slider_DragCompleted));
        }

        _mixerRegistry.UpdateDisplayedValues();
        _updateNotification.InitializeVersionText();
        _ = _updateNotification.CheckForUpdatesAsync();
    }

    private void RegisterMixerChannels()
    {
        _mixerRegistry.RegisterChannel(
            "master",
            MasterMonitorMuteToggle, MasterMonitorSlider, MasterMonitorValueText,
            MasterStreamMuteToggle, MasterStreamSlider, MasterStreamValueText,
            MasterMonitorMixIcon, MasterStreamRow);

        _mixerRegistry.RegisterChannel(
            "game",
            GameMonitorMuteToggle, GameMonitorSlider, GameMonitorValueText,
            GameStreamMuteToggle, GameStreamSlider, GameStreamValueText,
            null, GameStreamRow,
            GameMonitorMixToggle, GameStreamMixToggle);

        _mixerRegistry.RegisterChannel(
            "chatRender",
            ChatMonitorMuteToggle, ChatMonitorSlider, ChatMonitorValueText,
            ChatStreamMuteToggle, ChatStreamSlider, ChatStreamValueText,
            null, ChatStreamRow,
            ChatMonitorMixToggle, ChatStreamMixToggle);

        _mixerRegistry.RegisterChannel(
            "media",
            MediaMonitorMuteToggle, MediaMonitorSlider, MediaMonitorValueText,
            MediaStreamMuteToggle, MediaStreamSlider, MediaStreamValueText,
            null, MediaStreamRow,
            MediaMonitorMixToggle, MediaStreamMixToggle);

        _mixerRegistry.RegisterChannel(
            "aux",
            AuxMonitorMuteToggle, AuxMonitorSlider, AuxMonitorValueText,
            AuxStreamMuteToggle, AuxStreamSlider, AuxStreamValueText,
            null, AuxStreamRow,
            AuxMonitorMixToggle, AuxStreamMixToggle);

        _mixerRegistry.RegisterChannelSection("master", MasterChannelSection);
        _mixerRegistry.RegisterChannelSection("game", GameChannelSection);
        _mixerRegistry.RegisterChannelSection("chatRender", ChatChannelSection);
        _mixerRegistry.RegisterChannelSection("media", MediaChannelSection);
        _mixerRegistry.RegisterChannelSection("aux", AuxChannelSection);
    }

    public void ShowInstantly(System.Drawing.Point? anchorScreenPoint = null) =>
        _ = ShowInstantlyAsync(anchorScreenPoint);

    public async Task ShowInstantlyAsync(System.Drawing.Point? anchorScreenPoint = null)
    {
        if (_isHiding)
        {
            return;
        }

        _isVisibleForUser = true;
        _anchorScreenPoint = anchorScreenPoint;
        _suppressDeactivateHide = true;
        ShowMixerView(instant: true);

        Show();
        Visibility = Visibility.Visible;
        UpdateLayout();
        _overlayLayout.ReleaseOverlayHeight();
        _mixerSnapshot.ApplyCachedSnapshotIfAvailable();

        Topmost = true;
        CaptureAnchorPosition();
        Topmost = false;
        Topmost = true;

        _overlayAnimation.SetHiddenPose(_anchorLeft, _anchorTop);
        _mixerSnapshot.RestoreOrShowConnectingStatus();

        var snapshotTask = _mixerSnapshot.FetchSnapshotAsync();

        _isSlideAnimating = true;
        try
        {
            await _overlayAnimation.AnimateSlideAsync(
                _anchorTop,
                1,
                _overlayAnimation.ShowAnimationDurationMs,
                new CubicEase { EasingMode = EasingMode.EaseOut }).ConfigureAwait(true);
        }
        finally
        {
            _isSlideAnimating = false;
        }

        var snapshot = await snapshotTask.ConfigureAwait(true);
        if (snapshot is not null)
        {
            ApplySnapshotWithLayout(snapshot);
        }
        else if (_mixerSnapshot.HasCachedConnectionStatus())
        {
            _mixerSnapshot.RestoreOrShowConnectingStatus();
        }
        else
        {
            StatusText.Text = "Sonar API unavailable";
        }

        _settingsSyncTimer.Start();
        UpdateLevelPollTimer();

        Activate();
        Focus();
        _ = Dispatcher.BeginInvoke(new Action(() => _suppressDeactivateHide = false), DispatcherPriority.ApplicationIdle);
    }

    public async Task WarmupAsync()
    {
        await Dispatcher.InvokeAsync(WarmupVisualTree, DispatcherPriority.Background).Task.ConfigureAwait(false);

        try
        {
            if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
            {
                return;
            }

            var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
            if (!_apiClient.IsConnected)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => _mixerSnapshot.ApplySnapshot(snapshot)).Task.ConfigureAwait(false);
        }
        catch
        {
            // Warmup is best-effort.
        }
    }

    public void HideInstantly() => _ = HideAnimatedAsync();

    public async Task HideAnimatedAsync()
    {
        if (!_isVisibleForUser || _isHiding || _isSlideAnimating)
        {
            return;
        }

        _isHiding = true;
        _levelPollTimer.Stop();
        _settingsSyncTimer.Stop();

        _isSlideAnimating = true;
        try
        {
            await _overlayAnimation.AnimateSlideAsync(
                _anchorTop + _overlayAnimation.SlideDistance,
                0,
                _overlayAnimation.HideAnimationDurationMs,
                new CubicEase { EasingMode = EasingMode.EaseIn }).ConfigureAwait(true);
        }
        finally
        {
            _isSlideAnimating = false;
            _isHiding = false;
            FinishHide();
        }
    }

    private void FinishHide()
    {
        _isVisibleForUser = false;
        _suppressDeactivateHide = false;
        _audioVisualizer.ResetLevelMeters();
        ShowMixerView(instant: true);
        _overlayLayout.ReleaseOverlayHeight();
        _overlayAnimation.SetHiddenPose(_anchorLeft, _anchorTop);
        Visibility = Visibility.Collapsed;
    }

    private void WarmupVisualTree()
    {
        Left = -10000;
        Top = -10000;
        Opacity = 0;
        Show();
        UpdateLayout();
        InvalidateVisual();
        Visibility = Visibility.Collapsed;
        Opacity = 1;
    }

    private void CaptureAnchorPosition()
    {
        UpdateLayout();
        TrayWindowPlacement.PlaceAboveTaskbar(this, _anchorScreenPoint);
        _anchorLeft = Left;
        _anchorTop = Top;
    }

    private void ApplySnapshotWithLayout(SonarMixerSnapshot snapshot)
    {
        _mixerSnapshot.ApplySnapshot(snapshot);
        if (_isVisibleForUser)
        {
            _overlayLayout.LockOverlayHeight();
            if (!_isSlideAnimating && !_isViewTransitionAnimating)
            {
                RepositionOverlay();
            }
        }
    }

    private void RepositionOverlay()
    {
        if (_isSlideAnimating)
        {
            return;
        }

        CaptureAnchorPosition();

        if (_isVisibleForUser && Visibility == Visibility.Visible)
        {
            Left = _anchorLeft;
            Top = _anchorTop;
        }
    }

    private void PollLevelsIfNeeded()
    {
        if (!_isVisibleForUser || _isSlideAnimating || !_settingsPanel.AudioVisualizerEnabled)
        {
            return;
        }

        _audioVisualizer.PollAndRefreshLevels();
    }

    private async void SettingsSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isVisibleForUser || _mixerSnapshot.IsUpdatingFromApi || _mixerSnapshot.IsSyncInProgress)
        {
            return;
        }

        try
        {
            var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(true);
            if (!_apiClient.IsConnected)
            {
                return;
            }

            _mixerSnapshot.ApplySnapshot(snapshot, applyVolumes: !IsUserAdjustingMixer());
            if (_isVisibleForUser)
            {
                _overlayLayout.LockOverlayHeight();
            }
        }
        catch (Exception)
        {
            // Ignore transient sync errors while the overlay is open.
        }
    }

    private async void BackgroundSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (_isVisibleForUser || _mixerSnapshot.IsUpdatingFromApi || _mixerSnapshot.IsSyncInProgress)
        {
            return;
        }

        try
        {
            if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
            {
                return;
            }

            var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
            if (!_apiClient.IsConnected)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => _mixerSnapshot.ApplySnapshot(snapshot)).Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Warm background cache refresh is best-effort.
        }
    }

    private bool IsUserAdjustingMixer()
    {
        if (_volumeSend.IsSendInProgress)
        {
            return true;
        }

        foreach (var slider in _mixerRegistry.SliderBindings.Keys)
        {
            if (slider.IsMouseCaptureWithin)
            {
                return true;
            }
        }

        return false;
    }

    private void ChannelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mixerSnapshot.IsUpdatingFromApi || sender is not Slider slider)
        {
            return;
        }

        if (!_mixerRegistry.SliderBindings.TryGetValue(slider, out var binding))
        {
            return;
        }

        if (_mixerRegistry.SliderValueLabels.TryGetValue(slider, out var label))
        {
            label.Text = $"{slider.Value:0}%";
        }

        if (string.Equals(binding.Channel, "master", StringComparison.OrdinalIgnoreCase))
        {
            _mixerSnapshot.ApplyProportionalChannelsUi(binding.Path, e.OldValue, e.NewValue);
        }

        _volumeSend.QueueVolumeSend(
            binding.Channel,
            binding.Path,
            (float)(slider.Value / 100d),
            Math.Abs(e.NewValue - e.OldValue) >= VolumeClickJumpThreshold);

        _audioVisualizer.RefreshAllSliderLevels();
    }

    private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_mixerSnapshot.IsUpdatingFromApi || sender is not Slider slider)
        {
            return;
        }

        if (!_mixerRegistry.SliderBindings.TryGetValue(slider, out var binding))
        {
            return;
        }

        _volumeSend.QueueVolumeSend(binding.Channel, binding.Path, (float)(slider.Value / 100d), forceImmediate: true);
    }

    private async void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_mixerSnapshot.IsUpdatingFromApi || sender is not ToggleButton muteToggle)
        {
            return;
        }

        if (!_mixerRegistry.MuteBindings.TryGetValue(muteToggle, out var binding))
        {
            return;
        }

        var muted = muteToggle.IsChecked == true;
        var linkedSlider = _mixerRegistry.FindSliderForMuteToggle(muteToggle);
        if (linkedSlider is not null)
        {
            _mixerRegistry.UpdateSliderVisual(linkedSlider);
        }

        try
        {
            var updatedSettings = await _apiClient.SetMuteAsync(binding.Channel, muted, binding.Path).ConfigureAwait(true);
            if (updatedSettings is null)
            {
                muteToggle.IsChecked = !muted;
                if (linkedSlider is not null)
                {
                    _mixerRegistry.UpdateSliderVisual(linkedSlider);
                }

                StatusText.Text = "Failed to update mute";
                return;
            }

            _mixerSnapshot.ApplyChannelSettingsToUi(updatedSettings, onLevelsChanged: _audioVisualizer.RefreshAllSliderLevels);
            await _mixerSnapshot.SyncSnapshotAsync(applyVolumes: false).ConfigureAwait(true);
        }
        catch (Exception)
        {
            muteToggle.IsChecked = !muted;
            if (linkedSlider is not null)
            {
                _mixerRegistry.UpdateSliderVisual(linkedSlider);
            }

            StatusText.Text = "Failed to update mute";
        }
    }

    private async void MixToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_mixerSnapshot.IsUpdatingFromApi || sender is not ToggleButton mixToggle)
        {
            return;
        }

        if (!_mixerRegistry.MixBindings.TryGetValue(mixToggle, out var binding))
        {
            return;
        }

        var included = mixToggle.IsChecked == true;
        var linkedSlider = _mixerRegistry.FindSliderForMixToggle(mixToggle);
        if (linkedSlider is not null)
        {
            _mixerRegistry.UpdateSliderVisual(linkedSlider);
            _audioVisualizer.RefreshAllSliderLevels();
        }

        try
        {
            var updatedSettings = await _apiClient
                .SetMixIncludedAsync(binding.Channel, included, binding.Path)
                .ConfigureAwait(true);

            if (updatedSettings is null)
            {
                mixToggle.IsChecked = !included;
                if (linkedSlider is not null)
                {
                    _mixerRegistry.UpdateSliderVisual(linkedSlider);
                    _audioVisualizer.RefreshAllSliderLevels();
                }

                StatusText.Text = "Failed to update mix routing";
                return;
            }

            _mixerSnapshot.ApplyChannelSettingsToUi(updatedSettings, onLevelsChanged: _audioVisualizer.RefreshAllSliderLevels);
            await _mixerSnapshot.SyncSnapshotAsync(applyVolumes: false).ConfigureAwait(true);
        }
        catch (Exception)
        {
            mixToggle.IsChecked = !included;
            if (linkedSlider is not null)
            {
                _mixerRegistry.UpdateSliderVisual(linkedSlider);
                _audioVisualizer.RefreshAllSliderLevels();
            }

            StatusText.Text = "Failed to update mix routing";
        }
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => _ = ShowSettingsViewAsync();

    private void OpenMidiSetupButton_Click(object sender, RoutedEventArgs e)
    {
        _openMidiSetup?.Invoke();
    }
    private void UpdateNotificationDot_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; _updateNotification.OpenReleasePage(); }
    private void OpenReleaseButton_Click(object sender, RoutedEventArgs e) => _updateNotification.OpenReleasePage();
    private void BackToMixerButton_Click(object sender, RoutedEventArgs e) => _ = ShowMixerViewAsync();

    private void ShowMixerView(bool instant = false)
    {
        if (instant || !_isVisibleForUser || _isViewTransitionAnimating)
        {
            ApplyMixerViewInstant();
            return;
        }

        _ = ShowMixerViewAsync();
    }

    private Task ShowMixerViewAsync() => TransitionBetweenViewsAsync(showSettings: false);
    private Task ShowSettingsViewAsync() => TransitionBetweenViewsAsync(showSettings: true);

    private async Task TransitionBetweenViewsAsync(bool showSettings)
    {
        if (_isViewTransitionAnimating || _isShowingSettings == showSettings)
        {
            return;
        }

        _overlayLayout.LockOverlayHeight();
        PrepareIncomingViewLayout(showSettings);

        var slideDistance = _overlayAnimation.GetViewSlideDistance(ViewContentHost, ActualWidth);

        _isViewTransitionAnimating = true;
        OpenSettingsButton.IsEnabled = false;
        BackToMixerButton.IsEnabled = false;

        var outgoingHeader = showSettings ? MixerHeaderPanel : SettingsHeaderPanel;
        var incomingHeader = showSettings ? SettingsHeaderPanel : MixerHeaderPanel;
        var outgoingContent = (FrameworkElement)(showSettings ? MixerTabPanel : SettingsTabPanel);
        var incomingContent = (FrameworkElement)(showSettings ? SettingsTabPanel : MixerTabPanel);
        var incomingStart = showSettings ? slideDistance : -slideDistance;
        var outgoingEnd = showSettings ? -slideDistance : slideDistance;

        System.Windows.Controls.Panel.SetZIndex(incomingHeader, 1);
        System.Windows.Controls.Panel.SetZIndex(incomingContent, 1);
        System.Windows.Controls.Panel.SetZIndex(outgoingHeader, 0);
        System.Windows.Controls.Panel.SetZIndex(outgoingContent, 0);

        incomingHeader.Visibility = Visibility.Visible;
        incomingContent.Visibility = Visibility.Visible;
        outgoingHeader.Visibility = Visibility.Visible;
        outgoingContent.Visibility = Visibility.Visible;

        _overlayAnimation.SetViewSlideState(incomingHeader, incomingStart);
        _overlayAnimation.SetViewSlideState(incomingContent, incomingStart);
        _overlayAnimation.SetViewSlideState(outgoingHeader, 0);
        _overlayAnimation.SetViewSlideState(outgoingContent, 0);

        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        try
        {
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render).Task.ConfigureAwait(true);

            await Task.WhenAll(
                _overlayAnimation.AnimateViewSlideAsync(outgoingHeader, outgoingEnd, _overlayAnimation.ViewTransitionDurationMs, easing),
                _overlayAnimation.AnimateViewSlideAsync(outgoingContent, outgoingEnd, _overlayAnimation.ViewTransitionDurationMs, easing),
                _overlayAnimation.AnimateViewSlideAsync(incomingHeader, 0, _overlayAnimation.ViewTransitionDurationMs, easing),
                _overlayAnimation.AnimateViewSlideAsync(incomingContent, 0, _overlayAnimation.ViewTransitionDurationMs, easing)).ConfigureAwait(true);
        }
        finally
        {
            if (showSettings)
            {
                ApplySettingsViewInstant();
            }
            else
            {
                ApplyMixerViewInstant();
            }

            _isViewTransitionAnimating = false;
            OpenSettingsButton.IsEnabled = true;
            BackToMixerButton.IsEnabled = true;
            ApplyViewState();
        }
    }

    private void ApplyMixerViewInstant()
    {
        _isShowingSettings = false;
        MixerHeaderPanel.Visibility = Visibility.Visible;
        SettingsHeaderPanel.Visibility = Visibility.Collapsed;
        MixerTabPanel.Visibility = Visibility.Visible;
        SettingsTabPanel.Visibility = Visibility.Collapsed;
        _overlayAnimation.ResetViewSlideState(MixerHeaderPanel);
        _overlayAnimation.ResetViewSlideState(MixerTabPanel);
        _overlayAnimation.ResetViewSlideState(SettingsHeaderPanel);
        _overlayAnimation.ResetViewSlideState(SettingsTabPanel);
        _overlayAnimation.ResetViewZOrder(MixerHeaderPanel, SettingsHeaderPanel, MixerTabPanel, SettingsTabPanel);
        ApplyViewState();
    }

    private void ApplySettingsViewInstant()
    {
        _isShowingSettings = true;
        MixerHeaderPanel.Visibility = Visibility.Collapsed;
        SettingsHeaderPanel.Visibility = Visibility.Visible;
        MixerTabPanel.Visibility = Visibility.Collapsed;
        SettingsTabPanel.Visibility = Visibility.Visible;
        _overlayAnimation.ResetViewSlideState(MixerHeaderPanel);
        _overlayAnimation.ResetViewSlideState(MixerTabPanel);
        _overlayAnimation.ResetViewSlideState(SettingsHeaderPanel);
        _overlayAnimation.ResetViewSlideState(SettingsTabPanel);
        _overlayAnimation.ResetViewZOrder(MixerHeaderPanel, SettingsHeaderPanel, MixerTabPanel, SettingsTabPanel);
        ApplyViewState();
    }

    private void PrepareIncomingViewLayout(bool showSettings)
    {
        if (showSettings)
        {
            SettingsTabPanel.Visibility = Visibility.Visible;
            SettingsTabPanel.ScrollToVerticalOffset(0);
        }
        else
        {
            MixerTabPanel.Visibility = Visibility.Visible;
        }

        UpdateLayout();
    }

    private void ApplyViewState()
    {
        if (!_isVisibleForUser)
        {
            return;
        }

        if (_isShowingSettings)
        {
            _levelPollTimer.Stop();
            _audioVisualizer.ResetLevelMeters();
        }
        else if (!_isSlideAnimating)
        {
            UpdateLevelPollTimer();
            if (_settingsPanel.AudioVisualizerEnabled)
            {
                _audioVisualizer.PollAndRefreshLevels();
            }
        }

        if (!_isViewTransitionAnimating)
        {
            RepositionOverlay();
        }
    }

    private void FeatureToggle_Changed(object sender, RoutedEventArgs e) =>
        _settingsPanel.OnFeatureToggleChanged(sender, e, ApplyAudioVisualizerState);

    private void ApplyAudioVisualizerState()
    {
        if (!_settingsPanel.AudioVisualizerEnabled)
        {
            _levelPollTimer.Stop();
            _audioVisualizer.ResetLevelMeters();
            _audioVisualizer.ClearCachedLevels();
            _audioVisualizer.Suspend();
            return;
        }

        _audioVisualizer.RefreshDevices();
        if (_isVisibleForUser && !_isShowingSettings && !_isSlideAnimating)
        {
            _audioVisualizer.PollAndRefreshLevels();
        }

        UpdateLevelPollTimer();
    }

    private void MediaKeysOverrideChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _settingsPanel.OnMediaKeysOverrideChannelChanged();

    private void TrayIconStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _settingsPanel.OnTrayIconStyleChanged();

    private void ExternalMixerChanged()
    {
        if (!_isVisibleForUser || IsUserAdjustingMixer())
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!_isVisibleForUser || IsUserAdjustingMixer())
            {
                return;
            }

            try
            {
                await _mixerSnapshot.SyncSnapshotAsync().ConfigureAwait(true);
            }
            catch
            {
                // Ignore transient sync errors from media key / MIDI updates.
            }
        }));
    }

    private void UpdateLevelPollTimer()
    {
        _levelPollTimer.Stop();

        if (_settingsPanel.AudioVisualizerEnabled
            && _isVisibleForUser
            && !_isShowingSettings
            && !_isSlideAnimating)
        {
            _levelPollTimer.Start();
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_isVisibleForUser || _isHiding || _isSlideAnimating || _suppressDeactivateHide)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isVisibleForUser || _isHiding || _isSlideAnimating || _suppressDeactivateHide)
            {
                return;
            }

            _ = HideAnimatedAsync();
        }, DispatcherPriority.Input);
    }
}
