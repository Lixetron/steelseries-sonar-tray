using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SteelSeries.SonarTray;

public partial class VolumeOverlayWindow : Window
{
    private const double TrackWidth = 132;
    private const string VolumeIcon = "\uE767";
    private const string MuteIcon = "\uE74F";

    private const int FadeInMs = 400;
    private const int FadeOutMs = 320;
    private const double FillSmoothTimeMs = 55;
    private const double SlideDistance = 40;

    private Storyboard? _activeStoryboard;
    private TaskCompletionSource? _hideCompletion;
    private double _displayedFillWidth;
    private double _targetFillWidth;
    private double _restLeft;
    private double _restTop;
    private bool _isFillSmoothing;
    private bool _isMuted;
    private DateTime _lastFillUpdateUtc = DateTime.MinValue;

    public event Action? EntranceAnimationCompleted;

    public VolumeOverlayWindow()
    {
        InitializeComponent();
        TrackHost.Width = TrackWidth;
        SourceInitialized += (_, _) => VolumeNotificationGuard.ApplyNoActivateStyle(this);
    }

    public void SetRestPosition(double left, double top)
    {
        _restLeft = left;
        _restTop = top;
    }

    public void Warmup()
    {
        SetEntrancePose();
        SetFillWidthImmediate(0);
        UpdateLayout();
    }

    public void PrepareEntrance(VolumeNotificationState state)
    {
        StopActiveStoryboard();
        UpdateContent(state, snapFill: true);
        SetEntrancePose();
        UpdateLayout();
    }

    public void UpdateContentOnly(VolumeNotificationState state)
    {
        UpdateContent(state, snapFill: false);
    }

    public void StartEntranceAnimation()
    {
        _ = Dispatcher.BeginInvoke(PlayEntranceAnimation, DispatcherPriority.Loaded);
    }

    public void Present(VolumeNotificationState state)
    {
        StopActiveStoryboard();
        UpdateContent(state, snapFill: false);
        SetVisiblePose();
    }

    public void HideImmediately()
    {
        StopActiveStoryboard();
        StopFillSmoothing();
        SetEntrancePose();
        Hide();
    }

    public Task PlayExitAnimationAsync()
    {
        StopActiveStoryboard();
        StopFillSmoothing();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hideCompletion = completion;

        BeginAnimation(TopProperty, null);
        OverlayRoot.BeginAnimation(UIElement.OpacityProperty, null);

        var storyboard = CreateEntranceExitStoryboard(
            Top,
            _restTop - SlideDistance,
            OverlayRoot.Opacity,
            0,
            FadeOutMs,
            new CubicEase { EasingMode = EasingMode.EaseIn });

        storyboard.Completed += (_, _) =>
        {
            SetEntrancePose();
            _activeStoryboard = null;
            _hideCompletion?.TrySetResult();
            _hideCompletion = null;
        };

        _activeStoryboard = storyboard;
        storyboard.Begin();
        return completion.Task;
    }

    private void PlayEntranceAnimation()
    {
        BeginAnimation(TopProperty, null);
        OverlayRoot.BeginAnimation(UIElement.OpacityProperty, null);

        Left = _restLeft;
        Top = _restTop - SlideDistance;
        OverlayRoot.Opacity = 0;

        var storyboard = CreateEntranceExitStoryboard(
            _restTop - SlideDistance,
            _restTop,
            0,
            1,
            FadeInMs,
            new CubicEase { EasingMode = EasingMode.EaseOut });

        storyboard.Completed += (_, _) =>
        {
            SetVisiblePose();
            _activeStoryboard = null;
            EntranceAnimationCompleted?.Invoke();
        };

        _activeStoryboard = storyboard;
        storyboard.Begin();
    }

    private Storyboard CreateEntranceExitStoryboard(
        double fromTop,
        double toTop,
        double fromOpacity,
        double toOpacity,
        int durationMs,
        IEasingFunction easing)
    {
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };

        var topAnimation = new DoubleAnimation(fromTop, toTop, duration)
        {
            EasingFunction = easing
        };
        Storyboard.SetTarget(topAnimation, this);
        Storyboard.SetTargetProperty(topAnimation, new PropertyPath(TopProperty));

        var opacityAnimation = new DoubleAnimation(fromOpacity, toOpacity, duration)
        {
            EasingFunction = easing
        };
        Storyboard.SetTarget(opacityAnimation, OverlayRoot);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        storyboard.Children.Add(topAnimation);
        storyboard.Children.Add(opacityAnimation);
        return storyboard;
    }

    private void UpdateContent(VolumeNotificationState state, bool snapFill)
    {
        ChannelNameText.Text = SonarChannels.GetDisplayName(state.ChannelId);
        VolumeIconText.Text = state.IsMuted ? MuteIcon : VolumeIcon;
        _isMuted = state.IsMuted;

        var targetFillWidth = state.IsMuted ? 0 : TrackWidth * Math.Clamp(state.Volume, 0f, 1f);
        UpdateVolumeValueText(state.IsMuted ? 0 : state.Volume, state.IsMuted);

        if (snapFill)
        {
            SetFillWidthImmediate(targetFillWidth);
        }
        else
        {
            SetTargetFillWidth(targetFillWidth);
        }
    }

    private void SetFillWidthImmediate(double targetWidth)
    {
        StopFillSmoothing();
        _targetFillWidth = Math.Clamp(targetWidth, 0, TrackWidth);
        _displayedFillWidth = _targetFillWidth;
        VolumeFill.BeginAnimation(WidthProperty, null);
        VolumeFill.Width = _displayedFillWidth;
    }

    private void SetEntrancePose()
    {
        BeginAnimation(TopProperty, null);
        OverlayRoot.BeginAnimation(UIElement.OpacityProperty, null);
        Left = _restLeft;
        Top = _restTop - SlideDistance;
        OverlayRoot.Opacity = 0;
    }

    private void SetVisiblePose()
    {
        BeginAnimation(TopProperty, null);
        OverlayRoot.BeginAnimation(UIElement.OpacityProperty, null);
        Left = _restLeft;
        Top = _restTop;
        OverlayRoot.Opacity = 1;
    }

    private void SetTargetFillWidth(double targetWidth)
    {
        _targetFillWidth = Math.Clamp(targetWidth, 0, TrackWidth);

        if (!_isFillSmoothing)
        {
            _isFillSmoothing = true;
            _lastFillUpdateUtc = DateTime.MinValue;
            CompositionTarget.Rendering += OnFillRendering;
        }
    }

    private void OnFillRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var deltaMs = _lastFillUpdateUtc == DateTime.MinValue
            ? 16
            : Math.Clamp((now - _lastFillUpdateUtc).TotalMilliseconds, 1, 48);
        _lastFillUpdateUtc = now;

        var diff = _targetFillWidth - _displayedFillWidth;
        if (Math.Abs(diff) < 0.5)
        {
            _displayedFillWidth = _targetFillWidth;
            VolumeFill.Width = _displayedFillWidth;
            StopFillSmoothing();
            return;
        }

        var alpha = 1 - Math.Exp(-deltaMs / FillSmoothTimeMs);
        alpha = Math.Clamp(alpha, 0.25, 1);
        _displayedFillWidth += diff * alpha;
        VolumeFill.Width = _displayedFillWidth;
    }

    private void UpdateVolumeValueText(float volume, bool isMuted)
    {
        VolumeValueText.Text = isMuted
            ? "Mute"
            : $"{Math.Clamp((int)Math.Round(volume * 100), 0, 100)}%";
    }

    private void StopFillSmoothing()
    {
        if (!_isFillSmoothing)
        {
            return;
        }

        CompositionTarget.Rendering -= OnFillRendering;
        _isFillSmoothing = false;
        _lastFillUpdateUtc = DateTime.MinValue;
    }

    private void StopActiveStoryboard()
    {
        if (_activeStoryboard is not null)
        {
            _activeStoryboard.Stop();
            _activeStoryboard = null;
        }

        BeginAnimation(TopProperty, null);
        OverlayRoot.BeginAnimation(UIElement.OpacityProperty, null);

        if (_hideCompletion is not null)
        {
            _hideCompletion.TrySetResult();
            _hideCompletion = null;
        }
    }
}
