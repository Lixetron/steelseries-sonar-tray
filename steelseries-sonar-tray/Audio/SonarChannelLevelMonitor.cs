using NAudio.CoreAudioApi;

namespace SteelSeries.SonarTray.Audio;

public sealed class SonarChannelLevelMonitor : IDisposable
{
    private const float DecayFactor = 0.88f;
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<string, MMDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _smoothedLevels = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastDeviceRefreshUtc = DateTime.MinValue;
    private bool _disposed;

    public bool HasDevices => _devices.Count > 0;

    public IReadOnlyDictionary<string, float> PollLevels()
    {
        RefreshDevicesIfNeeded();

        var results = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var (channel, device) in _devices)
        {
            var peak = ReadPeak(device);
            results[channel] = Smooth(channel, peak);
        }

        return results;
    }

    private float Smooth(string channel, float peak)
    {
        var smoothed = _smoothedLevels.TryGetValue(channel, out var previous)
            ? Math.Max(peak, previous * DecayFactor)
            : peak;

        _smoothedLevels[channel] = smoothed;
        return smoothed;
    }

    private static float ReadPeak(MMDevice device)
    {
        try
        {
            return device.AudioMeterInformation.MasterPeakValue;
        }
        catch
        {
            return 0f;
        }
    }

    public void RefreshDevices() => RefreshDevices(force: true);

    public void Suspend()
    {
        DisposeDevices();
        _smoothedLevels.Clear();
        _lastDeviceRefreshUtc = DateTime.MinValue;
    }

    private void RefreshDevicesIfNeeded() => RefreshDevices(force: false);

    private void RefreshDevices(bool force)
    {
        if (!force && _devices.Count > 0 && DateTime.UtcNow - _lastDeviceRefreshUtc < DeviceRefreshInterval)
        {
            return;
        }

        DisposeDevices();
        _smoothedLevels.Clear();

        foreach (var endpoint in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                if (!SonarVirtualChannelMap.TryMatchChannel(endpoint.FriendlyName, out var channel))
                {
                    endpoint.Dispose();
                    continue;
                }

                if (_devices.TryGetValue(channel, out var existing))
                {
                    existing.Dispose();
                }

                _devices[channel] = endpoint;
            }
            catch
            {
                endpoint.Dispose();
            }
        }

        _lastDeviceRefreshUtc = DateTime.UtcNow;
    }

    private void DisposeDevices()
    {
        foreach (var device in _devices.Values)
        {
            device.Dispose();
        }

        _devices.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeDevices();
        _enumerator.Dispose();
    }
}
