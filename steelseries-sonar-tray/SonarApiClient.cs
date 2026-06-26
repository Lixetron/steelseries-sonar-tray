using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using SonarQuickMixer.Audio;

namespace SonarQuickMixer;

public sealed class SonarApiClient : IDisposable
{
    private static readonly string[] ChannelNames = SonarChannels.All;

    private const string StreamerMonitoringPath = "monitoring";
    private const string StreamerStreamingPath = "streaming";
    private const string StreamRedirectionMonitoringId = "monitoring";
    private const string StreamRedirectionStreamingId = "streaming";

    private static readonly string CorePropsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteelSeries", "SteelSeries Engine 3", "coreProps.json");

    private static readonly string SubAppsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteelSeries", "SteelSeries GG", "subApps.json");

    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private string? _webServerAddress;
    private bool? _streamerMode;

    public bool IsConnected => !string.IsNullOrWhiteSpace(_webServerAddress);

    public int? Port => TryParsePort(_webServerAddress);

    public async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_webServerAddress))
        {
            return true;
        }

        var address = await ResolveWebServerAddressAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        _webServerAddress = address;
        _streamerMode = await DetectStreamerModeAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void InvalidateConnection()
    {
        _webServerAddress = null;
        _streamerMode = null;
    }

    public bool IsStreamerMode => _streamerMode == true;

    public async Task<SonarMixerSnapshot> GetMixerSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return CreateEmptySnapshot();
        }

        await RefreshModeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var volumePath = GetVolumeSettingsPath();
            using var response = await _httpClient
                .GetAsync($"{_webServerAddress}{volumePath}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                InvalidateConnection();
                return CreateEmptySnapshot();
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var enabledChannels = ParseEnabledChannels(document.RootElement);
            await MergeOptionalChannelsFromFeaturesAsync(enabledChannels, cancellationToken)
                .ConfigureAwait(false);
            ApplyVirtualDeviceAvailability(enabledChannels);

            var channels = ParseAllChannelSettings(document.RootElement);
            if (_streamerMode == true)
            {
                channels = await MergeStreamMixRoutingAsync(channels, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SonarMixerSnapshot
            {
                IsStreamerMode = _streamerMode == true,
                EnabledChannels = enabledChannels,
                Channels = channels
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            InvalidateConnection();
            return CreateEmptySnapshot();
        }
    }

    public async Task<bool> RefreshModeAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_webServerAddress))
        {
            return false;
        }

        var streamerMode = await DetectStreamerModeAsync(cancellationToken).ConfigureAwait(false);
        _streamerMode = streamerMode;
        return true;
    }

    public async Task<IReadOnlyDictionary<string, SonarChannelSettings>> GetAllChannelSettingsAsync(
        CancellationToken cancellationToken = default) =>
        (await GetMixerSnapshotAsync(cancellationToken).ConfigureAwait(false)).Channels;

    private static SonarMixerSnapshot CreateEmptySnapshot()
    {
        var channels = ChannelNames.ToDictionary(
            channel => channel,
            _ => new SonarChannelSettings(),
            StringComparer.OrdinalIgnoreCase);

        return new SonarMixerSnapshot
        {
            IsStreamerMode = false,
            EnabledChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Channels = channels
        };
    }

    private static HashSet<string> ParseEnabledChannels(JsonElement root)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("masters", out _))
        {
            enabled.Add("master");
        }

        if (!root.TryGetProperty("devices", out var devices))
        {
            return enabled;
        }

        foreach (var deviceProperty in devices.EnumerateObject())
        {
            if (!IsMixerDeviceName(deviceProperty.Name))
            {
                continue;
            }

            if (IsChannelDeviceActive(deviceProperty.Value))
            {
                enabled.Add(deviceProperty.Name);
            }
        }

        return enabled;
    }

    private static bool IsMixerDeviceName(string deviceName) =>
        SonarChannels.MasterProportional.Contains(deviceName, StringComparer.OrdinalIgnoreCase);

    private static void ApplyVirtualDeviceAvailability(HashSet<string> enabledChannels)
    {
        var presentChannels = SonarVirtualChannelProbe.GetPresentChannels();

        foreach (var channel in SonarChannels.Optional)
        {
            if (!presentChannels.Contains(channel))
            {
                enabledChannels.Remove(channel);
            }
        }
    }

    private static bool IsChannelDeviceActive(JsonElement device)
    {
        if (device.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in new[] { "isEnabled", "enabled", "isChannelEnabled", "channelEnabled" })
        {
            if (device.TryGetProperty(propertyName, out var enabledFlag) &&
                enabledFlag.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                !enabledFlag.GetBoolean())
            {
                return false;
            }
        }

        return true;
    }

    private async Task MergeOptionalChannelsFromFeaturesAsync(
        HashSet<string> enabledChannels,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webServerAddress))
        {
            return;
        }

        try
        {
            using var response = await _httpClient
                .GetAsync($"{_webServerAddress}/features", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            ApplyOptionalChannelFlags(document.RootElement, enabledChannels);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // /features is optional; volumeSettings remains the primary source.
        }
    }

    private static void ApplyOptionalChannelFlags(JsonElement element, HashSet<string> enabledChannels, int depth = 0)
    {
        if (depth > 6 || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var channel in SonarChannels.Optional)
        {
            if (TryReadOptionalChannelFlag(element, channel, out var isEnabled))
            {
                if (isEnabled)
                {
                    enabledChannels.Add(channel);
                }
                else
                {
                    enabledChannels.Remove(channel);
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            ApplyOptionalChannelFlags(property.Value, enabledChannels, depth + 1);
        }
    }

    private static bool TryReadOptionalChannelFlag(JsonElement element, string channel, out bool isEnabled)
    {
        isEnabled = false;

        foreach (var propertyName in GetOptionalChannelFlagNames(channel))
        {
            if (element.TryGetProperty(propertyName, out var flag) &&
                flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isEnabled = flag.GetBoolean();
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetOptionalChannelFlagNames(string channel)
    {
        var titleCase = char.ToUpperInvariant(channel[0]) + channel[1..];
        yield return $"{channel}ChannelEnabled";
        yield return $"{channel}Enabled";
        yield return $"is{titleCase}ChannelEnabled";
        yield return $"is{titleCase}Enabled";
        yield return $"{channel}IsEnabled";
    }

    public async Task<float?> GetVolumeAsync(
        string channel,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetAllChannelSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.TryGetValue(channel, out var channelSettings))
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
        if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        volume = Math.Clamp(volume, 0f, 1f);
        var volumeSegment = volume.ToString("0.0########", CultureInfo.InvariantCulture);
        var url = BuildSetVolumeUrl(channel, volumeSegment, path);

        try
        {
            using var response = await _httpClient
                .PutAsync(url, content: null, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                InvalidateConnection();
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return ParseSettingsResponse(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            InvalidateConnection();
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, SonarChannelSettings>?> SetMixIncludedAsync(
        string channel,
        bool included,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false) || _streamerMode != true)
        {
            return null;
        }

        if (!SonarChannels.MixRoutable.Contains(channel, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var redirectionId = path == SonarMixerPath.Streaming
            ? StreamRedirectionStreamingId
            : StreamRedirectionMonitoringId;
        var includedSegment = included ? "true" : "false";
        var url = $"{_webServerAddress}/streamRedirections/{redirectionId}/redirections/{channel}/isEnabled/{includedSegment}";

        try
        {
            using var response = await _httpClient
                .PutAsync(url, content: null, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                InvalidateConnection();
                return null;
            }

            return (await GetMixerSnapshotAsync(cancellationToken).ConfigureAwait(false)).Channels;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            InvalidateConnection();
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, SonarChannelSettings>?> SetMuteAsync(
        string channel,
        bool muted,
        SonarMixerPath path = SonarMixerPath.Monitoring,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var muteSegment = muted ? "true" : "false";
        var url = BuildSetMuteUrl(channel, muteSegment, path);

        try
        {
            using var response = await _httpClient
                .PutAsync(url, content: null, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                InvalidateConnection();
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return ParseSettingsResponse(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            InvalidateConnection();
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, SonarChannelSettings>> MergeStreamMixRoutingAsync(
        IReadOnlyDictionary<string, SonarChannelSettings> channels,
        CancellationToken cancellationToken)
    {
        var routing = await GetStreamMixRoutingAsync(cancellationToken).ConfigureAwait(false);
        if (routing is null)
        {
            return channels;
        }

        var updated = new Dictionary<string, SonarChannelSettings>(channels, StringComparer.OrdinalIgnoreCase);

        foreach (var channel in SonarChannels.MixRoutable)
        {
            if (!updated.TryGetValue(channel, out var settings))
            {
                continue;
            }

            routing.Monitoring.TryGetValue(channel, out var monitoringIncluded);
            routing.Streaming.TryGetValue(channel, out var streamingIncluded);

            updated[channel] = new SonarChannelSettings
            {
                Monitoring = WithMixIncluded(
                    settings.Monitoring,
                    routing.Monitoring.ContainsKey(channel) ? monitoringIncluded : null),
                Streaming = WithMixIncluded(
                    settings.Streaming,
                    routing.Streaming.ContainsKey(channel) ? streamingIncluded : null)
            };
        }

        return updated;
    }

    private async Task<StreamMixRouting?> GetStreamMixRoutingAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webServerAddress))
        {
            return null;
        }

        try
        {
            using var response = await _httpClient
                .GetAsync($"{_webServerAddress}/streamRedirections", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return ParseStreamMixRouting(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static StreamMixRouting ParseStreamMixRouting(JsonElement root)
    {
        var routing = new StreamMixRouting();

        if (root.ValueKind != JsonValueKind.Array)
        {
            return routing;
        }

        foreach (var redirection in root.EnumerateArray())
        {
            if (!redirection.TryGetProperty("streamRedirectionId", out var redirectionIdElement))
            {
                continue;
            }

            var redirectionId = redirectionIdElement.GetString();
            Dictionary<string, bool>? target = redirectionId switch
            {
                StreamRedirectionMonitoringId => routing.Monitoring,
                StreamRedirectionStreamingId => routing.Streaming,
                _ => null
            };

            if (target is null || !redirection.TryGetProperty("status", out var status))
            {
                continue;
            }

            foreach (var entry in status.EnumerateArray())
            {
                if (!entry.TryGetProperty("role", out var roleElement) ||
                    !entry.TryGetProperty("isEnabled", out var enabledElement) ||
                    enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    continue;
                }

                var role = roleElement.GetString();
                if (!string.IsNullOrWhiteSpace(role))
                {
                    target[role] = enabledElement.GetBoolean();
                }
            }
        }

        return routing;
    }

    private static SonarChannelState? WithMixIncluded(SonarChannelState? state, bool? mixIncluded)
    {
        if (mixIncluded is null)
        {
            return state;
        }

        if (state is null)
        {
            return new SonarChannelState
            {
                MixIncluded = mixIncluded
            };
        }

        return new SonarChannelState
        {
            Volume = state.Volume,
            Muted = state.Muted,
            MixIncluded = mixIncluded
        };
    }

    private sealed class StreamMixRouting
    {
        public Dictionary<string, bool> Monitoring { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, bool> Streaming { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, SonarChannelSettings> ParseAllChannelSettings(JsonElement root)
    {
        var streamerMode = _streamerMode == true;
        var results = new Dictionary<string, SonarChannelSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in ChannelNames)
        {
            results[channel] = new SonarChannelSettings
            {
                Monitoring = ParseChannelState(root, channel, streamerMode, StreamerMonitoringPath),
                Streaming = streamerMode
                    ? ParseChannelState(root, channel, streamerMode, StreamerStreamingPath)
                    : null
            };
        }

        return results;
    }

    private IReadOnlyDictionary<string, SonarChannelSettings> ParseSettingsResponse(JsonElement root)
    {
        var all = ParseAllChannelSettings(root);
        return all
            .Where(pair => pair.Value.Monitoring is not null || pair.Value.Streaming is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private string BuildSetVolumeUrl(string channel, string volumeSegment, SonarMixerPath path)
    {
        if (_streamerMode == true)
        {
            var mixerPath = path == SonarMixerPath.Streaming
                ? StreamerStreamingPath
                : StreamerMonitoringPath;
            return $"{_webServerAddress}/volumeSettings/streamer/{mixerPath}/{channel}/Volume/{volumeSegment}";
        }

        return $"{_webServerAddress}/volumeSettings/classic/{channel}/Volume/{volumeSegment}";
    }

    private string BuildSetMuteUrl(string channel, string muteSegment, SonarMixerPath path)
    {
        if (_streamerMode == true)
        {
            var mixerPath = path == SonarMixerPath.Streaming
                ? StreamerStreamingPath
                : StreamerMonitoringPath;
            return $"{_webServerAddress}/volumeSettings/streamer/{mixerPath}/{channel}/isMuted/{muteSegment}";
        }

        return $"{_webServerAddress}/volumeSettings/classic/{channel}/Mute/{muteSegment}";
    }

    private string GetVolumeSettingsPath() =>
        _streamerMode == true ? "/volumeSettings/streamer" : "/volumeSettings/classic";

    private async Task<string?> ResolveWebServerAddressAsync(CancellationToken cancellationToken)
    {
        var ggBaseUrl = TryLoadGgBaseUrl();
        if (ggBaseUrl is not null)
        {
            var fromGg = await TryGetWebServerAddressFromGgAsync(ggBaseUrl, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromGg))
            {
                return fromGg;
            }
        }

        return TryGetWebServerAddressFromLocalFile();
    }

    private static string? TryLoadGgBaseUrl()
    {
        if (!File.Exists(CorePropsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(CorePropsPath));
            if (document.RootElement.TryGetProperty("ggEncryptedAddress", out var addressElement))
            {
                var address = addressElement.GetString();
                if (!string.IsNullOrWhiteSpace(address))
                {
                    return $"https://{address.Trim()}";
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private async Task<string?> TryGetWebServerAddressFromGgAsync(
        string ggBaseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync($"{ggBaseUrl}/subApps", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return TryReadWebServerAddress(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string? TryGetWebServerAddressFromLocalFile()
    {
        if (!File.Exists(SubAppsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SubAppsPath));
            return TryReadWebServerAddress(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadWebServerAddress(JsonElement root)
    {
        if (!root.TryGetProperty("subApps", out var subApps) ||
            !subApps.TryGetProperty("sonar", out var sonar))
        {
            return null;
        }

        if (sonar.TryGetProperty("isEnabled", out var isEnabled) && !isEnabled.GetBoolean())
        {
            return null;
        }

        if (sonar.TryGetProperty("isReady", out var isReady) && !isReady.GetBoolean())
        {
            return null;
        }

        if (sonar.TryGetProperty("isRunning", out var isRunning) && !isRunning.GetBoolean())
        {
            return null;
        }

        if (!sonar.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("webServerAddress", out var webServerAddress))
        {
            return null;
        }

        var address = webServerAddress.GetString();
        return string.IsNullOrWhiteSpace(address) || address == "null" ? null : address;
    }

    private async Task<bool> DetectStreamerModeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webServerAddress))
        {
            return false;
        }

        try
        {
            using var response = await _httpClient
                .GetAsync($"{_webServerAddress}/mode", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return true;
            }

            var mode = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim('"');
            return string.Equals(mode, "stream", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return true;
        }
    }

    private static SonarChannelState? ParseChannelState(
        JsonElement root,
        string channel,
        bool streamerMode,
        string streamerPath)
    {
        if (!TryGetChannelMixerElement(root, channel, streamerMode, streamerPath, out var mixerElement))
        {
            return null;
        }

        float? volume = null;
        if (mixerElement.TryGetProperty("volume", out var volumeElement))
        {
            volume = volumeElement.GetSingle();
        }

        return new SonarChannelState
        {
            Volume = volume,
            Muted = TryGetMute(mixerElement)
        };
    }

    private static bool TryGetChannelMixerElement(
        JsonElement root,
        string channel,
        bool streamerMode,
        string streamerPath,
        out JsonElement mixerElement)
    {
        mixerElement = default;

        JsonElement parent;
        if (string.Equals(channel, "master", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("masters", out parent))
            {
                return false;
            }
        }
        else if (!root.TryGetProperty("devices", out var devices) ||
                 !devices.TryGetProperty(channel, out parent))
        {
            return false;
        }

        if (streamerMode)
        {
            if (!parent.TryGetProperty("stream", out var stream) ||
                !stream.TryGetProperty(streamerPath, out mixerElement))
            {
                return false;
            }

            return true;
        }

        if (!parent.TryGetProperty("classic", out mixerElement))
        {
            return false;
        }

        return true;
    }

    private static bool? TryGetMute(JsonElement mixerElement)
    {
        if (mixerElement.TryGetProperty("muted", out var muted) &&
            muted.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return muted.GetBoolean();
        }

        if (mixerElement.TryGetProperty("isMuted", out var isMuted) &&
            isMuted.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return isMuted.GetBoolean();
        }

        if (mixerElement.TryGetProperty("mute", out var mute) &&
            mute.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return mute.GetBoolean();
        }

        if (mixerElement.TryGetProperty("Mute", out var muteCapitalized) &&
            muteCapitalized.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return muteCapitalized.GetBoolean();
        }

        return null;
    }

    private static int? TryParsePort(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.IsDefaultPort ? null : uri.Port;
    }

    public void Dispose() => _httpClient.Dispose();
}
