namespace SteelSeries.SonarTray;

public sealed class SonarMixerSnapshot
{
    public required bool IsStreamerMode { get; init; }

    public required IReadOnlySet<string> EnabledChannels { get; init; }

    public required IReadOnlyDictionary<string, SonarChannelSettings> Channels { get; init; }

    public bool IsChannelEnabled(string channel) => EnabledChannels.Contains(channel);
}

public static class SonarChannels
{
    public static readonly string[] All = ["master", "game", "chatRender", "media", "aux"];

    public static readonly string[] MasterProportional = ["game", "chatRender", "media", "aux"];

    public static readonly string[] Optional = ["media", "aux"];

    public static readonly string[] MixRoutable = ["game", "chatRender", "media", "aux"];
}
