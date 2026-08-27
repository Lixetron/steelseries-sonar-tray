namespace SonarQuickMixer.Controls;

public enum BatteryIconKind
{
    Hidden,
    Empty,
    Low,
    Half,
    Full,
    Charging
}

public static class BatteryIconKindResolver
{
    public static BatteryIconKind Resolve(int? percent, bool? isCharging, bool? isPowered)
    {
        if (isPowered == false)
        {
            return BatteryIconKind.Empty;
        }

        if (isCharging == true)
        {
            return BatteryIconKind.Charging;
        }

        if (percent is null)
        {
            return BatteryIconKind.Hidden;
        }

        return percent.Value switch
        {
            >= 75 => BatteryIconKind.Full,
            >= 40 => BatteryIconKind.Half,
            >= 15 => BatteryIconKind.Low,
            _ => BatteryIconKind.Empty
        };
    }
}
