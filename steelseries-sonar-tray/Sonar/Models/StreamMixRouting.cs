namespace SonarQuickMixer.Sonar;

internal sealed class StreamMixRouting
{
    public Dictionary<string, bool> Monitoring { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, bool> Streaming { get; } = new(StringComparer.OrdinalIgnoreCase);
}
