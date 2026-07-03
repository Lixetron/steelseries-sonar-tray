using System.Text.Json;
using SonarQuickMixer.Audio;

namespace SonarQuickMixer.Sonar;

internal static class FeatureFlagsParser
{
    public static void ApplyOptionalChannelFlags(JsonElement element, HashSet<string> enabledChannels, int depth = 0)
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
}
