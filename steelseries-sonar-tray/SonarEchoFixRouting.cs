namespace SonarQuickMixer;

public sealed class SonarEchoFixRouting
{
    public required bool IsStreamerMode { get; init; }

    /// <summary>
    /// Headphones icon above the microphone in Sonar: mic routed to the personal monitoring mix
    /// (streamRedirections → monitoring → chatCapture/mic → isEnabled).
    /// </summary>
    public required bool IsStreamMonitoringEnabled { get; init; }

    /// <summary>
    /// Broadcast icon: microphone routed to the stream mix in Sonar streamer mode.
    /// </summary>
    public required bool IsMicrophoneStreamBroadcastEnabled { get; init; }

    public string? MonitoringOutputDeviceId { get; init; }
}