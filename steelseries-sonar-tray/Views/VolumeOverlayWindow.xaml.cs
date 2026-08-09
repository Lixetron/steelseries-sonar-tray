using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SonarQuickMixer.Services;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Views;

public partial class VolumeOverlayWindow : Window
{
    private const double TrackWidth = 132;
    private const string VolumeIcon = "\uE767";
    private const string MuteIcon = "\uE74F";
    private const string LockIcon = "\uE72E";

    private const int FadeInMs = 400;
    private const int FadeOutMs = 320;
    private const double SlideDistance = 40;

    private Storyboard? _activeStoryboard;
    private TaskCompletionSource? _hideCompletion;
    private double _restLeft;
    private double _restTop;

    public event Action? EntranceAnimationCompleted;

    public VolumeOverlayWindow()
    {
        InitializeComponent();
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
        ChannelItems.ItemsSource = Array.Empty<VolumeOverlayRowVm>();
        UpdateLayout();
    }

    public void PrepareEntrance(IReadOnlyList<VolumeNotificationState> channels)
    {
        StopActiveStoryboard();
        UpdateContent(channels);
        SetEntrancePose();
        UpdateLayout();
    }

    public void UpdateContentOnly(IReadOnlyList<VolumeNotificationState> channels) =>
        UpdateContent(channels);

    public void StartEntranceAnimation()
    {
        _ = Dispatcher.BeginInvoke(PlayEntranceAnimation, DispatcherPriority.Loaded);
    }

    public void Present(IReadOnlyList<VolumeNotificationState> channels)
    {
        StopActiveStoryboard();
        UpdateContent(channels);
        SetVisiblePose();
    }

    public void HideImmediately()
    {
        StopActiveStoryboard();
        SetEntrancePose();
        ChannelItems.ItemsSource = Array.Empty<VolumeOverlayRowVm>();
        Hide();
    }

    public Task PlayExitAnimationAsync()
    {
        StopActiveStoryboard();

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
            ChannelItems.ItemsSource = Array.Empty<VolumeOverlayRowVm>();
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

    private void UpdateContent(IReadOnlyList<VolumeNotificationState> channels)
    {
        var rows = new List<VolumeOverlayRowVm>(channels.Count);
        for (var i = 0; i < channels.Count; i++)
        {
            rows.Add(VolumeOverlayRowVm.FromState(channels[i], isLast: i == channels.Count - 1));
        }

        ChannelItems.ItemsSource = rows;
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

    private sealed class VolumeOverlayRowVm
    {
        public required string DisplayName { get; init; }
        public required string IconGlyph { get; init; }
        public required double FillWidth { get; init; }
        public required string ValueText { get; init; }
        public required System.Windows.Media.Brush AccentBrush { get; init; }
        public required System.Windows.Media.Brush TrackBrush { get; init; }
        public string Message { get; init; } = string.Empty;
        public Visibility MessageVisibility { get; init; } = Visibility.Collapsed;
        public bool IsLast { get; init; }

        public static VolumeOverlayRowVm FromState(VolumeNotificationState state, bool isLast)
        {
            var hasMessage = !string.IsNullOrWhiteSpace(state.Message);
            var volume = Math.Clamp(state.Volume, 0f, 1f);
            var accent = CreateAccentBrush(state.ChannelId);
            return new VolumeOverlayRowVm
            {
                DisplayName = SonarChannels.GetDisplayName(state.ChannelId),
                IconGlyph = hasMessage ? LockIcon : state.IsMuted ? MuteIcon : VolumeIcon,
                FillWidth = state.IsMuted ? 0 : TrackWidth * volume,
                ValueText = state.IsMuted
                    ? "Mute"
                    : $"{Math.Clamp((int)Math.Round(volume * 100), 0, 100)}%",
                AccentBrush = accent,
                TrackBrush = CreateTrackBrush(accent.Color),
                Message = hasMessage ? state.Message! : string.Empty,
                MessageVisibility = hasMessage ? Visibility.Visible : Visibility.Collapsed,
                IsLast = isLast
            };
        }

        private static SolidColorBrush CreateAccentBrush(string channelId)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(SonarChannels.GetAccentHex(channelId))!);
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush CreateTrackBrush(System.Windows.Media.Color accent)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x44, accent.R, accent.G, accent.B));
            brush.Freeze();
            return brush;
        }
    }
}
