namespace SteelSeries.SonarTray;

public enum SonarMixerPath
{
    Monitoring,
    Streaming
}

public sealed class SonarChannelState
{
    public float? Volume { get; init; }
    public bool? Muted { get; init; }

    /// <summary>
    /// Streamer mode: whether the channel is routed to personal (monitoring) or audience (streaming) mix.
    /// </summary>
    public bool? MixIncluded { get; init; }
}

public sealed class SonarChannelSettings
{
    public SonarChannelState? Monitoring { get; init; }
    public SonarChannelState? Streaming { get; init; }
}
