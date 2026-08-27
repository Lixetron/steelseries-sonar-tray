using SonarQuickMixer.Controls;
using SonarQuickMixer.Headset;
using SonarQuickMixer.Settings;

namespace SonarQuickMixer.Tests.Headset;

public sealed class HeadsetDeviceInfoFormatterTests
{
    [Fact]
    public void FormatSecondaryLine_ExcludesBattery()
    {
        var info = new HeadsetDeviceInfo
        {
            DisplayName = "Arctis Nova 5X White",
            BatteryPercent = 60,
            IsCharging = false,
            IsHeadsetPowered = true,
            ConnectionMethod = "2.4 GHz"
        };

        var text = HeadsetDeviceInfoFormatter.FormatSecondaryLine(info, new AppSettings
        {
            ShowDeviceName = true,
            ShowDeviceBattery = true,
            ShowDeviceConnection = true
        });

        Assert.Equal("Arctis Nova 5X White · 2.4 GHz", text);
    }

    [Fact]
    public void FormatBatteryPercentText_ShowsPercentOrOff()
    {
        var powered = new HeadsetDeviceInfo
        {
            BatteryPercent = 42,
            IsCharging = true,
            IsHeadsetPowered = true
        };

        Assert.Equal("42%", HeadsetDeviceInfoFormatter.FormatBatteryPercentText(powered, new AppSettings
        {
            ShowDeviceBattery = true
        }));

        var off = new HeadsetDeviceInfo { IsHeadsetPowered = false, BatteryPercent = 10 };
        Assert.Equal("Off", HeadsetDeviceInfoFormatter.FormatBatteryPercentText(off, new AppSettings
        {
            ShowDeviceBattery = true
        }));

        Assert.Null(HeadsetDeviceInfoFormatter.FormatBatteryPercentText(powered, new AppSettings
        {
            ShowDeviceBattery = false
        }));
    }
}

public sealed class BatteryIconKindResolverTests
{
    [Theory]
    [InlineData(100, false, true, BatteryIconKind.Full)]
    [InlineData(75, false, true, BatteryIconKind.Full)]
    [InlineData(60, false, true, BatteryIconKind.Half)]
    [InlineData(40, false, true, BatteryIconKind.Half)]
    [InlineData(25, false, true, BatteryIconKind.Low)]
    [InlineData(15, false, true, BatteryIconKind.Low)]
    [InlineData(10, false, true, BatteryIconKind.Empty)]
    [InlineData(0, false, true, BatteryIconKind.Empty)]
    [InlineData(90, true, true, BatteryIconKind.Charging)]
    [InlineData(5, true, true, BatteryIconKind.Charging)]
    [InlineData(50, false, false, BatteryIconKind.Empty)]
    public void Resolve_MapsLevels(int percent, bool charging, bool powered, BatteryIconKind expected)
    {
        var kind = BatteryIconKindResolver.Resolve(percent, charging, powered);
        Assert.Equal(expected, kind);
    }
}
