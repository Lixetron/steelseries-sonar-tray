using NAudio.CoreAudioApi;

namespace SonarQuickMixer.Audio;

public static class WindowsAudioDeviceProbe
{
    public static bool IsSonarVirtualDevice(string friendlyName) =>
        friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase);

    public static MMDevice? TryGetDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(deviceId);
        }
        catch
        {
            return null;
        }
    }
}
