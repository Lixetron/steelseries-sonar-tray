namespace SonarQuickMixer.Sonar;

internal static class SonarEndpoints
{
    public const string StreamerMonitoringPath = "monitoring";
    public const string StreamerStreamingPath = "streaming";
    public const string StreamRedirectionMonitoringId = "monitoring";
    public const string StreamRedirectionStreamingId = "streaming";
    public const string MicrophoneStreamRole = "mic";
    public const string MicrophoneStreamRoleAlt = "chatCapture";

    public static string VolumeSettingsPath(bool streamerMode) =>
        streamerMode ? "/volumeSettings/streamer" : "/volumeSettings/classic";

    public static string SetVolume(string baseUrl, string channel, string volumeSegment, bool streamerMode, SonarMixerPath path)
    {
        if (streamerMode)
        {
            var mixerPath = path == SonarMixerPath.Streaming
                ? StreamerStreamingPath
                : StreamerMonitoringPath;
            return $"{baseUrl}/volumeSettings/streamer/{mixerPath}/{channel}/Volume/{volumeSegment}";
        }

        return $"{baseUrl}/volumeSettings/classic/{channel}/Volume/{volumeSegment}";
    }

    public static string SetMute(string baseUrl, string channel, string muteSegment, bool streamerMode, SonarMixerPath path)
    {
        if (streamerMode)
        {
            var mixerPath = path == SonarMixerPath.Streaming
                ? StreamerStreamingPath
                : StreamerMonitoringPath;
            return $"{baseUrl}/volumeSettings/streamer/{mixerPath}/{channel}/isMuted/{muteSegment}";
        }

        return $"{baseUrl}/volumeSettings/classic/{channel}/Mute/{muteSegment}";
    }

    public static string SetMixIncluded(
        string baseUrl,
        string channel,
        bool included,
        SonarMixerPath path)
    {
        var redirectionId = path == SonarMixerPath.Streaming
            ? StreamRedirectionStreamingId
            : StreamRedirectionMonitoringId;
        var includedSegment = included ? "true" : "false";
        return $"{baseUrl}/streamRedirections/{redirectionId}/redirections/{channel}/isEnabled/{includedSegment}";
    }
}
