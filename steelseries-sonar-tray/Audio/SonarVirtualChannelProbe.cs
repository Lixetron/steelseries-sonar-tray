using NAudio.CoreAudioApi;

namespace SteelSeries.SonarTray.Audio;

internal static class SonarVirtualChannelMap
{
    internal static readonly Dictionary<string, string> DeviceFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["game"] = "Game",
        ["chatRender"] = "Chat",
        ["media"] = "Media",
        ["aux"] = "Aux",
    };

    internal static bool TryMatchChannel(string friendlyName, out string channel)
    {
        channel = string.Empty;

        if (!friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase) ||
            friendlyName.Contains("Microphone", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var (channelName, fragment) in DeviceFragments)
        {
            if (friendlyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                channel = channelName;
                return true;
            }
        }

        return false;
    }
}

public static class SonarVirtualChannelProbe
{
    public static HashSet<string> GetPresentChannels()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var enumerator = new MMDeviceEnumerator();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                if (SonarVirtualChannelMap.TryMatchChannel(endpoint.FriendlyName, out var channel))
                {
                    present.Add(channel);
                }
            }
            finally
            {
                endpoint.Dispose();
            }
        }

        return present;
    }
}
