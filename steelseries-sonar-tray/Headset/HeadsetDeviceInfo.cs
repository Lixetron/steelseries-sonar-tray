namespace SonarQuickMixer.Headset;

public sealed class HeadsetDeviceInfo
{
    public string? DisplayName { get; init; }
    public int? BatteryPercent { get; init; }
    public bool? IsCharging { get; init; }
    public bool? IsHeadsetPowered { get; init; }
    public string? ConnectionMethod { get; init; }

    public bool HasAnyData =>
        !string.IsNullOrWhiteSpace(DisplayName)
        || BatteryPercent is not null
        || !string.IsNullOrWhiteSpace(ConnectionMethod);
}
