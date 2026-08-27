namespace SonarQuickMixer.Sonar;

/// <summary>
/// Facade over SteelSeries Sonar's local HTTP API.
/// Composes connection discovery, mixer read/write, and echo-fix routing.
/// </summary>
public sealed class SonarApiClient : IDisposable
{
    private readonly SonarHttpTransport _transport = new();
    private readonly SonarSession _session = new();
    private readonly SonarConnection _connection;
    private readonly SonarMixerApi _mixer;
    private readonly SonarEchoFixApi _echoFix;
    private readonly SonarDevicesApi _devices;

    public SonarApiClient()
    {
        var discovery = new SonarWebServerDiscovery(_transport);
        var modeDetector = new SonarModeDetector(_transport);
        _connection = new SonarConnection(_session, discovery, modeDetector);
        _mixer = new SonarMixerApi(_transport, _connection);
        _echoFix = new SonarEchoFixApi(_transport, _connection);
        _devices = new SonarDevicesApi(_transport, _connection);
    }

    public bool IsConnected => _session.IsConnected;

    public int? Port => _session.Port;

    public bool IsStreamerMode => _session.IsStreamerMode;

    public Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
        _connection.EnsureConnectedAsync(cancellationToken);

    public void InvalidateConnection() => _connection.Invalidate();

    public Task<bool> RefreshModeAsync(CancellationToken cancellationToken = default) =>
        _connection.RefreshModeAsync(cancellationToken);

    public Task<SonarMixerSnapshot> GetMixerSnapshotAsync(CancellationToken cancellationToken = default) =>
        _mixer.GetSnapshotAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, SonarChannelSettings>> GetAllChannelSettingsAsync(
        CancellationToken cancellationToken = default) =>
        (await _mixer.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Channels;

    public Task<float?> GetVolumeAsync(
        string channel,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default) =>
        _mixer.GetVolumeAsync(channel, path, cancellationToken);

    public Task<IReadOnlyDictionary<string, SonarChannelSettings>?> SetVolumeAsync(
        string channel,
        float volume,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default) =>
        _mixer.SetVolumeAsync(channel, volume, path, cancellationToken);

    public Task<bool> SetVolumeLiveAsync(
        string channel,
        float volume,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default) =>
        _mixer.SetVolumeLiveAsync(channel, volume, path, cancellationToken);

    public Task<IReadOnlyDictionary<string, SonarChannelSettings>?> SetMuteAsync(
        string channel,
        bool muted,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default) =>
        _mixer.SetMuteAsync(channel, muted, path, cancellationToken);

    public Task<IReadOnlyDictionary<string, SonarChannelSettings>?> SetMixIncludedAsync(
        string channel,
        bool included,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default) =>
        _mixer.SetMixIncludedAsync(channel, included, path, cancellationToken);

    public Task<SonarEchoFixRouting?> GetEchoFixRoutingAsync(CancellationToken cancellationToken = default) =>
        _echoFix.GetRoutingAsync(cancellationToken);

    public Task<IReadOnlyList<SonarAudioDevice>> GetAudioDevicesAsync(
        CancellationToken cancellationToken = default) =>
        _devices.GetAudioDevicesAsync(cancellationToken);

    public Task<SonarDeviceSelection?> GetDeviceSelectionAsync(
        CancellationToken cancellationToken = default) =>
        _devices.GetDeviceSelectionAsync(cancellationToken);

    public Task<bool> SetOutputDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default) =>
        _devices.SetOutputDeviceAsync(deviceId, cancellationToken);

    public Task<bool> SetMicrophoneDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default) =>
        _devices.SetMicrophoneDeviceAsync(deviceId, cancellationToken);

    public void Dispose() => _transport.Dispose();
}
