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
    private const string MicrophoneStreamRole = "mic";
    private const string MicrophoneStreamRoleAlt = "chatCapture";

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

    public async Task<SonarEchoFixRouting?> GetEchoFixRoutingAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await RefreshModeAsync(cancellationToken).ConfigureAwait(false);

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

                microphoneStreamBroadcast = TryGetRedirectionRoleEnabled(
                    streamRedirections.RootElement,
                    StreamRedirectionStreamingId,
                    MicrophoneStreamRole)
                    || TryGetRedirectionRoleEnabled(
                        streamRedirections.RootElement,
                        StreamRedirectionStreamingId,
                        MicrophoneStreamRoleAlt);

                if (_streamerMode == true)
                {
                    monitoringDeviceId = TryReadRedirectionDeviceId(
                        streamRedirections.RootElement,
                        StreamRedirectionMonitoringId);
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

        if (_streamerMode != true)
        {
            monitoringDeviceId = await GetClassicRedirectionDeviceIdAsync("game", cancellationToken)
                .ConfigureAwait(false);
        }

        return new SonarEchoFixRouting
        {
            IsStreamerMode = _streamerMode == true,
            IsStreamMonitoringEnabled = monitoringEnabled,
            IsMicrophoneStreamBroadcastEnabled = _streamerMode == true && microphoneStreamBroadcast,
            MonitoringOutputDeviceId = monitoringDeviceId
        };
    }

    private async Task<JsonDocument?> GetStreamRedirectionsDocumentAsync(CancellationToken cancellationToken)
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

            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static bool TryGetRedirectionRoleEnabled(JsonElement root, string redirectionId, string role)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var redirection in root.EnumerateArray())
        {
            if (!redirection.TryGetProperty("streamRedirectionId", out var redirectionIdElement) ||
                !string.Equals(redirectionIdElement.GetString(), redirectionId, StringComparison.OrdinalIgnoreCase) ||
                !redirection.TryGetProperty("status", out var status))
            {
                continue;
            }

            foreach (var entry in status.EnumerateArray())
            {
                if (!entry.TryGetProperty("role", out var roleElement) ||
                    !string.Equals(roleElement.GetString(), role, StringComparison.OrdinalIgnoreCase) ||
                    !entry.TryGetProperty("isEnabled", out var enabledElement) ||
                    enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    continue;
                }

                return enabledElement.GetBoolean();
            }
        }

        return false;
    }

    private async Task<bool> GetStreamMonitoringEnabledAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webServerAddress))
        {
            return false;
        }

        try
        {
            using var response = await _httpClient
                .GetAsync($"{_webServerAddress}/streamRedirections/isStreamMonitoringEnabled", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (TryParseBooleanLike(body, out var parsed))
            {
                return parsed;
            }

            using var document = JsonDocument.Parse(body);
            return TryParseBooleanElement(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> TryGetStreamMonitoringFromFeaturesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webServerAddress))
        {
            return false;
        }

        try
        {
            using var response = await _httpClient
                .GetAsync($"{_webServerAddress}/features", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return TryFindBooleanProperty(
                document.RootElement,
                out var enabled,
                "isStreamMonitoringEnabled",
                "streamMonitoringEnabled",
                "isAudienceMonitoringEnabled",
                "audienceMonitoringEnabled") && enabled;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private static bool TryGetMicrophoneMonitoringEnabled(JsonElement streamRedirectionsRoot) =>
        TryGetRedirectionRoleEnabled(
            streamRedirectionsRoot,
            StreamRedirectionMonitoringId,
            MicrophoneStreamRole)
        || TryGetRedirectionRoleEnabled(
            streamRedirectionsRoot,
            StreamRedirectionMonitoringId,
            MicrophoneStreamRoleAlt);

    private static bool TryResolveAudienceMonitoringEnabled(JsonElement streamRedirectionsRoot) =>
        TryFindBooleanProperty(
            streamRedirectionsRoot,
            out var enabled,
            "isStreamMonitoringEnabled",
            "streamMonitoringEnabled",
            "isAudienceMonitoringEnabled",
            "audienceMonitoringEnabled") && enabled;

    private static bool TryFindBooleanProperty(JsonElement element, params string[] propertyNames) =>
        TryFindBooleanProperty(element, out _, propertyNames);

    private static bool TryFindBooleanProperty(
        JsonElement element,
        out bool enabled,
        params string[] propertyNames)
    {
        enabled = false;
        return TryFindBooleanProperty(element, propertyNames, depth: 0, out enabled);
    }

    private static bool TryFindBooleanProperty(JsonElement element, string[] propertyNames, int depth)
    {
        if (depth > 8)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (element.TryGetProperty(propertyName, out var flag) &&
                    TryParseBooleanElement(flag, out var enabled))
                {
                    return enabled;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindBooleanProperty(property.Value, propertyNames, depth + 1, out var enabled))
                {
                    return enabled;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (TryFindBooleanProperty(child, propertyNames, depth + 1, out var enabled))
                {
                    return enabled;
                }
            }
        }

        return false;
    }

    private static bool TryFindBooleanProperty(
        JsonElement element,
        string[] propertyNames,
        int depth,
        out bool enabled)
    {
        enabled = false;

        if (depth > 8)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (element.TryGetProperty(propertyName, out var flag) &&
                    TryParseBooleanElement(flag, out enabled))
                {
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindBooleanProperty(property.Value, propertyNames, depth + 1, out enabled))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (TryFindBooleanProperty(child, propertyNames, depth + 1, out enabled))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseBooleanLike(string value, out bool result)
    {
        result = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim().Trim('"');
        if (bool.TryParse(value, out result))
        {
            return true;
        }

        if (int.TryParse(value, out var number))
        {
            result = number != 0;
            return true;
        }

        return false;
    }

    private static bool TryParseBooleanElement(JsonElement element) =>
        TryParseBooleanElement(element, out _);

    private static bool TryParseBooleanElement(JsonElement element, out bool result)
    {
        result = false;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String:
                return TryParseBooleanLike(element.GetString() ?? string.Empty, out result);
            case JsonValueKind.Number:
                result = element.TryGetInt32(out var number) ? number != 0 : Math.Abs(element.GetDouble()) > 0.0001d;
                return true;
            case JsonValueKind.Object:
                if (element.TryGetProperty("value", out var value) && TryParseBooleanElement(value, out result))
                {
                    return true;
                }

                if (element.TryGetProperty("enabled", out var enabled) && TryParseBooleanElement(enabled, out result))
                {
                    return true;
                }

                if (element.TryGetProperty("isEnabled", out var isEnabled) &&
                    TryParseBooleanElement(isEnabled, out result))
                {
                    return true;
                }

                break;
        }

        return false;
    }

    private async Task<string?> GetStreamRedirectionDeviceIdAsync(
        string redirectionId,
        CancellationToken cancellationToken)
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

            return TryReadRedirectionDeviceId(document.RootElement, redirectionId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<string?> GetClassicRedirectionDeviceIdAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webServerAddress))
        {
            return null;
        }

        try
        {
            using var response = await _httpClient
                .GetAsync($"{_webServerAddress}/classicRedirections", cancellationToken)
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

            return TryReadClassicRedirectionDeviceId(document.RootElement, channel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string? TryReadRedirectionDeviceId(JsonElement root, string redirectionId)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var redirection in root.EnumerateArray())
        {
            if (!redirection.TryGetProperty("streamRedirectionId", out var idElement))
            {
                continue;
            }

            if (!string.Equals(idElement.GetString(), redirectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (redirection.TryGetProperty("deviceId", out var deviceIdElement))
            {
                return deviceIdElement.GetString();
            }
        }

        return null;
    }

    private static string? TryReadClassicRedirectionDeviceId(JsonElement root, string channel)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty(channel, out var channelElement))
        {
            return null;
        }

        if (channelElement.TryGetProperty("deviceId", out var deviceIdElement))
        {
            return deviceIdElement.GetString();
        }

        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}
