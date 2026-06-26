using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SonarQuickMixer.Audio;
using SonarQuickMixer.Controls;

namespace SonarQuickMixer;

public partial class MainWindow : Window
{
    private const int VolumeThrottleMs = 16;
    private const double VolumeClickJumpThreshold = 2.0;
    private const int LevelPollIntervalMs = 33;
    private const double VisualizerDisplayGain = 1.45;
    private const int SettingsSyncIntervalMs = 1000;
    private const double SlideDistanceDip = 24;
    private const int ShowAnimationMs = 240;
    private const int HideAnimationMs = 180;
    private const int ViewTransitionMs = 220;

    private static readonly string[] MasterProportionalChannels = SonarChannels.MasterProportional;

    private bool _isShowingSettings;
    private bool _isSlideAnimating;
    private bool _isHiding;
    private bool _isViewTransitionAnimating;
    private double _anchorLeft;
    private double _anchorTop;
    private double? _lockedOverlayHeight;
    private bool _suppressDeactivateHide;

    private readonly SonarApiClient _apiClient = new();
    private readonly SonarChannelLevelMonitor _levelMonitor = new();
    private readonly AppSettings _settings;
    private readonly MediaKeysOverrideService _mediaKeysOverride;
    private readonly VolumeOverlayService _volumeOverlay;
    private readonly Action _applyTrayIcon;
    private readonly DispatcherTimer _volumeThrottleTimer;
    private readonly DispatcherTimer _levelPollTimer;
    private readonly DispatcherTimer _settingsSyncTimer;
    private readonly Dictionary<Slider, (string Channel, SonarMixerPath Path)> _sliderBindings = new();
    private readonly Dictionary<Slider, TextBlock> _sliderValueLabels = new();
    private readonly Dictionary<ToggleButton, (string Channel, SonarMixerPath Path)> _muteBindings = new();
    private readonly Dictionary<ToggleButton, (string Channel, SonarMixerPath Path)> _mixBindings = new();
    private readonly Dictionary<Slider, ToggleButton> _sliderMuteToggles = new();
    private readonly Dictionary<Slider, ToggleButton> _sliderMixToggles = new();
    private readonly Dictionary<string, List<Slider>> _channelSliders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _channelSections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _lastRawChannelLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FrameworkElement> _streamerOnlyElements = [];
    private readonly HashSet<string> _enabledChannels = new(StringComparer.OrdinalIgnoreCase);

    private bool _isUpdatingFromApi;
    private bool _isVisibleForUser;
    private System.Drawing.Point? _anchorScreenPoint;
    private bool _volumeSendInProgress;
    private bool _volumeResendPending;
    private string? _pendingVolumeChannel;
    private SonarMixerPath _pendingVolumePath;
    private float _pendingVolume;
    private DateTime _lastVolumeSendUtc = DateTime.MinValue;
    private string? _cachedStatusText;
    private double? _lockedHeaderHostHeight;
    private bool _suppressMediaKeysChannelChange;
    private bool _suppressFeatureToggleChanges;
    private bool _suppressTrayIconStyleChange;

    public MainWindow(
        AppSettings settings,
        MediaKeysOverrideService mediaKeysOverride,
        VolumeOverlayService volumeOverlay,
        Action applyTrayIcon)
    {
        _settings = settings;
        _mediaKeysOverride = mediaKeysOverride;
        _volumeOverlay = volumeOverlay;
        _applyTrayIcon = applyTrayIcon;
        _mediaKeysOverride.MixerChanged += MediaKeysOverride_MixerChanged;
        InitializeComponent();

        RegisterChannel(
            "master",
            MasterMonitorMuteToggle,
            MasterMonitorSlider,
            MasterMonitorValueText,
            MasterStreamMuteToggle,
            MasterStreamSlider,
            MasterStreamValueText,
            MasterMonitorMixIcon,
            MasterStreamRow);

        RegisterChannel(
            "game",
            GameMonitorMuteToggle,
            GameMonitorSlider,
            GameMonitorValueText,
            GameStreamMuteToggle,
            GameStreamSlider,
            GameStreamValueText,
            null,
            GameStreamRow,
            GameMonitorMixToggle,
            GameStreamMixToggle);

        RegisterChannel(
            "chatRender",
            ChatMonitorMuteToggle,
            ChatMonitorSlider,
            ChatMonitorValueText,
            ChatStreamMuteToggle,
            ChatStreamSlider,
            ChatStreamValueText,
            null,
            ChatStreamRow,
            ChatMonitorMixToggle,
            ChatStreamMixToggle);

        RegisterChannel(
            "media",
            MediaMonitorMuteToggle,
            MediaMonitorSlider,
            MediaMonitorValueText,
            MediaStreamMuteToggle,
            MediaStreamSlider,
            MediaStreamValueText,
            null,
            MediaStreamRow,
            MediaMonitorMixToggle,
            MediaStreamMixToggle);

        RegisterChannel(
            "aux",
            AuxMonitorMuteToggle,
            AuxMonitorSlider,
            AuxMonitorValueText,
            AuxStreamMuteToggle,
            AuxStreamSlider,
            AuxStreamValueText,
            null,
            AuxStreamRow,
            AuxMonitorMixToggle,
            AuxStreamMixToggle);

        _channelSections["master"] = MasterChannelSection;
        _channelSections["game"] = GameChannelSection;
        _channelSections["chatRender"] = ChatChannelSection;
        _channelSections["media"] = MediaChannelSection;
        _channelSections["aux"] = AuxChannelSection;

        ShowMixerView(instant: true);

        _volumeThrottleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(VolumeThrottleMs)
        };
        _volumeThrottleTimer.Tick += VolumeThrottleTimer_Tick;

        _levelPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LevelPollIntervalMs)
        };
        _levelPollTimer.Tick += LevelPollTimer_Tick;

        _settingsSyncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SettingsSyncIntervalMs)
        };
        _settingsSyncTimer.Tick += SettingsSyncTimer_Tick;

        _suppressFeatureToggleChanges = true;
        try
        {
            RunAtWindowsStartupToggle.IsChecked = _settings.RunAtWindowsStartup;
            MediaKeysOverrideToggle.IsChecked = _settings.MediaKeysOverride;
            VolumeOverlayToggle.IsChecked = _settings.VolumeOverlayEnabled;
            DiscordEchoFixToggle.IsChecked = _settings.DiscordScreenshareEchoFix;
            AudioVisualizerToggle.IsChecked = _settings.AudioVisualizerEnabled;
        }
        finally
        {
            _suppressFeatureToggleChanges = false;
        }

        PopulateMediaKeysOverrideChannelCombo();
        SelectMediaKeysOverrideChannel(_settings.MediaKeysOverrideChannel);
        PopulateTrayIconStyleCombo();
        SelectTrayIconStyle(_settings.TrayIconStyle);
        ApplyMediaKeysOverrideSettings();
        ApplyAudioVisualizerState();

        Closed += (_, _) =>
        {
            _mediaKeysOverride.MixerChanged -= MediaKeysOverride_MixerChanged;
            SyncFeatureSettingsFromUi();
            _settings.Save();
            _levelPollTimer.Stop();
            _settingsSyncTimer.Stop();
            _levelMonitor.Dispose();
            _apiClient.Dispose();
        };

        foreach (var slider in _sliderBindings.Keys)
        {
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Slider_DragCompleted));
        }

        UpdateDisplayedValues();
    }

    private void RegisterChannel(
        string channel,
        ToggleButton monitorMuteToggle,
        Slider monitorSlider,
        TextBlock monitorValueLabel,
        ToggleButton streamMuteToggle,
        Slider streamSlider,
        TextBlock streamValueLabel,
        FrameworkElement? streamerMonitorIndicator,
        FrameworkElement streamRow,
        ToggleButton? monitorMixToggle = null,
        ToggleButton? streamMixToggle = null)
    {
        RegisterMixerRow(channel, SonarMixerPath.Monitoring, monitorMuteToggle, monitorSlider, monitorValueLabel, monitorMixToggle);
        RegisterMixerRow(channel, SonarMixerPath.Streaming, streamMuteToggle, streamSlider, streamValueLabel, streamMixToggle);
        if (streamerMonitorIndicator is not null)
        {
            _streamerOnlyElements.Add(streamerMonitorIndicator);
        }

        _streamerOnlyElements.Add(streamRow);
    }

    private void RegisterMixerRow(
        string channel,
        SonarMixerPath path,
        ToggleButton muteToggle,
        Slider slider,
        TextBlock valueLabel,
        ToggleButton? mixToggle = null)
    {
        _muteBindings[muteToggle] = (channel, path);
        _sliderBindings[slider] = (channel, path);
        _sliderValueLabels[slider] = valueLabel;
        _sliderMuteToggles[slider] = muteToggle;

        if (mixToggle is not null)
        {
            _mixBindings[mixToggle] = (channel, path);
            _sliderMixToggles[slider] = mixToggle;
            _streamerOnlyElements.Add(mixToggle);
        }

        RegisterChannelSlider(channel, slider);
    }

    private void RegisterChannelSlider(string channel, Slider slider)
    {
        if (!_channelSliders.TryGetValue(channel, out var sliders))
        {
            sliders = [];
            _channelSliders[channel] = sliders;
        }

        sliders.Add(slider);
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

        Topmost = true;
        CaptureAnchorPosition();
        Topmost = false;
        Topmost = true;

        ClearSlideAnimations();
        Left = _anchorLeft;
        Top = _anchorTop + SlideDistanceDip;
        OverlayRoot.Opacity = 0;
        RestoreOrShowConnectingStatus();

        var snapshotTask = FetchMixerSnapshotAsync();

        _isSlideAnimating = true;
        try
        {
            await AnimateSlideAsync(
                toTop: _anchorTop,
                toOpacity: 1,
                durationMs: ShowAnimationMs,
                easing: new CubicEase { EasingMode = EasingMode.EaseOut }).ConfigureAwait(true);
        }
        finally
        {
            _isSlideAnimating = false;
        }

        var snapshot = await snapshotTask.ConfigureAwait(true);
        if (snapshot is not null)
        {
            ApplyMixerSnapshot(snapshot);
        }
        else if (HasCachedConnectionStatus())
        {
            StatusText.Text = _cachedStatusText!;
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

            var statusText = BuildStatusText(snapshot);
            await Dispatcher.InvokeAsync(() => _cachedStatusText = statusText).Task.ConfigureAwait(false);
        }
        catch
        {
            // Warmup is best-effort.
        }
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

    private async Task<SonarMixerSnapshot?> FetchMixerSnapshotAsync()
    {
        try
        {
            if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(true))
            {
                return null;
            }

            if (_settings.AudioVisualizerEnabled)
            {
                _levelMonitor.RefreshDevices();
            }

            return await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
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
            await AnimateSlideAsync(
                toTop: _anchorTop + SlideDistanceDip,
                toOpacity: 0,
                durationMs: HideAnimationMs,
                easing: new CubicEase { EasingMode = EasingMode.EaseIn }).ConfigureAwait(true);
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
        ResetLevelMeters();
        ShowMixerView(instant: true);
        ReleaseOverlayHeight();
        ClearSlideAnimations();
        Left = _anchorLeft;
        Top = _anchorTop + SlideDistanceDip;
        OverlayRoot.Opacity = 0;
        Visibility = Visibility.Collapsed;
    }

    private void CaptureAnchorPosition()
    {
        UpdateLayout();
        TrayWindowPlacement.PlaceAboveTaskbar(this, _anchorScreenPoint);
        _anchorLeft = Left;
        _anchorTop = Top;
    }

    private double MeasureMixerLayoutHeight()
    {
        UpdateLayout();

        var contentWidth = Math.Max(ActualWidth, Width) - 34;
        if (contentWidth < 200)
        {
            contentWidth = 370;
        }

        MixerHeaderPanel.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));
        ChannelsPanel.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));

        const double chromeHeight = 46;
        return chromeHeight + MixerHeaderPanel.DesiredSize.Height + ChannelsPanel.DesiredSize.Height;
    }

    private void LockOverlayHeight()
    {
        var measuredHeight = Math.Clamp(MeasureMixerLayoutHeight(), MinHeight, MaxHeight);
        if (_lockedOverlayHeight is null || measuredHeight > _lockedOverlayHeight)
        {
            _lockedOverlayHeight = measuredHeight;
        }

        ApplyLockedOverlaySize();
    }

    private void ApplyLockedOverlaySize()
    {
        if (_lockedOverlayHeight is not double height)
        {
            return;
        }

        SizeToContent = SizeToContent.Manual;
        Height = height;
        MinHeight = height;
        MaxHeight = height;

        UpdateLayout();
        var headerHeight = ResolveLockedHeaderHostHeight();
        ViewHeaderHost.MinHeight = headerHeight;
        var contentAreaHeight = Math.Max(0, height - headerHeight - 58);
        MixerTabPanel.MinHeight = contentAreaHeight;
        SettingsTabPanel.MinHeight = contentAreaHeight;
    }

    private double ResolveLockedHeaderHostHeight()
    {
        var headerHeight = MixerHeaderPanel.ActualHeight;
        if (headerHeight < 1)
        {
            var contentWidth = Math.Max(ActualWidth, Width) - 34;
            if (contentWidth < 200)
            {
                contentWidth = 370;
            }

            MixerHeaderPanel.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));
            headerHeight = MixerHeaderPanel.DesiredSize.Height;
        }

        if (_lockedHeaderHostHeight is null || headerHeight > _lockedHeaderHostHeight)
        {
            _lockedHeaderHostHeight = headerHeight;
        }

        return _lockedHeaderHostHeight.Value;
    }

    private void ReleaseOverlayHeight()
    {
        _lockedOverlayHeight = null;
        _lockedHeaderHostHeight = null;
        SizeToContent = SizeToContent.Height;
        MinHeight = 200;
        MaxHeight = 700;
        ViewHeaderHost.MinHeight = 0;
        MixerTabPanel.MinHeight = 0;
        SettingsTabPanel.MinHeight = 0;
    }

    private void ClearSlideAnimations()
    {
        BeginAnimation(TopProperty, null);
        OverlayRoot.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private Task AnimateSlideAsync(double toTop, double toOpacity, int durationMs, IEasingFunction easing)
    {
        ClearSlideAnimations();
        OverlayRoot.CacheMode = new BitmapCache(1.0);

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var fromTop = Top;
        var fromOpacity = OverlayRoot.Opacity;

        var topAnimation = new DoubleAnimation
        {
            From = fromTop,
            To = toTop,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        Storyboard.SetTarget(topAnimation, this);
        Storyboard.SetTargetProperty(topAnimation, new PropertyPath(TopProperty));

        var opacityAnimation = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        Storyboard.SetTarget(opacityAnimation, OverlayRoot);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.Stop,
            Children = { topAnimation, opacityAnimation }
        };

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storyboard.Completed += (_, _) =>
        {
            Top = toTop;
            OverlayRoot.Opacity = toOpacity;
            OverlayRoot.CacheMode = null;
            completion.TrySetResult();
        };
        storyboard.Begin();
        return completion.Task;
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

    private void LevelPollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isVisibleForUser || _isSlideAnimating || !_settings.AudioVisualizerEnabled)
        {
            return;
        }

        PollAndRefreshLevels();
    }

    private void PollAndRefreshLevels()
    {
        var levels = _levelMonitor.PollLevels();

        foreach (var (channel, level) in levels)
        {
            _lastRawChannelLevels[channel] = level;
        }

        RefreshAllSliderLevels();
    }

    private async void SettingsSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isVisibleForUser || _isUpdatingFromApi)
        {
            return;
        }

        try
        {
            if (_settings.AudioVisualizerEnabled)
            {
                _levelMonitor.RefreshDevices();
            }

            var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(true);
            ApplyMixerSnapshot(snapshot, applyVolumes: !IsUserAdjustingMixer());
        }
        catch (Exception)
        {
            // Ignore transient sync errors while the overlay is open.
        }
    }

    private bool IsChannelEnabled(string channel) => _enabledChannels.Contains(channel);

    private IEnumerable<string> GetActiveProportionalChannels()
    {
        foreach (var channel in MasterProportionalChannels)
        {
            if (IsChannelEnabled(channel))
            {
                yield return channel;
            }
        }
    }

    private bool IsUserAdjustingMixer()
    {
        if (_volumeSendInProgress)
        {
            return true;
        }

        foreach (var slider in _sliderBindings.Keys)
        {
            if (slider.IsMouseCaptureWithin)
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshAllSliderLevels()
    {
        foreach (var (channel, sliders) in _channelSliders)
        {
            if (!IsChannelEnabled(channel))
            {
                continue;
            }

            if (string.Equals(channel, "master", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawLevel = _lastRawChannelLevels.TryGetValue(channel, out var value) ? value : 0d;
            foreach (var slider in sliders)
            {
                ApplyCappedLevel(slider, rawLevel);
            }
        }

        if (!_channelSliders.TryGetValue("master", out var masterSliders))
        {
            return;
        }

        foreach (var masterSlider in masterSliders)
        {
            if (!_sliderBindings.TryGetValue(masterSlider, out var binding))
            {
                continue;
            }

            var mixPeak = ComputeMixPeak(binding.Path);
            ApplyCappedLevel(masterSlider, mixPeak);
        }
    }

    private double ComputeMixPeak(SonarMixerPath path)
    {
        var peak = 0d;

        foreach (var channel in GetActiveProportionalChannels())
        {
            var rawLevel = _lastRawChannelLevels.TryGetValue(channel, out var value) ? value : 0d;
            var channelSlider = FindSlider(channel, path);
            if (channelSlider is null)
            {
                continue;
            }

            if (IsSliderMuted(channelSlider) || IsSliderMixExcluded(channelSlider))
            {
                continue;
            }

            var volumeFactor = channelSlider.Value / 100d;
            peak = Math.Max(peak, rawLevel * volumeFactor);
        }

        return peak;
    }

    private void ApplyCappedLevel(Slider slider, double rawLevel)
    {
        if (!_settings.AudioVisualizerEnabled)
        {
            SliderLevelProperties.SetLevel(slider, 0);
            return;
        }

        if (IsSliderMuted(slider) || IsSliderMixExcluded(slider))
        {
            SliderLevelProperties.SetLevel(slider, 0);
            return;
        }

        var volumeFactor = slider.Value / 100d;
        SliderLevelProperties.SetLevel(slider, MapVisualizerLevel(rawLevel, volumeFactor));
    }

    private static double MapVisualizerLevel(double rawLevel, double volumeFactor) =>
        Math.Min(rawLevel * VisualizerDisplayGain, 1d) * volumeFactor;

    private bool IsSliderMuted(Slider slider) =>
        _sliderMuteToggles.TryGetValue(slider, out var muteToggle) && muteToggle.IsChecked == true;

    private bool IsSliderMixExcluded(Slider slider) =>
        _sliderMixToggles.TryGetValue(slider, out var mixToggle) && mixToggle.IsChecked != true;

    private void UpdateSliderVisual(Slider slider)
    {
        slider.Opacity = IsSliderMuted(slider) || IsSliderMixExcluded(slider) ? 0.45 : 1.0;
    }

    private void ResetLevelMeters()
    {
        foreach (var sliders in _channelSliders.Values)
        {
            foreach (var slider in sliders)
            {
                SliderLevelProperties.SetLevel(slider, 0);
            }
        }
    }

    private async Task RefreshVolumesAsync(bool showConnectionStatus)
    {
        if (showConnectionStatus)
        {
            RestoreOrShowConnectingStatus();
        }

        var snapshot = await FetchMixerSnapshotAsync().ConfigureAwait(true);
        if (snapshot is null)
        {
            if (HasCachedConnectionStatus())
            {
                StatusText.Text = _cachedStatusText!;
            }
            else
            {
                StatusText.Text = "Sonar API unavailable";
            }

            return;
        }

        ApplyMixerSnapshot(snapshot);
    }

    private async Task SyncMixerSnapshotAsync(bool applyVolumes = true)
    {
        if (_settings.AudioVisualizerEnabled)
        {
            _levelMonitor.RefreshDevices();
        }

        var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(true);
        ApplyMixerSnapshot(snapshot, applyVolumes);
    }

    private void ApplyMixerSnapshot(SonarMixerSnapshot snapshot, bool applyVolumes = true)
    {
        _enabledChannels.Clear();
        foreach (var channel in snapshot.EnabledChannels)
        {
            _enabledChannels.Add(channel);
        }

        ApplyChannelVisibility(snapshot);
        ApplyStreamerModeLayout(snapshot.IsStreamerMode);
        ApplyChannelSettingsToUi(snapshot.Channels, applyVolumes);

        if (!_apiClient.IsConnected)
        {
            ClearCachedStatusText();
            StatusText.Text = "Sonar API unavailable";
        }
        else
        {
            UpdateCachedStatusText(snapshot);
        }

        if (_isVisibleForUser)
        {
            LockOverlayHeight();
            if (!_isSlideAnimating && !_isViewTransitionAnimating)
            {
                RepositionOverlay();
            }
        }
    }

    private string BuildStatusText(SonarMixerSnapshot snapshot)
    {
        if (!_apiClient.IsConnected)
        {
            return "Sonar API unavailable";
        }

        var portSuffix = _apiClient.Port is int port ? $" · port {port}" : string.Empty;
        var modeLabel = snapshot.IsStreamerMode ? "Streamer mode" : "Classic mode";
        var channelCount = snapshot.EnabledChannels.Count;
        return $"{modeLabel} · {channelCount} channels{portSuffix}";
    }

    private bool HasCachedConnectionStatus() =>
        !string.IsNullOrWhiteSpace(_cachedStatusText) && _apiClient.IsConnected;

    private void RestoreOrShowConnectingStatus()
    {
        StatusText.Text = HasCachedConnectionStatus()
            ? _cachedStatusText!
            : "Connecting to Sonar...";
    }

    private void UpdateCachedStatusText(SonarMixerSnapshot snapshot)
    {
        _cachedStatusText = BuildStatusText(snapshot);
        StatusText.Text = _cachedStatusText;
    }

    private void ClearCachedStatusText()
    {
        _cachedStatusText = null;
    }

    private void ApplyChannelVisibility(SonarMixerSnapshot snapshot)
    {
        foreach (var (channel, section) in _channelSections)
        {
            section.Visibility = snapshot.IsChannelEnabled(channel)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplyStreamerModeLayout(bool streamerMode)
    {
        var visibility = streamerMode ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in _streamerOnlyElements)
        {
            element.Visibility = visibility;
        }
    }

    private void ApplyChannelSettingsToUi(
        IReadOnlyDictionary<string, SonarChannelSettings> settings,
        bool applyVolumes = true)
    {
        _isUpdatingFromApi = true;
        try
        {
            foreach (var (slider, binding) in _sliderBindings)
            {
                if (!IsChannelEnabled(binding.Channel))
                {
                    continue;
                }

                if (!settings.TryGetValue(binding.Channel, out var channelSettings))
                {
                    continue;
                }

                var state = binding.Path == SonarMixerPath.Streaming
                    ? channelSettings.Streaming
                    : channelSettings.Monitoring;

                if (state is null)
                {
                    continue;
                }

                if (applyVolumes && state.Volume is float volume)
                {
                    slider.Value = Math.Round(volume * 100, 1);
                }

                if (_sliderMuteToggles.TryGetValue(slider, out var muteToggle))
                {
                    muteToggle.IsChecked = state.Muted == true;
                }

                if (_sliderMixToggles.TryGetValue(slider, out var mixToggle))
                {
                    mixToggle.IsChecked = state.MixIncluded != false;
                }

                UpdateSliderVisual(slider);
            }

            if (applyVolumes)
            {
                UpdateDisplayedValues();
            }
        }
        finally
        {
            _isUpdatingFromApi = false;
        }

        RefreshAllSliderLevels();
    }

    private void UpdateDisplayedValues()
    {
        foreach (var (slider, label) in _sliderValueLabels)
        {
            label.Text = $"{slider.Value:0}%";
        }
    }

    private void ChannelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingFromApi || sender is not Slider slider)
        {
            return;
        }

        if (!_sliderBindings.TryGetValue(slider, out var binding))
        {
            return;
        }

        if (_sliderValueLabels.TryGetValue(slider, out var label))
        {
            label.Text = $"{slider.Value:0}%";
        }

        if (string.Equals(binding.Channel, "master", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProportionalChannelsUi(binding.Path, e.OldValue, e.NewValue);
        }

        QueueVolumeSend(
            binding.Channel,
            binding.Path,
            (float)(slider.Value / 100d),
            Math.Abs(e.NewValue - e.OldValue) >= VolumeClickJumpThreshold);

        RefreshAllSliderLevels();
    }

    private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_isUpdatingFromApi || sender is not Slider slider)
        {
            return;
        }

        if (!_sliderBindings.TryGetValue(slider, out var binding))
        {
            return;
        }

        QueueVolumeSend(binding.Channel, binding.Path, (float)(slider.Value / 100d), forceImmediate: true);
    }

    private void ApplyProportionalChannelsUi(SonarMixerPath path, double oldMaster, double newMaster)
    {
        if (oldMaster <= 0.01)
        {
            return;
        }

        var ratio = newMaster / oldMaster;
        _isUpdatingFromApi = true;
        try
        {
            foreach (var channel in GetActiveProportionalChannels())
            {
                var channelSlider = FindSlider(channel, path);
                if (channelSlider is null)
                {
                    continue;
                }

                channelSlider.Value = Math.Clamp(Math.Round(channelSlider.Value * ratio, 1), 0, 100);
            }

            UpdateDisplayedValues();
        }
        finally
        {
            _isUpdatingFromApi = false;
        }
    }

    private Slider? FindSlider(string channel, SonarMixerPath path)
    {
        foreach (var (slider, binding) in _sliderBindings)
        {
            if (string.Equals(binding.Channel, channel, StringComparison.OrdinalIgnoreCase) && binding.Path == path)
            {
                return slider;
            }
        }

        return null;
    }

    private void QueueVolumeSend(string channel, SonarMixerPath path, float volume, bool forceImmediate)
    {
        _pendingVolumeChannel = channel;
        _pendingVolumePath = path;
        _pendingVolume = volume;

        if (forceImmediate)
        {
            _volumeThrottleTimer.Stop();
            _ = SendPendingVolumeAsync();
            return;
        }

        var elapsedMs = (DateTime.UtcNow - _lastVolumeSendUtc).TotalMilliseconds;
        if (elapsedMs >= VolumeThrottleMs && !_volumeSendInProgress)
        {
            _ = SendPendingVolumeAsync();
            return;
        }

        if (!_volumeThrottleTimer.IsEnabled)
        {
            var delayMs = Math.Max(1, VolumeThrottleMs - (int)elapsedMs);
            _volumeThrottleTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _volumeThrottleTimer.Start();
        }
    }

    private async void VolumeThrottleTimer_Tick(object? sender, EventArgs e)
    {
        _volumeThrottleTimer.Stop();
        await SendPendingVolumeAsync().ConfigureAwait(true);
    }

    private async Task SendPendingVolumeAsync()
    {
        if (_pendingVolumeChannel is null)
        {
            return;
        }

        if (_volumeSendInProgress)
        {
            _volumeResendPending = true;
            return;
        }

        _volumeSendInProgress = true;
        var channel = _pendingVolumeChannel;
        var path = _pendingVolumePath;
        var volume = _pendingVolume;
        _lastVolumeSendUtc = DateTime.UtcNow;

        try
        {
            var updatedVolumes = await _apiClient.SetVolumeAsync(channel, volume, path).ConfigureAwait(true);
            if (updatedVolumes is null)
            {
                StatusText.Text = "Failed to update Sonar volume";
            }
            else
            {
                if (updatedVolumes.Count > 0 &&
                    (_pendingVolumeChannel != channel ||
                     _pendingVolumePath != path ||
                     Math.Abs(_pendingVolume - volume) <= 0.001f))
                {
                    ApplyChannelSettingsToUi(updatedVolumes);
                }

                await SyncMixerSnapshotAsync(applyVolumes: false).ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            StatusText.Text = "Failed to update Sonar volume";
        }
        finally
        {
            _volumeSendInProgress = false;

            if (_volumeResendPending ||
                (_pendingVolumeChannel == channel &&
                 _pendingVolumePath == path &&
                 Math.Abs(_pendingVolume - volume) > 0.001f))
            {
                _volumeResendPending = false;
                _ = SendPendingVolumeAsync();
            }
        }
    }

    private async void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFromApi || sender is not ToggleButton muteToggle)
        {
            return;
        }

        if (!_muteBindings.TryGetValue(muteToggle, out var binding))
        {
            return;
        }

        var muted = muteToggle.IsChecked == true;
        var linkedSlider = _sliderMuteToggles.FirstOrDefault(pair => pair.Value == muteToggle).Key;
        if (linkedSlider is not null)
        {
            UpdateSliderVisual(linkedSlider);
        }

        try
        {
            var updatedSettings = await _apiClient.SetMuteAsync(binding.Channel, muted, binding.Path).ConfigureAwait(true);
            if (updatedSettings is null)
            {
                muteToggle.IsChecked = !muted;
                if (linkedSlider is not null)
                {
                    UpdateSliderVisual(linkedSlider);
                }

                StatusText.Text = "Failed to update mute";
                return;
            }

            ApplyChannelSettingsToUi(updatedSettings);
            await SyncMixerSnapshotAsync(applyVolumes: false).ConfigureAwait(true);
        }
        catch (Exception)
        {
            muteToggle.IsChecked = !muted;
            if (linkedSlider is not null)
            {
                UpdateSliderVisual(linkedSlider);
            }

            StatusText.Text = "Failed to update mute";
        }
    }

    private async void MixToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFromApi || sender is not ToggleButton mixToggle)
        {
            return;
        }

        if (!_mixBindings.TryGetValue(mixToggle, out var binding))
        {
            return;
        }

        var included = mixToggle.IsChecked == true;
        var linkedSlider = _sliderMixToggles.FirstOrDefault(pair => pair.Value == mixToggle).Key;
        if (linkedSlider is not null)
        {
            UpdateSliderVisual(linkedSlider);
            RefreshAllSliderLevels();
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
                    UpdateSliderVisual(linkedSlider);
                    RefreshAllSliderLevels();
                }

                StatusText.Text = "Failed to update mix routing";
                return;
            }

            ApplyChannelSettingsToUi(updatedSettings);
            await SyncMixerSnapshotAsync(applyVolumes: false).ConfigureAwait(true);
        }
        catch (Exception)
        {
            mixToggle.IsChecked = !included;
            if (linkedSlider is not null)
            {
                UpdateSliderVisual(linkedSlider);
                RefreshAllSliderLevels();
            }

            StatusText.Text = "Failed to update mix routing";
        }
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => _ = ShowSettingsViewAsync();

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

    private void ShowSettingsView(bool instant = false)
    {
        if (instant || !_isVisibleForUser || _isViewTransitionAnimating)
        {
            ApplySettingsViewInstant();
            return;
        }

        _ = ShowSettingsViewAsync();
    }

    private Task ShowMixerViewAsync() =>
        TransitionBetweenViewsAsync(showSettings: false);

    private Task ShowSettingsViewAsync() =>
        TransitionBetweenViewsAsync(showSettings: true);

    private async Task TransitionBetweenViewsAsync(bool showSettings)
    {
        if (_isViewTransitionAnimating || _isShowingSettings == showSettings)
        {
            return;
        }

        LockOverlayHeight();
        UpdateLayout();

        var slideDistance = GetViewSlideDistance();

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

        SetViewSlideState(incomingHeader, incomingStart);
        SetViewSlideState(incomingContent, incomingStart);
        SetViewSlideState(outgoingHeader, 0);
        SetViewSlideState(outgoingContent, 0);

        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        try
        {
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render).Task.ConfigureAwait(true);

            await Task.WhenAll(
                AnimateViewSlideAsync(outgoingHeader, outgoingEnd, ViewTransitionMs, easing),
                AnimateViewSlideAsync(outgoingContent, outgoingEnd, ViewTransitionMs, easing),
                AnimateViewSlideAsync(incomingHeader, 0, ViewTransitionMs, easing),
                AnimateViewSlideAsync(incomingContent, 0, ViewTransitionMs, easing)).ConfigureAwait(true);
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
        ResetViewSlideState(MixerHeaderPanel);
        ResetViewSlideState(MixerTabPanel);
        ResetViewSlideState(SettingsHeaderPanel);
        ResetViewSlideState(SettingsTabPanel);
        ResetViewZOrder();
        ApplyViewState();
    }

    private void ApplySettingsViewInstant()
    {
        _isShowingSettings = true;
        MixerHeaderPanel.Visibility = Visibility.Collapsed;
        SettingsHeaderPanel.Visibility = Visibility.Visible;
        MixerTabPanel.Visibility = Visibility.Collapsed;
        SettingsTabPanel.Visibility = Visibility.Visible;
        ResetViewSlideState(MixerHeaderPanel);
        ResetViewSlideState(MixerTabPanel);
        ResetViewSlideState(SettingsHeaderPanel);
        ResetViewSlideState(SettingsTabPanel);
        ResetViewZOrder();
        ApplyViewState();
    }

    private double GetViewSlideDistance()
    {
        UpdateLayout();
        var width = ViewContentHost.ActualWidth;
        if (width < 1)
        {
            width = Math.Max(ActualWidth - 34, 320);
        }

        return width;
    }

    private void ResetViewZOrder()
    {
        System.Windows.Controls.Panel.SetZIndex(MixerHeaderPanel, 0);
        System.Windows.Controls.Panel.SetZIndex(SettingsHeaderPanel, 0);
        System.Windows.Controls.Panel.SetZIndex(MixerTabPanel, 0);
        System.Windows.Controls.Panel.SetZIndex(SettingsTabPanel, 0);
    }

    private static TranslateTransform EnsureViewTransform(FrameworkElement element)
    {
        element.RenderTransformOrigin = new System.Windows.Point(0, 0);

        if (element.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static void SetViewSlideState(FrameworkElement element, double x)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        EnsureViewTransform(element).BeginAnimation(TranslateTransform.XProperty, null);
        element.Opacity = 1;
        EnsureViewTransform(element).X = x;
    }

    private static void ResetViewSlideState(FrameworkElement element)
    {
        SetViewSlideState(element, 0);
    }

    private static Task AnimateViewSlideAsync(
        FrameworkElement element,
        double toX,
        int durationMs,
        IEasingFunction easing)
    {
        var transform = EnsureViewTransform(element);
        transform.BeginAnimation(TranslateTransform.XProperty, null);

        var animation = new DoubleAnimation
        {
            From = transform.X,
            To = toX,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        animation.Completed += OnCompleted;

        void OnCompleted(object? sender, EventArgs e)
        {
            animation.Completed -= OnCompleted;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = toX;
            completion.TrySetResult();
        }

        transform.BeginAnimation(TranslateTransform.XProperty, animation);
        return completion.Task;
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
            ResetLevelMeters();
        }
        else if (!_isSlideAnimating)
        {
            UpdateLevelPollTimer();
            if (_settings.AudioVisualizerEnabled)
            {
                PollAndRefreshLevels();
            }
        }

        if (!_isViewTransitionAnimating)
        {
            RepositionOverlay();
        }
    }

    private void FeatureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFeatureToggleChanges)
        {
            return;
        }

        if (sender == RunAtWindowsStartupToggle && !ApplyRunAtWindowsStartupSetting())
        {
            return;
        }

        SyncFeatureSettingsFromUi();
        _settings.Save();

        ApplyAudioVisualizerState();
        ApplyMediaKeysOverrideSettings();

        if (!_settings.VolumeOverlayEnabled)
        {
            _volumeOverlay.HideImmediately();
        }

        if (_settings.DiscordScreenshareEchoFix)
        {
            // Future: locate discord.exe render session and mute chatRender endpoint.
        }
    }

    private bool ApplyRunAtWindowsStartupSetting()
    {
        var enabled = RunAtWindowsStartupToggle.IsChecked == true;
        if (WindowsStartupRegistration.TrySetEnabled(enabled))
        {
            _settings.RunAtWindowsStartup = enabled;
            return true;
        }

        _suppressFeatureToggleChanges = true;
        try
        {
            RunAtWindowsStartupToggle.IsChecked = _settings.RunAtWindowsStartup;
        }
        finally
        {
            _suppressFeatureToggleChanges = false;
        }

        return false;
    }

    private void SyncFeatureSettingsFromUi()
    {
        _settings.RunAtWindowsStartup = RunAtWindowsStartupToggle.IsChecked == true;
        _settings.MediaKeysOverride = MediaKeysOverrideToggle.IsChecked == true;
        _settings.VolumeOverlayEnabled = VolumeOverlayToggle.IsChecked == true;
        _settings.DiscordScreenshareEchoFix = DiscordEchoFixToggle.IsChecked == true;
        _settings.AudioVisualizerEnabled = AudioVisualizerToggle.IsChecked == true;
    }

    private void MediaKeysOverrideChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMediaKeysChannelChange || MediaKeysOverrideChannelCombo.SelectedItem is null)
        {
            return;
        }

        _settings.MediaKeysOverrideChannel = GetSelectedMediaKeysOverrideChannel();
        _settings.Save();
        ApplyMediaKeysOverrideSettings();
    }

    private void TrayIconStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTrayIconStyleChange || TrayIconStyleCombo.SelectedItem is null)
        {
            return;
        }

        _settings.TrayIconStyle = GetSelectedTrayIconStyle();
        _settings.Save();
        _applyTrayIcon();
    }

    private void PopulateTrayIconStyleCombo()
    {
        TrayIconStyleCombo.Items.Clear();

        TrayIconStyleCombo.Items.Add(new ComboBoxItem
        {
            Content = "Auto (match Windows theme)",
            Tag = TrayIconStyle.Auto
        });
        TrayIconStyleCombo.Items.Add(new ComboBoxItem
        {
            Content = "Accent (cyan)",
            Tag = TrayIconStyle.Accent
        });
        TrayIconStyleCombo.Items.Add(new ComboBoxItem
        {
            Content = "White",
            Tag = TrayIconStyle.White
        });
        TrayIconStyleCombo.Items.Add(new ComboBoxItem
        {
            Content = "Dark",
            Tag = TrayIconStyle.Dark
        });
    }

    private void SelectTrayIconStyle(TrayIconStyle style)
    {
        _suppressTrayIconStyleChange = true;
        try
        {
            foreach (ComboBoxItem item in TrayIconStyleCombo.Items)
            {
                if (item.Tag is TrayIconStyle candidate && candidate == style)
                {
                    TrayIconStyleCombo.SelectedItem = item;
                    return;
                }
            }

            TrayIconStyleCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressTrayIconStyleChange = false;
        }
    }

    private TrayIconStyle GetSelectedTrayIconStyle()
    {
        if (TrayIconStyleCombo.SelectedItem is ComboBoxItem { Tag: TrayIconStyle style })
        {
            return style;
        }

        return TrayIconStyle.Auto;
    }

    private void PopulateMediaKeysOverrideChannelCombo()
    {
        MediaKeysOverrideChannelCombo.Items.Clear();

        foreach (var channel in SonarChannels.All)
        {
            MediaKeysOverrideChannelCombo.Items.Add(new ComboBoxItem
            {
                Content = SonarChannels.GetDisplayName(channel),
                Tag = channel
            });
        }
    }

    private void SelectMediaKeysOverrideChannel(string channel)
    {
        var normalizedChannel = SonarChannels.NormalizeChannel(channel);

        _suppressMediaKeysChannelChange = true;
        try
        {
            for (var i = 0; i < MediaKeysOverrideChannelCombo.Items.Count; i++)
            {
                if (MediaKeysOverrideChannelCombo.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, normalizedChannel, StringComparison.OrdinalIgnoreCase))
                {
                    MediaKeysOverrideChannelCombo.SelectedIndex = i;
                    return;
                }
            }

            MediaKeysOverrideChannelCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressMediaKeysChannelChange = false;
        }
    }

    private string GetSelectedMediaKeysOverrideChannel()
    {
        if (MediaKeysOverrideChannelCombo.SelectedItem is ComboBoxItem item)
        {
            return SonarChannels.NormalizeChannel(item.Tag as string);
        }

        return SonarChannels.NormalizeChannel(_settings.MediaKeysOverrideChannel);
    }

    private void ApplyMediaKeysOverrideSettings()
    {
        var enabled = MediaKeysOverrideToggle.IsChecked == true;
        MediaKeysOverrideChannelPanel.IsEnabled = enabled;
        MediaKeysOverrideChannelPanel.Opacity = enabled ? 1.0 : 0.55;

        var channel = GetSelectedMediaKeysOverrideChannel();
        _settings.MediaKeysOverrideChannel = channel;
        _mediaKeysOverride.SetTargetChannel(channel);
        _mediaKeysOverride.SetEnabled(enabled);
    }

    private void MediaKeysOverride_MixerChanged()
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
                await SyncMixerSnapshotAsync().ConfigureAwait(true);
            }
            catch
            {
                // Ignore transient sync errors from media key updates.
            }
        }));
    }

    private void ApplyAudioVisualizerState()
    {
        if (_levelPollTimer is null)
        {
            return;
        }

        if (!_settings.AudioVisualizerEnabled)
        {
            _levelPollTimer.Stop();
            ResetLevelMeters();
            _lastRawChannelLevels.Clear();
            _levelMonitor.Suspend();
            return;
        }

        _levelMonitor.RefreshDevices();
        if (_isVisibleForUser && !_isShowingSettings && !_isSlideAnimating)
        {
            PollAndRefreshLevels();
        }

        UpdateLevelPollTimer();
    }

    private void UpdateLevelPollTimer()
    {
        if (_levelPollTimer is null)
        {
            return;
        }

        _levelPollTimer.Stop();

        if (_settings.AudioVisualizerEnabled
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
