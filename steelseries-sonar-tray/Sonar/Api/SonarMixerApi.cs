using System.Globalization;
using System.Text.Json;

namespace SonarQuickMixer.Sonar;

internal sealed class SonarMixerApi
{
    private readonly SonarHttpTransport _transport;
    private readonly SonarConnection _connection;

    public SonarMixerApi(SonarHttpTransport transport, SonarConnection connection)
    {
        _transport = transport;
        _connection = connection;
    }

    public async Task<SonarMixerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return VolumeSettingsParser.CreateEmptySnapshot();
        }

        await _connection.RefreshModeAsync(cancellationToken).ConfigureAwait(false);

        var session = _connection.Session;
        var volumePath = SonarEndpoints.VolumeSettingsPath(session.IsStreamerMode);
        var (success, document) = await _transport
            .GetJsonDocumentOrFailAsync($"{session.WebServerAddress}{volumePath}", cancellationToken)
            .ConfigureAwait(false);

        if (!success || document is null)
        {
            _connection.Invalidate();
            return VolumeSettingsParser.CreateEmptySnapshot();
        }

        using (document)
        {
            var enabledChannels = VolumeSettingsParser.ParseEnabledChannels(document.RootElement);
            await MergeOptionalChannelsFromFeaturesAsync(enabledChannels, cancellationToken)
                .ConfigureAwait(false);
            VolumeSettingsParser.ApplyVirtualDeviceAvailability(enabledChannels);

            var channels = VolumeSettingsParser.ParseAllChannelSettings(
                document.RootElement,
                session.IsStreamerMode);

            if (session.IsStreamerMode)
            {
                channels = await MergeStreamMixRoutingAsync(channels, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SonarMixerSnapshot
            {
                IsStreamerMode = session.IsStreamerMode,
                EnabledChannels = enabledChannels,
                Channels = channels
            };
        }
    }

    public async Task<float?> GetVolumeAsync(
        string channel,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Channels.TryGetValue(channel, out var channelSettings))
        {
            return null;
        }

        return path == SonarMixerPath.Streaming
            ? channelSettings.Streaming?.Volume
            : channelSettings.Monitoring?.Volume;
    }

    public async Task<IReadOnlyDictionary<string, SonarChannelSettings>?> SetVolumeAsync(
        string channel,
        float volume,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default)
    {
        if (!await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var session = _connection.Session;
        volume = Math.Clamp(volume, 0f, 1f);
        var volumeSegment = volume.ToString("0.0########", CultureInfo.InvariantCulture);
        var url = SonarEndpoints.SetVolume(
            session.WebServerAddress!,
            channel,
            volumeSegment,
            session.IsStreamerMode,
            path);

        var (success, document) = await _transport.PutJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!success || document is null)
        {
            _connection.Invalidate();
            return null;
        }

        using (document)
        {
            return VolumeSettingsParser.ParseSettingsResponse(document.RootElement, session.IsStreamerMode);
        }
    }

    public async Task<IReadOnlyDictionary<string, SonarChannelSettings>?> SetMuteAsync(
        string channel,
        bool muted,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default)
    {
        if (!await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var session = _connection.Session;
        var muteSegment = muted ? "true" : "false";
        var url = SonarEndpoints.SetMute(
            session.WebServerAddress!,
            channel,
            muteSegment,
            session.IsStreamerMode,
            path);

        var (success, document) = await _transport.PutJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!success || document is null)
        {
            _connection.Invalidate();
            return null;
        }

        using (document)
        {
            return VolumeSettingsParser.ParseSettingsResponse(document.RootElement, session.IsStreamerMode);
        }
    }

    public async Task<IReadOnlyDictionary<string, SonarChannelSettings>?> SetMixIncludedAsync(
        string channel,
        bool included,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default)
    {
        if (!await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false)
            || !_connection.Session.IsStreamerMode)
        {
            return null;
        }

        if (!SonarChannels.MixRoutable.Contains(channel, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var url = SonarEndpoints.SetMixIncluded(
            _connection.Session.WebServerAddress!,
            channel,
            included,
            path);

        if (!await _transport.PutAsync(url, cancellationToken).ConfigureAwait(false))
        {
            _connection.Invalidate();
            return null;
        }

        return (await GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Channels;
    }

    private async Task MergeOptionalChannelsFromFeaturesAsync(
        HashSet<string> enabledChannels,
        CancellationToken cancellationToken)
    {
        var address = _connection.Session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        using var document = await _transport.GetJsonDocumentAsync($"{address}/features", cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return;
        }

        FeatureFlagsParser.ApplyOptionalChannelFlags(document.RootElement, enabledChannels);
    }

    private async Task<IReadOnlyDictionary<string, SonarChannelSettings>> MergeStreamMixRoutingAsync(
        IReadOnlyDictionary<string, SonarChannelSettings> channels,
        CancellationToken cancellationToken)
    {
        var routing = await GetStreamMixRoutingAsync(cancellationToken).ConfigureAwait(false);
        return routing is null
            ? channels
            : VolumeSettingsParser.MergeStreamMixRouting(channels, routing);
    }

    private async Task<StreamMixRouting?> GetStreamMixRoutingAsync(CancellationToken cancellationToken)
    {
        var address = _connection.Session.WebServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        using var document = await _transport
            .GetJsonDocumentAsync($"{address}/streamRedirections", cancellationToken)
            .ConfigureAwait(false);

        return document is null ? null : StreamMixRoutingParser.Parse(document.RootElement);
    }
}
