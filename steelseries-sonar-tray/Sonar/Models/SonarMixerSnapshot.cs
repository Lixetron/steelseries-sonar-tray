namespace SonarQuickMixer.Sonar;

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

    public static bool IsValidChannel(string? channel) =>
        !string.IsNullOrWhiteSpace(channel)
        && All.Contains(channel, StringComparer.OrdinalIgnoreCase);

    public static string NormalizeChannel(string? channel) =>
        All.FirstOrDefault(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase)) ?? "master";

    public static string GetDisplayName(string channel) => channel.ToLowerInvariant() switch
    {
        "master" => "Master",
        "game" => "Game",
        "chatrender" => "Chat",
        "media" => "Media",
        "aux" => "Aux",
        _ => channel
    };

    /// <summary>
    /// Official SteelSeries GG Sonar channel accent (extracted from GG theme tokens).
    /// Game=success teal, Chat=info blue, Media=fuchsia, Aux=violet, Master=primary text grey.
    /// </summary>
    public static string GetAccentHex(string? channel) => NormalizeChannel(channel) switch
    {
        "game" => "#02DDBC",       // forgeColors.text.success / SUPPORT_GREEN_100
        "chatRender" => "#2DB1FC", // forgeColors.text.info / SUPPORT_BLUE_100
        "media" => "#FF53B0",      // PRIMARY_FUCHSIA_100
        "aux" => "#8F51F4",        // PRIMARY_VIOLET_100
        _ => "#D0D9F6"             // GREY_PRIMARY_100 (master / fallback)
    };
}
