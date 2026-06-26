using NAudio.CoreAudioApi;

namespace SonarQuickMixer.Audio;

public static class SonarVirtualMicrophoneRenderProbe
{
    public const string DeviceKey = "sonar-microphone-render";

    public static bool IsSonarVirtualMicrophone(string friendlyName) =>
        friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase) &&
        friendlyName.Contains("Microphone", StringComparison.OrdinalIgnoreCase);

    public static bool IsSonarMicrophoneRenderDeviceId(string deviceId) =>
        string.Equals(deviceId, DeviceKey, StringComparison.Ordinal);

    /// <summary>
    /// Sonar Microphone as a playback (render) endpoint — not the capture device used for voice input.
    /// </summary>
    public static MMDevice? TryGetDevice()
    {
        using var enumerator = new MMDeviceEnumerator();

        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                if (IsSonarVirtualMicrophone(endpoint.FriendlyName))
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

        return null;
    }
}
