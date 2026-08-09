using SonarQuickMixer.Midi;

namespace SonarQuickMixer.Tests.Midi;

public class MidiLayoutJsonTests
{
    [Fact]
    public void TryParse_ValidMinimalLayout_Succeeds()
    {
        const string json = """
            {
              "name": "Pad",
              "deviceMatch": ["Pad"],
              "controls": [
                { "id": "f1", "row": 0, "col": 0, "type": "fader", "label": "F1", "controller": 7 }
              ]
            }
            """;

        Assert.True(MidiLayoutJson.TryParse(json, out var layout, out var error), error);
        Assert.NotNull(layout);
        Assert.Equal("Pad", layout!.Name);
        Assert.Single(layout.Controls);
        Assert.Equal(7, layout.Controls[0].Controller);
    }

    [Fact]
    public void TryParse_BrokenSyntax_ReportsLineAndPosition()
    {
        const string json = """
            {
              "name": "Broken",
              "controls": [
                { "id": "f1", "type": "fader",
              ]
            }
            """;

        Assert.False(MidiLayoutJson.TryParse(json, out _, out var error));
        Assert.Contains("line", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Invalid JSON syntax", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DuplicateControlId_FailsSemantics()
    {
        const string json = """
            {
              "name": "Dup",
              "controls": [
                { "id": "f1", "row": 0, "col": 0, "type": "fader" },
                { "id": "f1", "row": 0, "col": 1, "type": "encoder" }
              ]
            }
            """;

        Assert.False(MidiLayoutJson.TryParse(json, out _, out var error));
        Assert.Contains("Duplicate control id", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_MissingRegionReference_FailsSemantics()
    {
        const string json = """
            {
              "name": "BadRef",
              "controls": [
                { "id": "f1", "regionId": "missing", "row": 0, "col": 0, "type": "fader" }
              ]
            }
            """;

        Assert.False(MidiLayoutJson.TryParse(json, out _, out var error));
        Assert.Contains("regionId", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_RoundTripsFactoryFlags()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "Round",
            DeviceMatch = ["Round"],
            Controls =
            [
                new MidiLayoutControl
                {
                    Id = "f1",
                    Type = MidiControlType.Fader,
                    Controller = 0,
                    IsPitchBend = true,
                    DefaultMode = MidiValueMode.Absolute
                }
            ]
        };

        var json = MidiLayoutJson.Serialize(layout);
        Assert.True(MidiLayoutJson.TryParse(json, out var parsed, out var error), error);
        Assert.True(parsed!.Controls[0].IsPitchBend);
        Assert.Equal(0, parsed.Controls[0].Controller);
    }

    [Fact]
    public void Serialize_RoundTripsMuteFeedback_DefaultsAndExplicit()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "Feedback",
            DeviceMatch = ["Pad"],
            Controls =
            [
                new MidiLayoutControl
                {
                    Id = "m1",
                    Type = MidiControlType.Button,
                    Controller = 16,
                    IsNote = true,
                    DefaultAction = MidiBindingAction.MuteToggle,
                    Feedback = new MidiControlFeedbackSpec { Source = MidiFeedbackSource.Mute }
                },
                new MidiLayoutControl
                {
                    Id = "m2",
                    Type = MidiControlType.Button,
                    Controller = 40,
                    IsNote = false,
                    DefaultAction = MidiBindingAction.MuteToggle,
                    Feedback = new MidiControlFeedbackSpec
                    {
                        Source = MidiFeedbackSource.Mute,
                        On = new MidiFeedbackMessage
                        {
                            Kind = MidiFeedbackKind.Note,
                            Controller = 16,
                            Value = 127,
                            Channel = 1
                        },
                        Off = new MidiFeedbackMessage
                        {
                            Kind = MidiFeedbackKind.Note,
                            Controller = 16,
                            Value = 0,
                            Channel = 1
                        }
                    }
                }
            ]
        };

        var json = MidiLayoutJson.Serialize(layout);
        Assert.True(MidiLayoutJson.TryParse(json, out var parsed, out var error), error);
        Assert.Equal(MidiFeedbackSource.Mute, parsed!.Controls[0].Feedback!.Source);
        Assert.Null(parsed.Controls[0].Feedback!.On);
        Assert.True(MidiFeedbackResolver.TryResolveMuteMessages(parsed.Controls[0], out var on, out var off));
        Assert.Equal(MidiFeedbackKind.Note, on.Kind);
        Assert.Equal(16, on.Controller);
        Assert.Equal(127, on.Value);
        Assert.Equal(0, off.Value);

        Assert.Equal(16, parsed.Controls[1].Feedback!.On!.Controller);
        Assert.Equal(MidiFeedbackKind.Note, parsed.Controls[1].Feedback!.On!.Kind);
    }

    [Fact]
    public void OfficialSmcPreset_BakesHardwareOnly_NoFeedback()
    {
        var official = Path.Combine(AppContext.BaseDirectory, "Presets");
        var path = Path.Combine(official, "m-vave-smc-mixer.json");
        if (!File.Exists(path))
        {
            path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "steelseries-sonar-tray", "Presets", "m-vave-smc-mixer.json"));
        }

        Assert.True(File.Exists(path), path);
        var json = File.ReadAllText(path);
        Assert.True(MidiLayoutJson.TryParse(json, out var layout, out var error), error);
        for (var i = 1; i <= 8; i++)
        {
            var mute = Assert.Single(layout!.Controls, c => c.Id == $"m{i}");
            Assert.True(mute.IsNote);
            Assert.Equal(15 + i, mute.Controller);
            Assert.Null(mute.DefaultAction);
            Assert.Null(mute.Feedback);

            var fader = Assert.Single(layout.Controls, c => c.Id == $"f{i}");
            Assert.True(fader.IsPitchBend);
            Assert.Equal(i - 1, fader.Controller);
            Assert.Null(fader.DefaultAction);
            Assert.Null(fader.Feedback);
        }
    }

    [Fact]
    public void PitchBendFeedback_DefaultsToMatchLampNotSelectNotes()
    {
        var muteFader = new MidiLayoutControl
        {
            Id = "f1",
            Type = MidiControlType.Fader,
            Controller = 0,
            IsPitchBend = true,
            Feedback = new MidiControlFeedbackSpec { Source = MidiFeedbackSource.Mute }
        };
        Assert.True(MidiFeedbackResolver.TryResolveMessages(muteFader, out var muteOn, out var muteOff));
        Assert.Equal(MidiFeedbackKind.PitchBend, muteOn.Kind);
        Assert.Equal(1, muteOn.Channel);

        var selectFader = new MidiLayoutControl
        {
            Id = "f2",
            Type = MidiControlType.Fader,
            Controller = 1,
            IsPitchBend = true,
            Feedback = new MidiControlFeedbackSpec
            {
                Source = MidiFeedbackSource.ChannelAssigned,
                Style = MidiFeedbackStyle.Solid
            }
        };
        Assert.True(MidiFeedbackResolver.TryResolveMessages(selectFader, out var selectOn, out _));
        Assert.Equal(MidiFeedbackKind.PitchBend, selectOn.Kind);
        Assert.Equal(2, selectOn.Channel);

        // Materialize off = hardware echo (extinguish); on template is unused for soft-takeover path.
        var dark = MidiFeedbackResolver.Materialize(muteOff, muteFader, hardwareNormalized: 0.25f);
        Assert.Equal(32, dark.Value);
    }

    [Fact]
    public void Serialize_RoundTripsBlinkAndChannelAssigned()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "Blink",
            DeviceMatch = ["Pad"],
            Controls =
            [
                new MidiLayoutControl
                {
                    Id = "m1",
                    Type = MidiControlType.Button,
                    Controller = 16,
                    IsNote = true,
                    Feedback = new MidiControlFeedbackSpec
                    {
                        Source = MidiFeedbackSource.Mute,
                        Style = MidiFeedbackStyle.Blink
                    }
                },
                new MidiLayoutControl
                {
                    Id = "f1",
                    Type = MidiControlType.Fader,
                    Controller = 0,
                    IsPitchBend = true,
                    Feedback = new MidiControlFeedbackSpec
                    {
                        Source = MidiFeedbackSource.ChannelAssigned
                    }
                }
            ]
        };

        var json = MidiLayoutJson.Serialize(layout);
        Assert.True(MidiLayoutJson.TryParse(json, out var parsed, out var error), error);
        Assert.Equal(MidiFeedbackStyle.Blink, parsed!.Controls[0].Feedback!.Style);
        Assert.Equal(MidiFeedbackSource.ChannelAssigned, parsed.Controls[1].Feedback!.Source);
        Assert.Equal(MidiFeedbackUi.TagMuteBlink, MidiFeedbackUi.ToTag(MidiFeedbackSource.Mute, MidiFeedbackStyle.Blink));
        Assert.Equal(
            "ChannelAssignedBlink",
            MidiFeedbackUi.ToTag(MidiFeedbackSource.ChannelAssigned, MidiFeedbackStyle.Blink));
        Assert.True(MidiFeedbackUi.TryParseTag("ChannelSelect", out var src, out _));
        Assert.Equal(MidiFeedbackSource.ChannelAssigned, src);
        Assert.False(MidiFeedbackUi.AllowsMuteSource(parsed.Controls[1]));
        Assert.True(MidiFeedbackUi.AllowsMuteSource(parsed.Controls[0]));
    }
}
