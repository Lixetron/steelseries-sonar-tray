using System.Text.Json;

namespace SonarQuickMixer.Sonar;

internal static class StreamMixRoutingParser
{
    public static StreamMixRouting Parse(JsonElement root)
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
                SonarEndpoints.StreamRedirectionMonitoringId => routing.Monitoring,
                SonarEndpoints.StreamRedirectionStreamingId => routing.Streaming,
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

    public static bool TryGetRedirectionRoleEnabled(JsonElement root, string redirectionId, string role)
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

    public static string? TryReadRedirectionDeviceId(JsonElement root, string redirectionId)
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

    public static string? TryReadClassicRedirectionDeviceId(JsonElement root, string channel)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var idElement)
                    || !string.Equals(idElement.GetString(), channel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.TryGetProperty("deviceId", out var deviceIdElement)
                    && deviceIdElement.ValueKind == JsonValueKind.String)
                {
                    var deviceId = deviceIdElement.GetString();
                    return string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
                }

                return null;
            }

            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty(channel, out var channelElement))
        {
            return null;
        }

        if (channelElement.TryGetProperty("deviceId", out var objectDeviceIdElement)
            && objectDeviceIdElement.ValueKind == JsonValueKind.String)
        {
            var deviceId = objectDeviceIdElement.GetString();
            return string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        }

        return null;
    }

    public static SonarChannelState? WithMixIncluded(SonarChannelState? state, bool? mixIncluded)
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
}
