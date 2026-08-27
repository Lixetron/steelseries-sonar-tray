using System.Text.Json;

namespace SonarQuickMixer.Headset;

internal sealed class EngineHeadsetSummary
{
    public required string DisplayName { get; init; }
    public required string DeviceName { get; init; }
    public bool IsConnected { get; init; }
    public ushort? UsbProductId { get; init; }
    public bool LooksWireless { get; init; }
}

internal sealed class SteelSeriesEngineDeviceApi
{
    private readonly HeadsetHttpTransport _transport;
    private readonly SteelSeriesEngineDiscovery _discovery;
    private string? _baseUrl;

    public SteelSeriesEngineDeviceApi(HeadsetHttpTransport transport, SteelSeriesEngineDiscovery discovery)
    {
        _transport = transport;
        _discovery = discovery;
    }

    public async Task<EngineHeadsetSummary?> GetPrimaryHeadsetAsync(CancellationToken cancellationToken)
    {
        _baseUrl ??= await _discovery.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            return null;
        }

        using var devicesDocument = await _transport
            .GetJsonDocumentAsync($"{_baseUrl}/devices", cancellationToken)
            .ConfigureAwait(false);
        if (devicesDocument is null)
        {
            _baseUrl = null;
            return null;
        }

        var headset = TryPickHeadset(devicesDocument.RootElement);
        if (headset is null)
        {
            return null;
        }

        var wirelessConnected = await TryGetWirelessConnectedAsync(headset.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        return new EngineHeadsetSummary
        {
            DisplayName = headset.DisplayName,
            DeviceName = headset.DeviceName,
            IsConnected = wirelessConnected ?? headset.Connected,
            UsbProductId = headset.UsbProductId,
            LooksWireless = headset.LooksWireless
        };
    }

    private async Task<bool?> TryGetWirelessConnectedAsync(int deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            return null;
        }

        using var document = await _transport
            .GetJsonDocumentAsync($"{_baseUrl}/v1/wirelessDeviceConnectionStatus", cancellationToken)
            .ConfigureAwait(false);
        if (document is null ||
            !document.RootElement.TryGetProperty("devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var device in devices.EnumerateArray())
        {
            if (!device.TryGetProperty("deviceId", out var idElement) || idElement.GetInt32() != deviceId)
            {
                continue;
            }

            if (device.TryGetProperty("isConnected", out var connected))
            {
                return connected.GetBoolean();
            }
        }

        return null;
    }

    private static ParsedEngineDevice? TryPickHeadset(JsonElement root)
    {
        if (!root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        ParsedEngineDevice? best = null;
        foreach (var device in devices.EnumerateArray())
        {
            if (!IsHeadsetCandidate(device))
            {
                continue;
            }

            var parsed = ParseDevice(device);
            if (parsed is null)
            {
                continue;
            }

            if (best is null || Score(parsed) > Score(best))
            {
                best = parsed;
            }
        }

        return best;
    }

    private static bool IsHeadsetCandidate(JsonElement device)
    {
        if (device.TryGetProperty("hide_device_card", out var hide) && hide.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        if (device.TryGetProperty("deviceTypeName", out var typeName) &&
            string.Equals(typeName.GetString(), "Headset", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (device.TryGetProperty("type", out var type) && type.TryGetInt32(out var typeId) && typeId == 3)
        {
            return true;
        }

        var name = device.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        return !string.IsNullOrWhiteSpace(name) &&
               name.Contains("arctis", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedEngineDevice? ParseDevice(JsonElement device)
    {
        if (!device.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
        {
            return null;
        }

        var displayName =
            (device.TryGetProperty("display_name", out var display) ? display.GetString() : null)
            ?? (device.TryGetProperty("full_name", out var full) ? full.GetString() : null)
            ?? (device.TryGetProperty("name", out var name) ? name.GetString() : null);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var deviceName = device.TryGetProperty("name", out var rawName) ? rawName.GetString() ?? displayName : displayName;
        var connected = device.TryGetProperty("connected", out var connectedElement) &&
                        connectedElement.TryGetInt32(out var connectedFlag) &&
                        connectedFlag == 1;

        ushort? usbProductId = null;
        if (device.TryGetProperty("physical_devices", out var physical) &&
            physical.ValueKind == JsonValueKind.Array)
        {
            foreach (var phys in physical.EnumerateArray())
            {
                if (!phys.TryGetProperty("hexProductId", out var hex) ||
                    hex.GetString() is not { } hexValue)
                {
                    continue;
                }

                usbProductId = TryParseUsbProductId(hexValue);
                if (usbProductId is not null)
                {
                    break;
                }
            }
        }

        var looksWireless =
            device.TryGetProperty("wireless_device_information", out var wireless) &&
            wireless.ValueKind == JsonValueKind.Object &&
            ((wireless.TryGetProperty("transmitter_product_id", out var tx) &&
              tx.TryGetInt64(out var txId) && txId != 0) ||
             (wireless.TryGetProperty("receiver_product_id", out var rx) &&
              rx.TryGetInt64(out var rxId) && rxId != 0) ||
             deviceName.Contains("_tx", StringComparison.OrdinalIgnoreCase) ||
             deviceName.Contains("wireless", StringComparison.OrdinalIgnoreCase));

        return new ParsedEngineDevice(id, displayName.Trim(), deviceName, connected, usbProductId, looksWireless);
    }

    private static ushort? TryParseUsbProductId(string hexProductId)
    {
        // Engine reports combined VID+PID, e.g. "0x10382264".
        var hex = hexProductId.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (hex.Length >= 8 &&
            ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var combined))
        {
            return (ushort)(combined & 0xFFFF);
        }

        if (hex.Length <= 4 &&
            ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var pid))
        {
            return pid;
        }

        return null;
    }

    private static int Score(ParsedEngineDevice device)
    {
        var score = 0;
        if (device.Connected)
        {
            score += 100;
        }

        if (device.LooksWireless)
        {
            score += 10;
        }

        if (device.DeviceName.Contains("_tx", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        return score;
    }

    private sealed record ParsedEngineDevice(
        int DeviceId,
        string DisplayName,
        string DeviceName,
        bool Connected,
        ushort? UsbProductId,
        bool LooksWireless);
}
