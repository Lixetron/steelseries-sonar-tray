using System.Text.Json;

namespace SonarQuickMixer.Sonar;

internal static class SonarAudioDevicesParser
{
    public static IReadOnlyList<SonarAudioDevice> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SonarAudioDevice>();
        }

        var devices = new List<SonarAudioDevice>();
        foreach (var entry in root.EnumerateArray())
        {
            if (!TryParseDevice(entry, out var device) || device is null)
            {
                continue;
            }

            devices.Add(device);
        }

        return devices;
    }

    public static IReadOnlyList<SonarAudioDevice> FilterPhysical(
        IEnumerable<SonarAudioDevice> devices,
        SonarAudioDataFlow dataFlow) =>
        devices
            .Where(device => device.DataFlow == dataFlow && device.IsActivePhysicalDevice)
            .OrderBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static bool TryParseDevice(JsonElement entry, out SonarAudioDevice? device)
    {
        device = null;
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!entry.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var friendlyName = entry.TryGetProperty("friendlyName", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            friendlyName = id;
        }

        if (!entry.TryGetProperty("dataFlow", out var flowElement)
            || flowElement.ValueKind != JsonValueKind.String
            || !TryParseDataFlow(flowElement.GetString(), out var dataFlow))
        {
            return false;
        }

        var isVad = entry.TryGetProperty("isVad", out var vadElement)
            && vadElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && vadElement.GetBoolean();

        var state = entry.TryGetProperty("state", out var stateElement)
            && stateElement.ValueKind == JsonValueKind.String
                ? stateElement.GetString()
                : null;

        device = new SonarAudioDevice
        {
            Id = id,
            FriendlyName = friendlyName!,
            DataFlow = dataFlow,
            IsVad = isVad,
            State = state
        };
        return true;
    }

    private static bool TryParseDataFlow(string? value, out SonarAudioDataFlow dataFlow)
    {
        if (string.Equals(value, "render", StringComparison.OrdinalIgnoreCase))
        {
            dataFlow = SonarAudioDataFlow.Render;
            return true;
        }

        if (string.Equals(value, "capture", StringComparison.OrdinalIgnoreCase))
        {
            dataFlow = SonarAudioDataFlow.Capture;
            return true;
        }

        dataFlow = default;
        return false;
    }
}
