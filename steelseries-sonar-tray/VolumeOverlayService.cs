using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SonarQuickMixer;

public sealed class VolumeOverlayService : IDisposable
{
    private const int HideDelayMs = 2000;

    private readonly Func<bool> _isEnabled;
    private readonly Dispatcher _dispatcher;
    private VolumeOverlayWindow? _window;
    private DispatcherTimer? _hideTimer;
    private bool _isOverlayVisible;
    private bool _entranceInProgress;
    private bool _hideInProgress;
    private bool _disposed;

    public VolumeOverlayService(Func<bool> isEnabled)
    {
        _isEnabled = isEnabled;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _ = _dispatcher.BeginInvoke(WarmupWindow, DispatcherPriority.ApplicationIdle);
    }

    public void HideImmediately()
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            HideImmediatelyUi();
        }
        else
        {
            _dispatcher.Invoke(HideImmediatelyUi);
        }
    }

    private void HideImmediatelyUi()
    {
        _hideTimer?.Stop();
        _entranceInProgress = false;
        _hideInProgress = false;
        _isOverlayVisible = false;

        if (_window is null)
        {
            return;
        }

        _window.HideImmediately();
    }

    public void Show(VolumeNotificationState state)
    {
        if (_disposed || !_isEnabled() || !VolumeNotificationGuard.ShouldShowVolumeOverlay())
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() => ShowInternal(state));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_dispatcher.CheckAccess())
        {
            DisposeUi();
        }
        else
        {
            _dispatcher.Invoke(DisposeUi);
        }
    }

    private void WarmupWindow()
    {
        if (_disposed)
        {
            return;
        }

        EnsureWindow();
        _window!.Warmup();
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _window = new VolumeOverlayWindow();
        _window.EntranceAnimationCompleted += OnEntranceAnimationCompleted;
        _ = new WindowInteropHelper(_window).EnsureHandle();
    }

    private void OnEntranceAnimationCompleted()
    {
        _entranceInProgress = false;
    }

    private void ShowInternal(VolumeNotificationState state)
    {
        if (_disposed)
        {
            return;
        }

        _hideTimer?.Stop();

        var animateEntrance = !_isOverlayVisible && !_hideInProgress && !_entranceInProgress;
        _hideInProgress = false;

        EnsureWindow();
        var window = _window!;
        var position = VolumeNotificationGuard.GetTopCenterPosition(window);
        window.SetRestPosition(position.Left, position.Top);

        if (animateEntrance)
        {
            window.PrepareEntrance(state);

            if (window.Visibility != Visibility.Visible)
            {
                window.Show();
            }

            _entranceInProgress = true;
            window.StartEntranceAnimation();
        }
        else if (_entranceInProgress)
        {
            window.UpdateContentOnly(state);
        }
        else
        {
            window.Present(state);

            if (window.Visibility != Visibility.Visible)
            {
                window.Show();
            }
        }

        _isOverlayVisible = true;
        ResetHideTimer();
    }

    private void ResetHideTimer()
    {
        _hideTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(HideDelayMs)
        };
        _hideTimer.Tick -= HideTimer_Tick;
        _hideTimer.Tick += HideTimer_Tick;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        _ = HideAsync();
    }

    private async Task HideAsync()
    {
        if (_window is null || !_isOverlayVisible || _hideInProgress)
        {
            return;
        }

        _hideInProgress = true;
        try
        {
            await _window.PlayExitAnimationAsync().ConfigureAwait(true);
            _window.Hide();
            _isOverlayVisible = false;
        }
        finally
        {
            _hideInProgress = false;
            _entranceInProgress = false;
        }
    }

    private void DisposeUi()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
        _isOverlayVisible = false;
        _entranceInProgress = false;

        if (_window is not null)
        {
            _window.EntranceAnimationCompleted -= OnEntranceAnimationCompleted;
            _window.Close();
            _window = null;
        }
    }
}
