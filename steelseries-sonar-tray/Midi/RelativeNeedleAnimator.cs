using System.Windows.Threading;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Drives the animated needle position for relative (endless) MIDI encoders: each incoming
/// tick nudges a target angle, then a render-frame timer eases the visible needle toward it.
/// Extracted from <see cref="BlueprintControlVm"/>, which composes one of these rather than
/// implementing the timer/easing itself — this is timer-driven animation logic, not
/// view-model state, even though it lives in the Midi namespace next to the VM it serves.
/// </summary>
internal sealed class RelativeNeedleAnimator
{
    /// <summary>
    /// Visual degrees per relative MIDI tick (before smoothing).
    /// Calibrated iteratively against common relative MIDI knobs (physical ≈ UI).
    /// </summary>
    public const double DegreesPerTick = 3.43;

    /// <summary>
    /// Fraction of the remaining gap applied immediately on each MIDI tick (rest eases in 1–2 frames).
    /// High = tight to the hand; low = jelly lag.
    /// </summary>
    private const double InstantCatchup = 0.82;

    /// <summary>Exponential follow rate for the leftover gap (~settles in ~2 frames at 60 Hz).</summary>
    private const double ResponsePerSecond = 55.0;

    /// <summary>Discard backlog beyond this so direction reversals stay crisp.</summary>
    private const double MaxLeadDegrees = 28.0;

    private const double SnapDegrees = 0.4;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly Action _onAngleChanged;
    private double _degrees;
    private double _targetDegrees;
    private DispatcherTimer? _timer;
    private DateTime _lastTickUtc;

    public RelativeNeedleAnimator(Action onAngleChanged)
    {
        _onAngleChanged = onAngleChanged;
    }

    public double Degrees => _degrees;

    public void Reset(double degrees = 0)
    {
        StopTimer();
        _degrees = degrees;
        _targetDegrees = degrees;
        _onAngleChanged();
    }

    public void QueueSpin(int ticks)
    {
        _targetDegrees += ticks * DegreesPerTick;
        ClampLead();

        // Unit tests / no WPF pump: snap so assertions stay deterministic.
        if (System.Windows.Application.Current?.Dispatcher is null)
        {
            _degrees = _targetDegrees;
            _onAngleChanged();
            return;
        }

        // Most of the motion lands on the MIDI event itself — avoids "following behind the hand".
        var gap = _targetDegrees - _degrees;
        _degrees += gap * InstantCatchup;
        _onAngleChanged();

        if (Math.Abs(_targetDegrees - _degrees) <= SnapDegrees)
        {
            Finish();
            return;
        }

        EnsureTimer();
    }

    private void ClampLead()
    {
        var lead = _targetDegrees - _degrees;
        if (Math.Abs(lead) <= MaxLeadDegrees)
        {
            return;
        }

        _targetDegrees = _degrees + (Math.Sign(lead) * MaxLeadDegrees);
    }

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _lastTickUtc = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = FrameInterval
        };
        timer.Tick += Timer_Tick;
        _timer = timer;
        timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0.001, 0.05);
        _lastTickUtc = now;

        var remaining = _targetDegrees - _degrees;
        if (Math.Abs(remaining) <= SnapDegrees)
        {
            Finish();
            return;
        }

        // Exponential ease: leftover gap closes quickly without a long rubber-band trail.
        var alpha = 1.0 - Math.Exp(-ResponsePerSecond * dt);
        _degrees += remaining * alpha;
        _onAngleChanged();
    }

    private void Finish()
    {
        _degrees = WrapDegrees(_targetDegrees);
        _targetDegrees = _degrees;
        _onAngleChanged();
        StopTimer();
    }

    private void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _timer = null;
    }

    private static double WrapDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        return degrees;
    }
}
