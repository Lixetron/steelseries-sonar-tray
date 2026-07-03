using System.Text.Json;

namespace SonarQuickMixer.Sonar;

internal sealed class SonarEchoFixApi
{
    private readonly SonarHttpTransport _transport;
    private readonly SonarConnection _connection;

    public SonarEchoFixApi(SonarHttpTransport transport, SonarConnection connection)
    {
        _transport = transport;
        _connection = connection;
    }

    public async Task<SonarEchoFixRouting?> GetRoutingAsync(CancellationToken cancellationToken = default)
    {
        if (!await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await _connection.RefreshModeAsync(cancellationToken).ConfigureAwait(false);
        var session = _connection.Session;

        var microphoneStreamBroadcast = false;
        string? monitoringDeviceId = null;
        var monitoringEnabled = false;

        using (var streamRedirections = await GetStreamRedirectionsDocumentAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            if (streamRedirections is not null)
            {
                monitoringEnabled = TryGetMicrophoneMonitoringEnabled(streamRedirections.RootElement);

                if (!monitoringEnabled)
                {
                    monitoringEnabled = TryResolveAudienceMonitoringEnabled(streamRedirections.RootElement);
                }

                microphoneStreamBroadcast = StreamMixRoutingParser.TryGetRedirectionRoleEnabled(
                    streamRedirections.RootElement,
                    SonarEndpoints.StreamRedirectionStreamingId,
                    SonarEndpoints.MicrophoneStreamRole)
                    || StreamMixRoutingParser.TryGetRedirectionRoleEnabled(
                        streamRedirections.RootElement,
                        SonarEndpoints.StreamRedirectionStreamingId,
                        SonarEndpoints.MicrophoneStreamRoleAlt);

                if (session.IsStreamerMode)
                {
                    monitoringDeviceId = StreamMixRoutingParser.TryReadRedirectionDeviceId(
                        streamRedirections.RootElement,
                        SonarEndpoints.StreamRedirectionMonitoringId);
                }
            }
        }

        if (!monitoringEnabled)
        {
            monitoringEnabled = await GetStreamMonitoringEnabledAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!monitoringEnabled)
        {
            monitoringEnabled = await TryGetStreamMonitoringFromFeaturesAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (!session.IsStreamerMode)
        {
            monitoringDeviceId = await GetClassicRedirectionDeviceIdAsync("game", cancellationToken)
                .ConfigureAwait(false);
        }

        return new SonarEchoFixRouting
        {
            IsStreamerMode = session.IsStreamerMode,
            IsStreamMonitoringEnabled = monitoringEnabled,
            IsMicrophoneStreamBroadcastEnabled = session.IsStreamerMode && microphoneStreamBroadcast,
            MonitoringOutputDeviceId = monitoringDeviceId
        };
    }

    private async Task<JsonDocument?> GetStreamRedirectionsDocumentAsync(CancellationToken cancellationToken)
    {
        var address = _connection.Session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        return await _transport
            .GetJsonDocumentAsync($"{address}/streamRedirections", cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryGetMicrophoneMonitoringEnabled(JsonElement streamRedirectionsRoot) =>
        StreamMixRoutingParser.TryGetRedirectionRoleEnabled(
            streamRedirectionsRoot,
            SonarEndpoints.StreamRedirectionMonitoringId,
            SonarEndpoints.MicrophoneStreamRole)
        || StreamMixRoutingParser.TryGetRedirectionRoleEnabled(
            streamRedirectionsRoot,
            SonarEndpoints.StreamRedirectionMonitoringId,
            SonarEndpoints.MicrophoneStreamRoleAlt);

    private static bool TryResolveAudienceMonitoringEnabled(JsonElement streamRedirectionsRoot) =>
        JsonBooleanParser.TryFindBooleanProperty(
            streamRedirectionsRoot,
            out var enabled,
            "isStreamMonitoringEnabled",
            "streamMonitoringEnabled",
            "isAudienceMonitoringEnabled",
            "audienceMonitoringEnabled") && enabled;

    private async Task<bool> GetStreamMonitoringEnabledAsync(CancellationToken cancellationToken)
    {
        var address = _connection.Session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var (success, body) = await _transport
            .GetStringOrFailAsync($"{address}/streamRedirections/isStreamMonitoringEnabled", cancellationToken)
            .ConfigureAwait(false);

        if (!success || body is null)
        {
            return false;
        }

        var trimmed = body.Trim();
        if (JsonBooleanParser.TryParseBooleanLike(trimmed, out var parsed))
        {
            return parsed;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return JsonBooleanParser.TryParseBooleanElement(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<bool> TryGetStreamMonitoringFromFeaturesAsync(CancellationToken cancellationToken)
    {
        var address = _connection.Session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        using var document = await _transport.GetJsonDocumentAsync($"{address}/features", cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return false;
        }

        return JsonBooleanParser.TryFindBooleanProperty(
            document.RootElement,
            out var enabled,
            "isStreamMonitoringEnabled",
            "streamMonitoringEnabled",
            "isAudienceMonitoringEnabled",
            "audienceMonitoringEnabled") && enabled;
    }

    private async Task<string?> GetClassicRedirectionDeviceIdAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        var address = _connection.Session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        using var document = await _transport
            .GetJsonDocumentAsync($"{address}/classicRedirections", cancellationToken)
            .ConfigureAwait(false);

        return document is null
            ? null
            : StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(document.RootElement, channel);
    }
}
