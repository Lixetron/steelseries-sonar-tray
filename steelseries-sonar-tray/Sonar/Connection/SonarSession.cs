namespace SonarQuickMixer.Sonar;

internal sealed class SonarSession
{
    public string? WebServerAddress { get; set; }

    public bool? StreamerMode { get; set; }

    public bool IsConnected => !string.IsNullOrWhiteSpace(WebServerAddress);

    public bool IsStreamerMode => StreamerMode == true;

    public int? Port => TryParsePort(WebServerAddress);

    public void Invalidate()
    {
        WebServerAddress = null;
        StreamerMode = null;
    }

    private static int? TryParsePort(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.IsDefaultPort ? null : uri.Port;
    }
}
