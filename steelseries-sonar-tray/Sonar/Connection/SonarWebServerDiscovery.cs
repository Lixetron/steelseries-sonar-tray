using System.IO;
using System.Text.Json;

namespace SonarQuickMixer.Sonar;

internal sealed class SonarWebServerDiscovery
{
    private static readonly string CorePropsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteelSeries", "SteelSeries Engine 3", "coreProps.json");

    private static readonly string SubAppsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteelSeries", "SteelSeries GG", "subApps.json");

    private readonly SonarHttpTransport _transport;

    public SonarWebServerDiscovery(SonarHttpTransport transport) => _transport = transport;

    public async Task<string?> ResolveAsync(CancellationToken cancellationToken)
    {
        var ggBaseUrl = TryLoadGgBaseUrl();
        if (ggBaseUrl is not null)
        {
            var fromGg = await TryGetFromGgAsync(ggBaseUrl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromGg))
            {
                return fromGg;
            }
        }

        return TryGetFromLocalFile();
    }

    private async Task<string?> TryGetFromGgAsync(string ggBaseUrl, CancellationToken cancellationToken)
    {
        using var document = await _transport.GetJsonDocumentAsync($"{ggBaseUrl}/subApps", cancellationToken)
            .ConfigureAwait(false);
        return document is null ? null : TryReadWebServerAddress(document.RootElement);
    }

    private static string? TryLoadGgBaseUrl()
    {
        if (!File.Exists(CorePropsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(CorePropsPath));
            if (document.RootElement.TryGetProperty("ggEncryptedAddress", out var addressElement))
            {
                var address = addressElement.GetString();
                if (!string.IsNullOrWhiteSpace(address))
                {
                    return $"https://{address.Trim()}";
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryGetFromLocalFile()
    {
        if (!File.Exists(SubAppsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SubAppsPath));
            return TryReadWebServerAddress(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadWebServerAddress(JsonElement root)
    {
        if (!root.TryGetProperty("subApps", out var subApps) ||
            !subApps.TryGetProperty("sonar", out var sonar))
        {
            return null;
        }

        if (sonar.TryGetProperty("isEnabled", out var isEnabled) && !isEnabled.GetBoolean())
        {
            return null;
        }

        if (sonar.TryGetProperty("isReady", out var isReady) && !isReady.GetBoolean())
        {
            return null;
        }

        if (sonar.TryGetProperty("isRunning", out var isRunning) && !isRunning.GetBoolean())
        {
            return null;
        }

        if (!sonar.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("webServerAddress", out var webServerAddress))
        {
            return null;
        }

        var address = webServerAddress.GetString();
        return string.IsNullOrWhiteSpace(address) || address == "null" ? null : address;
    }
}
