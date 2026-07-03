namespace SonarQuickMixer.Sonar;

internal sealed class SonarModeDetector
{
    private readonly SonarHttpTransport _transport;

    public SonarModeDetector(SonarHttpTransport transport) => _transport = transport;

    public async Task<bool> DetectStreamerModeAsync(string? webServerAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webServerAddress))
        {
            return false;
        }

        var (success, body) = await _transport
            .GetStringOrFailAsync($"{webServerAddress}/mode", cancellationToken)
            .ConfigureAwait(false);

        if (!success)
        {
            return true;
        }

        var mode = body!.Trim('"');
        return string.Equals(mode, "stream", StringComparison.OrdinalIgnoreCase);
    }
}
