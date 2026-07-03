using System.Windows.Controls;
using System.Windows.Threading;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Mixing;

public sealed class VolumeSendCoordinator
{
    private const int VolumeThrottleMs = 16;

    private readonly SonarApiClient _apiClient;
    private readonly Action<string> _setStatusText;
    private readonly Func<bool> _isUpdatingFromApi;
    private readonly Action<IReadOnlyDictionary<string, SonarChannelSettings>> _applyChannelSettings;
    private readonly Func<Task> _syncMixerSnapshot;
    private readonly DispatcherTimer _volumeThrottleTimer;

    private bool _volumeSendInProgress;
    private bool _volumeResendPending;
    private string? _pendingVolumeChannel;
    private SonarMixerPath _pendingVolumePath;
    private float _pendingVolume;
    private DateTime _lastVolumeSendUtc = DateTime.MinValue;

    public VolumeSendCoordinator(
        SonarApiClient apiClient,
        Action<string> setStatusText,
        Func<bool> isUpdatingFromApi,
        Action<IReadOnlyDictionary<string, SonarChannelSettings>> applyChannelSettings,
        Func<Task> syncMixerSnapshot)
    {
        _apiClient = apiClient;
        _setStatusText = setStatusText;
        _isUpdatingFromApi = isUpdatingFromApi;
        _applyChannelSettings = applyChannelSettings;
        _syncMixerSnapshot = syncMixerSnapshot;

        _volumeThrottleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(VolumeThrottleMs)
        };
        _volumeThrottleTimer.Tick += VolumeThrottleTimer_Tick;
    }

    public bool IsSendInProgress => _volumeSendInProgress;

    public void QueueVolumeSend(string channel, SonarMixerPath path, float volume, bool forceImmediate)
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

    public void Stop()
    {
        _volumeThrottleTimer.Stop();
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
                _setStatusText("Failed to update Sonar volume");
            }
            else
            {
                if (updatedVolumes.Count > 0 &&
                    (_pendingVolumeChannel != channel ||
                     _pendingVolumePath != path ||
                     Math.Abs(_pendingVolume - volume) <= 0.001f))
                {
                    _applyChannelSettings(updatedVolumes);
                }

                await _syncMixerSnapshot().ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            _setStatusText("Failed to update Sonar volume");
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
}
