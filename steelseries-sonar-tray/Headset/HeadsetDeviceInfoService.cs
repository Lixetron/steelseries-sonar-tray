namespace SonarQuickMixer.Headset;

public sealed class HeadsetDeviceInfoService : IDisposable
{
    private readonly HeadsetHttpTransport _transport = new();
    private readonly SteelSeriesEngineDeviceApi _engineApi;
    private readonly object _gate = new();
    private HeadsetDeviceInfo? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(2);

    public HeadsetDeviceInfoService()
    {
        var discovery = new SteelSeriesEngineDiscovery(_transport);
        _engineApi = new SteelSeriesEngineDeviceApi(_transport, discovery);
    }

    public async Task<HeadsetDeviceInfo?> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < _cacheTtl)
            {
                return _cached;
            }
        }

        EngineHeadsetSummary? engine = null;
        try
        {
            engine = await _engineApi.GetPrimaryHeadsetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Engine is optional; HID may still succeed.
        }

        HidHeadsetStatus? hid = null;
        try
        {
            hid = await Task.Run(() => SteelSeriesHeadsetHidReader.TryReadStatus(engine?.UsbProductId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // HID access can fail when GG holds exclusive handles or device is absent.
        }

        var info = Merge(engine, hid);
        lock (_gate)
        {
            _cached = info;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return info;
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cached = null;
            _cachedAtUtc = DateTime.MinValue;
        }
    }

    private static HeadsetDeviceInfo? Merge(EngineHeadsetSummary? engine, HidHeadsetStatus? hid)
    {
        if (engine is null && hid is null)
        {
            return null;
        }

        var displayName = engine?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = hid?.ProductName;
        }

        string? connection = null;
        if (hid is { IsHeadsetPowered: false })
        {
            connection = "Disconnected";
        }
        else if (engine?.LooksWireless == true || hid is not null)
        {
            connection = InferConnectionMethod(engine, displayName);
        }

        int? battery = null;
        bool? charging = null;
        bool? powered = engine?.IsConnected;

        if (hid is not null)
        {
            powered = hid.IsHeadsetPowered;
            if (hid.IsHeadsetPowered)
            {
                battery = hid.BatteryPercent;
                charging = hid.IsCharging;
            }
        }

        var info = new HeadsetDeviceInfo
        {
            DisplayName = displayName,
            BatteryPercent = battery,
            IsCharging = charging,
            IsHeadsetPowered = powered,
            ConnectionMethod = connection
        };

        return info.HasAnyData ? info : null;
    }

    private static string InferConnectionMethod(EngineHeadsetSummary? engine, string? displayName)
    {
        var haystack = $"{engine?.DeviceName} {displayName}";
        if (haystack.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("_bt", StringComparison.OrdinalIgnoreCase))
        {
            return "Bluetooth";
        }

        if (engine?.LooksWireless == true ||
            haystack.Contains("nova", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("arctis", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("wireless", StringComparison.OrdinalIgnoreCase))
        {
            return "2.4 GHz";
        }

        return "USB";
    }

    public void Dispose() => _transport.Dispose();
}
