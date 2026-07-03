using System.Text.Json;
using SonarQuickMixer.Audio;

namespace SonarQuickMixer.Sonar;

internal static class VolumeSettingsParser
{
    public static HashSet<string> ParseEnabledChannels(JsonElement root)
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

    public static void ApplyVirtualDeviceAvailability(HashSet<string> enabledChannels)
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

    public static IReadOnlyDictionary<string, SonarChannelSettings> ParseAllChannelSettings(
        JsonElement root,
        bool streamerMode)
    {
        var results = new Dictionary<string, SonarChannelSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in SonarChannels.All)
        {
            results[channel] = new SonarChannelSettings
            {
                Monitoring = ParseChannelState(root, channel, streamerMode, SonarEndpoints.StreamerMonitoringPath),
                Streaming = streamerMode
                    ? ParseChannelState(root, channel, streamerMode, SonarEndpoints.StreamerStreamingPath)
                    : null
            };
        }

        return results;
    }

    public static IReadOnlyDictionary<string, SonarChannelSettings> ParseSettingsResponse(
        JsonElement root,
        bool streamerMode)
    {
        var all = ParseAllChannelSettings(root, streamerMode);
        return all
            .Where(pair => pair.Value.Monitoring is not null || pair.Value.Streaming is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, SonarChannelSettings> MergeStreamMixRouting(
        IReadOnlyDictionary<string, SonarChannelSettings> channels,
        StreamMixRouting routing)
    {
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
                Monitoring = StreamMixRoutingParser.WithMixIncluded(
                    settings.Monitoring,
                    routing.Monitoring.ContainsKey(channel) ? monitoringIncluded : null),
                Streaming = StreamMixRoutingParser.WithMixIncluded(
                    settings.Streaming,
                    routing.Streaming.ContainsKey(channel) ? streamingIncluded : null)
            };
        }

        return updated;
    }

    public static SonarMixerSnapshot CreateEmptySnapshot()
    {
        var channels = SonarChannels.All.ToDictionary(
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

    private static bool IsMixerDeviceName(string deviceName) =>
        SonarChannels.MasterProportional.Contains(deviceName, StringComparer.OrdinalIgnoreCase);

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
}
