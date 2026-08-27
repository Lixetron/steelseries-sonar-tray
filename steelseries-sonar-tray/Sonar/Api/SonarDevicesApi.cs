namespace SonarQuickMixer.Sonar;

internal sealed class SonarDevicesApi
{
    private readonly SonarHttpTransport _transport;
    private readonly SonarConnection _connection;

    public SonarDevicesApi(SonarHttpTransport transport, SonarConnection connection)
    {
        _transport = transport;
        _connection = connection;
    }

    public async Task<IReadOnlyList<SonarAudioDevice>> GetAudioDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return Array.Empty<SonarAudioDevice>();
        }

        var address = _connection.Session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return Array.Empty<SonarAudioDevice>();
        }

        using var document = await _transport
            .GetJsonDocumentAsync(SonarEndpoints.AudioDevices(address), cancellationToken)
            .ConfigureAwait(false);

        return document is null
            ? Array.Empty<SonarAudioDevice>()
            : SonarAudioDevicesParser.Parse(document.RootElement);
    }

    public async Task<SonarDeviceSelection?> GetDeviceSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await _connection.RefreshModeAsync(cancellationToken).ConfigureAwait(false);
        var session = _connection.Session;
        var address = session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (session.IsStreamerMode)
        {
            using var streamDocument = await _transport
                .GetJsonDocumentAsync($"{address}/streamRedirections", cancellationToken)
                .ConfigureAwait(false);

            if (streamDocument is null)
            {
                return null;
            }

            return new SonarDeviceSelection
            {
                IsStreamerMode = true,
                OutputDeviceId = StreamMixRoutingParser.TryReadRedirectionDeviceId(
                    streamDocument.RootElement,
                    SonarEndpoints.StreamRedirectionMonitoringId),
                MicrophoneDeviceId = StreamMixRoutingParser.TryReadRedirectionDeviceId(
                    streamDocument.RootElement,
                    SonarEndpoints.StreamRedirectionMicId)
            };
        }

        using var classicDocument = await _transport
            .GetJsonDocumentAsync($"{address}/classicRedirections", cancellationToken)
            .ConfigureAwait(false);

        if (classicDocument is null)
        {
            return null;
        }

        var root = classicDocument.RootElement;
        return new SonarDeviceSelection
        {
            IsStreamerMode = false,
            OutputDeviceId = StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(root, "game")
                ?? StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(root, "media")
                ?? StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(root, "chat")
                ?? StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(root, "aux"),
            MicrophoneDeviceId = StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(root, "mic")
        };
    }

    public async Task<bool> SetOutputDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)
            || !await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _connection.RefreshModeAsync(cancellationToken).ConfigureAwait(false);
        var session = _connection.Session;
        var address = session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var url = session.IsStreamerMode
            ? SonarEndpoints.SetStreamRedirectionDevice(
                address,
                SonarEndpoints.StreamRedirectionMonitoringId,
                deviceId)
            : SonarEndpoints.SetClassicRedirectionDevice(
                address,
                SonarEndpoints.ClassicRenderDeviceChannel,
                deviceId);

        var success = await _transport.PutAsync(url, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            _connection.Invalidate();
        }

        return success;
    }

    public async Task<bool> SetMicrophoneDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)
            || !await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _connection.RefreshModeAsync(cancellationToken).ConfigureAwait(false);
        var session = _connection.Session;
        var address = session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var url = session.IsStreamerMode
            ? SonarEndpoints.SetStreamRedirectionDevice(
                address,
                SonarEndpoints.StreamRedirectionMicId,
                deviceId)
            : SonarEndpoints.SetClassicRedirectionDevice(
                address,
                SonarEndpoints.ClassicMicDeviceChannel,
                deviceId);

        var success = await _transport.PutAsync(url, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            _connection.Invalidate();
        }

        return success;
    }
}
