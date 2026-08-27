using System.IO;
using System.Text.Json;

namespace SonarQuickMixer.Headset;

internal sealed class SteelSeriesEngineDiscovery
{
    private static readonly string CorePropsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteelSeries", "SteelSeries Engine 3", "coreProps.json");

    private static readonly string SubAppsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteelSeries", "SteelSeries GG", "subApps.json");

    private readonly HeadsetHttpTransport _transport;

    public SteelSeriesEngineDiscovery(HeadsetHttpTransport transport) => _transport = transport;

    public async Task<string?> ResolveAsync(CancellationToken cancellationToken)
    {
        var fromCoreProps = TryLoadEngineBaseUrlFromCoreProps();
        if (!string.IsNullOrWhiteSpace(fromCoreProps))
        {
            return fromCoreProps;
        }

        var ggBaseUrl = TryLoadGgBaseUrlFromCoreProps();
        if (ggBaseUrl is not null)
        {
            var fromGg = await TryGetFromGgAsync(ggBaseUrl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromGg))
            {
                return fromGg;
            }
        }

        return TryGetFromLocalSubAppsFile();
    }

    private async Task<string?> TryGetFromGgAsync(string ggBaseUrl, CancellationToken cancellationToken)
    {
        using var document = await _transport.GetJsonDocumentAsync($"{ggBaseUrl}/subApps", cancellationToken)
            .ConfigureAwait(false);
        return document is null ? null : TryReadEngineAddress(document.RootElement);
    }

    private static string? TryLoadEngineBaseUrlFromCoreProps()
    {
        if (!File.Exists(CorePropsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(CorePropsPath));
            if (document.RootElement.TryGetProperty("encryptedAddress", out var addressElement))
            {
                return FormatHttpsBaseUrl(addressElement.GetString());
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryLoadGgBaseUrlFromCoreProps()
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
                return FormatHttpsBaseUrl(addressElement.GetString());
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryGetFromLocalSubAppsFile()
    {
        if (!File.Exists(SubAppsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SubAppsPath));
            return TryReadEngineAddress(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadEngineAddress(JsonElement root)
    {
        if (!root.TryGetProperty("subApps", out var subApps) ||
            !subApps.TryGetProperty("engine", out var engine))
        {
            return null;
        }

        if (engine.TryGetProperty("isEnabled", out var isEnabled) && !isEnabled.GetBoolean())
        {
            return null;
        }

        if (engine.TryGetProperty("isReady", out var isReady) && !isReady.GetBoolean())
        {
            return null;
        }

        if (engine.TryGetProperty("isRunning", out var isRunning) && !isRunning.GetBoolean())
        {
            return null;
        }

        if (!engine.TryGetProperty("metadata", out var metadata))
        {
            return null;
        }

        if (metadata.TryGetProperty("encryptedWebServerAddress", out var encrypted) &&
            FormatHttpsBaseUrl(encrypted.GetString()) is { } httpsUrl)
        {
            return httpsUrl;
        }

        if (metadata.TryGetProperty("webServerAddress", out var plain))
        {
            var address = plain.GetString();
            if (!string.IsNullOrWhiteSpace(address) && address != "null")
            {
                return address.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? address.TrimEnd('/')
                    : FormatHttpsBaseUrl(address);
            }
        }

        return null;
    }

    private static string? FormatHttpsBaseUrl(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address == "null")
        {
            return null;
        }

        address = address.Trim();
        return address.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? address.TrimEnd('/')
            : $"https://{address}";
    }
}
