using SonarQuickMixer.Sonar;
using SonarQuickMixer.Services;

namespace SonarQuickMixer.Midi;

/// <summary>
/// When Sonar volume drifts from a non-motorized absolute fader position, starts a 3s window.
/// If the user does not overwrite via hardware, rolls volume back to the last hardware value.
/// </summary>
public sealed class FaderPriorityGuard : IDisposable
{
    public const int RollbackWindowMs = 3000;
    public const float VolumeEpsilon = 0.008f;

    private readonly object _sync = new();
    private readonly Dictionary<string, float> _lastHardwareVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pendingRollbacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _midiOriginatedKeys = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public event Action<string, float, SonarMixerPath, VolumeNotificationState>? RollbackRequested;

    public static string ChannelKey(string channelId, SonarMixerPath path) =>
        $"{SonarChannels.NormalizeChannel(channelId)}|{(int)path}";

    public void RememberHardwareVolume(MidiBinding binding, float volume)
    {
        if (binding.Mode != MidiValueMode.Absolute || binding.IsMotorized || binding.IsNote)
        {
            return;
        }

        var key = ChannelKey(binding.ChannelId, binding.Path);
        lock (_sync)
        {
            _lastHardwareVolumes[key] = Math.Clamp(volume, 0f, 1f);
            CancelRollback_NoLock(key);
        }
    }

    public void MarkMidiOriginated(string channelId, SonarMixerPath path)
    {
        var key = ChannelKey(channelId, path);
        lock (_sync)
        {
            _midiOriginatedKeys.Add(key);
        }
    }

    public void CancelRollbackForBinding(MidiBinding binding)
    {
        var key = ChannelKey(binding.ChannelId, binding.Path);
        lock (_sync)
        {
            CancelRollback_NoLock(key);
        }
    }

    /// <summary>
    /// Compares Sonar snapshot against last hardware positions and schedules rollbacks when needed.
    /// </summary>
    public void ObserveSnapshot(SonarMixerSnapshot snapshot, IEnumerable<MidiBinding> absoluteBindings)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var binding in absoluteBindings)
        {
            if (binding.Mode != MidiValueMode.Absolute || binding.IsMotorized || binding.IsNote)
            {
                continue;
            }

            if (!snapshot.Channels.TryGetValue(SonarChannels.NormalizeChannel(binding.ChannelId), out var settings))
            {
                continue;
            }

            var state = binding.Path == SonarMixerPath.Streaming ? settings.Streaming : settings.Monitoring;
            if (state?.Volume is not float current)
            {
                continue;
            }

            var key = ChannelKey(binding.ChannelId, binding.Path);
            float hardware;
            bool skipAsMidiOrigin;

            lock (_sync)
            {
                if (_midiOriginatedKeys.Remove(key))
                {
                    skipAsMidiOrigin = true;
                    _lastHardwareVolumes[key] = current;
                }
                else
                {
                    skipAsMidiOrigin = false;
                }

                if (!_lastHardwareVolumes.TryGetValue(key, out hardware))
                {
                    _lastHardwareVolumes[key] = current;
                    continue;
                }
            }

            if (skipAsMidiOrigin)
            {
                continue;
            }

            if (Math.Abs(current - hardware) <= VolumeEpsilon)
            {
                lock (_sync)
                {
                    CancelRollback_NoLock(key);
                }

                continue;
            }

            ScheduleRollback(binding, hardware, current);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            foreach (var cts in _pendingRollbacks.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _pendingRollbacks.Clear();
        }
    }

    private void ScheduleRollback(MidiBinding binding, float hardwareVolume, float observedVolume)
    {
        var key = ChannelKey(binding.ChannelId, binding.Path);
        CancellationTokenSource cts;

        lock (_sync)
        {
            if (_pendingRollbacks.ContainsKey(key))
            {
                return;
            }

            cts = new CancellationTokenSource();
            _pendingRollbacks[key] = cts;
        }

        _ = RunRollbackAsync(binding, hardwareVolume, observedVolume, cts);
    }

    private async Task RunRollbackAsync(
        MidiBinding binding,
        float hardwareVolume,
        float observedVolume,
        CancellationTokenSource cts)
    {
        var key = ChannelKey(binding.ChannelId, binding.Path);
        try
        {
            await Task.Delay(RollbackWindowMs, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            // Re-check still drifted (another MIDI write may have updated hardware).
            lock (_sync)
            {
                if (_lastHardwareVolumes.TryGetValue(key, out var latestHardware))
                {
                    hardwareVolume = latestHardware;
                }

                _pendingRollbacks.Remove(key);
            }

            if (Math.Abs(observedVolume - hardwareVolume) <= VolumeEpsilon)
            {
                return;
            }

            var notification = new VolumeNotificationState(
                SonarChannels.NormalizeChannel(binding.ChannelId),
                hardwareVolume,
                IsMuted: false,
                Message: "Volume locked by hardware fader");

            RollbackRequested?.Invoke(binding.ChannelId, hardwareVolume, binding.Path, notification);
        }
        catch (OperationCanceledException)
        {
            // Window cancelled by hardware overwrite or dispose.
        }
        finally
        {
            lock (_sync)
            {
                if (_pendingRollbacks.TryGetValue(key, out var existing) && ReferenceEquals(existing, cts))
                {
                    _pendingRollbacks.Remove(key);
                }
            }

            cts.Dispose();
        }
    }

    private void CancelRollback_NoLock(string key)
    {
        if (_pendingRollbacks.Remove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
