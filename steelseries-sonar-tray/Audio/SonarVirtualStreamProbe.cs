using NAudio.CoreAudioApi;

namespace SonarQuickMixer.Audio;

public static class SonarVirtualStreamProbe
{
    public const string DeviceKey = "sonar-stream";

    public static bool IsSonarStreamDevice(string friendlyName) =>
        friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase) &&
        friendlyName.Contains("Stream", StringComparison.OrdinalIgnoreCase) &&
        !friendlyName.Contains("Microphone", StringComparison.OrdinalIgnoreCase);

    public static bool IsSonarStreamDeviceId(string deviceId) =>
        string.Equals(deviceId, DeviceKey, StringComparison.Ordinal);

    public static MMDevice? TryGetDevice()
    {
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    if (IsSonarStreamDevice(endpoint.FriendlyName))
                    {
                        return endpoint;
                    }
                }
                catch
                {
                    // Ignore broken endpoints and keep scanning.
                }

                endpoint.Dispose();
            }
        }

        return null;
    }
}
