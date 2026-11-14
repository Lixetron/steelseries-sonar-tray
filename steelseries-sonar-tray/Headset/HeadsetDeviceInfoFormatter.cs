using SonarQuickMixer.Localization;
using SonarQuickMixer.Settings;

namespace SonarQuickMixer.Headset;

public static class HeadsetDeviceInfoFormatter
{
    /// <summary>
    /// Secondary header line: device name and/or connection (battery is shown separately).
    /// </summary>
    public static string? FormatSecondaryLine(HeadsetDeviceInfo? info, AppSettings settings)
    {
        if (info is null)
        {
            return null;
        }

        var parts = new List<string>(2);

        if (settings.ShowDeviceName && !string.IsNullOrWhiteSpace(info.DisplayName))
        {
            parts.Add(info.DisplayName.Trim());
        }

        if (settings.ShowDeviceConnection && !string.IsNullOrWhiteSpace(info.ConnectionMethod))
        {
            if (!(info.IsHeadsetPowered == false &&
                  string.Equals(info.ConnectionMethod, "Disconnected", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(LocalizeConnection(info.ConnectionMethod));
            }
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string LocalizeConnection(string connection) => connection.ToLowerInvariant() switch
    {
        "bluetooth" => Loc.Get("headset.connection.bluetooth"),
        "2.4 ghz" => Loc.Get("headset.connection.2_4ghz"),
        "usb" => Loc.Get("headset.connection.usb"),
        "disconnected" => Loc.Get("headset.connection.disconnected"),
        _ => connection
    };

    public static string? FormatBatteryPercentText(HeadsetDeviceInfo? info, AppSettings settings)
    {
        if (info is null || !settings.ShowDeviceBattery)
        {
            return null;
        }

        if (info.IsHeadsetPowered == false)
        {
            return Loc.Get("headset.battery.off");
        }

        if (info.BatteryPercent is int percent)
        {
            return $"{percent}%";
        }

        return null;
    }

    [Obsolete("Use FormatSecondaryLine / FormatBatteryPercentText.")]
    public static string? Format(HeadsetDeviceInfo? info, AppSettings settings)
    {
        var secondary = FormatSecondaryLine(info, settings);
        var battery = FormatBatteryPercentText(info, settings);
        if (secondary is null && battery is null)
        {
            return null;
        }

        if (secondary is null)
        {
            return battery;
        }

        if (battery is null)
        {
            return secondary;
        }

        return $"{secondary} · {battery}";
    }
}
