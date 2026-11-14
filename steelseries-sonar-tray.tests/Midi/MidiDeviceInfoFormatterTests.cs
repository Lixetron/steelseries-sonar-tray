using SonarQuickMixer.Midi;
using SonarQuickMixer.Settings;

namespace SonarQuickMixer.Tests.Midi;

public sealed class MidiDeviceInfoFormatterTests
{
    [Fact]
    public void Format_ListsEnabledDevices()
    {
        var text = MidiDeviceInfoFormatter.Format(
            ["M-VAVE SMC-Mixer"],
            new AppSettings { MidiEnabled = true, ShowMidiDeviceName = true });

        Assert.Equal("MIDI: M-VAVE SMC-Mixer", text);
    }

    [Fact]
    public void Format_TrimsAndDeduplicatesNames()
    {
        var text = MidiDeviceInfoFormatter.Format(
            ["  M-VAVE SMC-Mixer  ", "m-vave smc-mixer", "nanoKONTROL2"],
            new AppSettings { MidiEnabled = true, ShowMidiDeviceName = true });

        Assert.Equal("MIDI: M-VAVE SMC-Mixer, nanoKONTROL2", text);
    }

    [Fact]
    public void Format_ReturnsNull_WhenSettingIsOff() =>
        Assert.Null(MidiDeviceInfoFormatter.Format(
            ["M-VAVE SMC-Mixer"],
            new AppSettings { MidiEnabled = true, ShowMidiDeviceName = false }));

    [Fact]
    public void Format_ReturnsNull_WhenMidiControlIsOff() =>
        Assert.Null(MidiDeviceInfoFormatter.Format(
            ["M-VAVE SMC-Mixer"],
            new AppSettings { MidiEnabled = false, ShowMidiDeviceName = true }));

    [Fact]
    public void Format_ReturnsNull_WhenNoDevicesAreEnabled()
    {
        Assert.Null(MidiDeviceInfoFormatter.Format(
            [],
            new AppSettings { MidiEnabled = true, ShowMidiDeviceName = true }));

        Assert.Null(MidiDeviceInfoFormatter.Format(
            ["   "],
            new AppSettings { MidiEnabled = true, ShowMidiDeviceName = true }));
    }
}
