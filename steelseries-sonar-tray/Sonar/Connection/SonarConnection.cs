namespace SonarQuickMixer.Sonar;

internal sealed class SonarConnection
{
    private readonly SonarSession _session;
    private readonly SonarWebServerDiscovery _discovery;
    private readonly SonarModeDetector _modeDetector;

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
        return true;
    }

    public async Task<bool> RefreshModeAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.IsConnected)
        {
            return false;
        }

        _session.StreamerMode = await _modeDetector
            .DetectStreamerModeAsync(_session.WebServerAddress, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public void Invalidate() => _session.Invalidate();
}
