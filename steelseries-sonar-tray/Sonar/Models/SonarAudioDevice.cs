namespace SonarQuickMixer.Sonar;

public enum SonarAudioDataFlow
{
    Render,
    Capture
}

public sealed class SonarAudioDevice
{
    public required string Id { get; init; }
    public required string FriendlyName { get; init; }
    public required SonarAudioDataFlow DataFlow { get; init; }
    public bool IsVad { get; init; }
    public string? State { get; init; }

    public bool IsActivePhysicalDevice =>
        !IsVad
        && (string.IsNullOrWhiteSpace(State)
            || string.Equals(State, "active", StringComparison.OrdinalIgnoreCase));
}

public sealed class SonarDeviceSelection
{
    public required bool IsStreamerMode { get; init; }
    public string? OutputDeviceId { get; init; }
    public string? MicrophoneDeviceId { get; init; }
}
