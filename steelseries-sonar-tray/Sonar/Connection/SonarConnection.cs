namespace SonarQuickMixer.Sonar;

internal sealed class SonarConnection
{
    // Streamer mode is a property of the Sonar server that changes rarely, yet every
    // snapshot fetch would otherwise re-issue a `GET /mode` round-trip. Refreshing it on a
    // short TTL keeps the layout responsive to GG toggles without redundant allocations.
    private static readonly TimeSpan ModeRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly SonarSession _session;
    private readonly SonarWebServerDiscovery _discovery;
    private readonly SonarModeDetector _modeDetector;
    private DateTime _lastModeRefreshUtc = DateTime.MinValue;

    public SonarConnection(
        SonarSession session,
        SonarWebServerDiscovery discovery,
        SonarModeDetector modeDetector)
    {
        _session = session;
        _discovery = discovery;
        _modeDetector = modeDetector;
    }

    public SonarSession Session => _session;

    public async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_session.IsConnected)
        {
            return true;
        }

        var address = await _discovery.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        _session.WebServerAddress = address;
        _session.StreamerMode = await _modeDetector
            .DetectStreamerModeAsync(address, cancellationToken)
            .ConfigureAwait(false);
        _lastModeRefreshUtc = DateTime.UtcNow;
        return true;
    }

    public async Task<bool> RefreshModeAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.IsConnected)
        {
            return false;
        }

        if (DateTime.UtcNow - _lastModeRefreshUtc < ModeRefreshInterval)
        {
            return true;
        }

        _session.StreamerMode = await _modeDetector
            .DetectStreamerModeAsync(_session.WebServerAddress, cancellationToken)
            .ConfigureAwait(false);
        _lastModeRefreshUtc = DateTime.UtcNow;
        return true;
    }

    public void Invalidate() => _session.Invalidate();
}
