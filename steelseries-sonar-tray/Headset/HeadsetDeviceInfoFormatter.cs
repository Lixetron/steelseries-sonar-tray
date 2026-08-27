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
                parts.Add(info.ConnectionMethod);
            }
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public static string? FormatBatteryPercentText(HeadsetDeviceInfo? info, AppSettings settings)
    {
        if (info is null || !settings.ShowDeviceBattery)
        {
            return null;
        }

        if (info.IsHeadsetPowered == false)
        {
            return "Off";
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
