using SonarQuickMixer.Midi;
using SonarQuickMixer.Services;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Tests.Midi;

public class MidiValueParserTests
{
    [Theory]
    [InlineData(0, 0f)]
    [InlineData(64, 64f / 127f)]
    [InlineData(127, 1f)]
    [InlineData(-5, 0f)]
    [InlineData(200, 1f)]
    public void AbsoluteToVolume_ClampsAndScales(int raw, float expected)
    {
        Assert.Equal(expected, MidiValueParser.AbsoluteToVolume(raw), precision: 5);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 127)]
    [InlineData(0.5f, 64)]
    public void VolumeToRaw_CcMapsNormalized(float volume, int expectedRaw)
    {
        Assert.Equal(expectedRaw, MidiValueParser.VolumeToRaw(isPitchBend: false, volume));
    }

    [Fact]
    public void VolumeToRaw_PitchBendRoundTripEndpoints()
    {
        Assert.Equal(0, MidiValueParser.VolumeToRaw(isPitchBend: true, 0f));
        Assert.Equal(127 << 7, MidiValueParser.VolumeToRaw(isPitchBend: true, 1f));
        Assert.Equal(1f, MidiValueParser.PitchBendToVolume(MidiValueParser.VolumeToRaw(true, 1f)), precision: 5);
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(127 << 7, 1f)] // MSB=127, LSB=0 → 00 7F on wire
    [InlineData(64 << 7, 64f / 127f)]
    [InlineData(-1, 0f)]
    [InlineData(200 << 7, 1f)]
    public void PitchBendToVolume_MapsMsbRange(int pitch14, float expected)
    {
        Assert.Equal(expected, MidiValueParser.PitchBendToVolume(pitch14), precision: 5);
    }

    [Fact]
    public void FormatRawDisplay_PitchBendUsesLsbMsbHex()
    {
        Assert.Equal("00 7F", MidiValueParser.FormatRawDisplay(127 << 7, isPitchBend: true));
        Assert.Equal("00 00", MidiValueParser.FormatRawDisplay(0, isPitchBend: true));
        Assert.Equal("64", MidiValueParser.FormatRawDisplay(64, isPitchBend: false));
    }

    [Fact]
    public void BindingKey_DistinguishesPitchBendFromCc()
    {
        var cc = new MidiBinding { DeviceName = "SMC-Mixer", Controller = 0, IsNote = false };
        var pb = new MidiBinding { DeviceName = "SMC-Mixer", Controller = 0, IsPitchBend = true };
        Assert.Equal("SMC-Mixer|C|0", cc.BindingKey);
        Assert.Equal("SMC-Mixer|P|0", pb.BindingKey);
        Assert.Equal("PB E0", MidiBinding.FormatHardwareLabel(false, 0, isPitchBend: true));
        Assert.Equal("PB E7", MidiBinding.FormatHardwareLabel(false, 7, isPitchBend: true));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(64, 0)]
    [InlineData(65, -1)]
    [InlineData(127, -63)]
    [InlineData(0, 0)]
    public void ParseRelativeTicks_OffsetBinary(int raw, int expectedTicks)
    {
        Assert.Equal(expectedTicks, MidiValueParser.ParseRelativeTicks(raw, MidiRelativeEncoding.OffsetBinary));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    [InlineData(127, -1)]
    [InlineData(65, -63)]
    public void ParseRelativeTicks_TwosComplement(int raw, int expectedTicks)
    {
        Assert.Equal(expectedTicks, MidiValueParser.ParseRelativeTicks(raw, MidiRelativeEncoding.TwosComplement));
    }

    [Fact]
    public void ApplyRelativeDelta_StepsFromCurrent()
    {
        var result = MidiValueParser.ApplyRelativeDelta(0.50f, ticks: 2, step: 0.02f);
        Assert.Equal(0.54f, result, precision: 5);
    }

    [Fact]
    public void ApplyRelativeDelta_ClampsToRange()
    {
        Assert.Equal(0f, MidiValueParser.ApplyRelativeDelta(0.01f, -2, 0.02f), precision: 5);
        Assert.Equal(1f, MidiValueParser.ApplyRelativeDelta(0.99f, 2, 0.02f), precision: 5);
    }

    [Fact]
    public void RelativeEncoderNeedle_SpinsFromTicksNotRawAbsolute()
    {
        var vm = new BlueprintControlVm
        {
            Id = "e1",
            Label = "ENC1",
            Type = MidiControlType.Encoder,
            Row = 0,
            Col = 0,
            Mode = MidiValueMode.Relative,
            RelativeEncoding = MidiRelativeEncoding.OffsetBinary
        };

        Assert.Equal(0, vm.NeedleAngle, precision: 5);

        vm.ApplyIncomingVisual(1); // +1 tick
        Assert.Equal(BlueprintControlVm.RelativeDegreesPerTick, vm.NeedleAngle, precision: 5);

        vm.ApplyIncomingVisual(1);
        Assert.Equal(BlueprintControlVm.RelativeDegreesPerTick * 2, vm.NeedleAngle, precision: 5);

        vm.ApplyIncomingVisual(65); // -1 tick
        Assert.Equal(BlueprintControlVm.RelativeDegreesPerTick, vm.NeedleAngle, precision: 5);

        // Must not jump to absolute 1/127 or 65/127 positions.
        Assert.True(vm.UsesRelativeNeedle);
    }
}

public class MidiConflictValidatorTests
{
    [Fact]
    public void RequiresConflictConfirmation_WhenSecondAbsoluteFaderOnSameChannel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-map-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MidiMappingStore(path);
            store.Upsert(new MidiBinding
            {
                DeviceName = "Pad A",
                Controller = 1,
                ChannelId = "game",
                Mode = MidiValueMode.Absolute,
                Action = MidiBindingAction.Volume
            });

            var candidate = new MidiBinding
            {
                DeviceName = "Pad B",
                Controller = 2,
                ChannelId = "game",
                Mode = MidiValueMode.Absolute,
                Action = MidiBindingAction.Volume
            };

            Assert.True(MidiConflictValidator.RequiresConflictConfirmation(store, candidate, out var conflicts));
            Assert.Single(conflicts);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void RequiresConflictConfirmation_ExemptsUnassignedChannel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-map-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MidiMappingStore(path);
            store.Upsert(new MidiBinding
            {
                DeviceName = "Pad A",
                Controller = 1,
                ChannelId = "game",
                Mode = MidiValueMode.Absolute
            });

            var unassigned = new MidiBinding
            {
                DeviceName = "Pad B",
                Controller = 2,
                ChannelId = MidiBinding.UnassignedChannelId,
                Mode = MidiValueMode.Absolute
            };

            Assert.False(MidiConflictValidator.RequiresConflictConfirmation(store, unassigned, out _));
            Assert.False(unassigned.HasSonarChannel);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void RequiresConflictConfirmation_ExemptsRelativeAndButtons()
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-map-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MidiMappingStore(path);
            store.Upsert(new MidiBinding
            {
                DeviceName = "Pad A",
                Controller = 1,
                ChannelId = "master",
                Mode = MidiValueMode.Absolute
            });

            var relative = new MidiBinding
            {
                DeviceName = "Pad B",
                Controller = 2,
                ChannelId = "master",
                Mode = MidiValueMode.Relative
            };
            var button = new MidiBinding
            {
                DeviceName = "Pad B",
                Controller = 3,
                ChannelId = "master",
                Mode = MidiValueMode.Absolute,
                Action = MidiBindingAction.MuteToggle,
                IsNote = true
            };

            Assert.False(MidiConflictValidator.RequiresConflictConfirmation(store, relative, out _));
            Assert.False(MidiConflictValidator.RequiresConflictConfirmation(store, button, out _));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

public class PresetCatalogTests
{
    [Fact]
    public void Resolve_MatchesOfficialDeviceName()
    {
        var official = Path.Combine(Path.GetTempPath(), $"midi-official-{Guid.NewGuid():N}");
        var user = Path.Combine(Path.GetTempPath(), $"midi-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            File.WriteAllText(
                Path.Combine(official, "demo.json"),
                """
                {
                  "name": "Demo Mixer",
                  "deviceMatch": [ "SMC-Mixer", "Demo" ],
                  "columns": 2,
                  "rows": 1,
                  "controls": [
                    { "id": "f1", "row": 0, "col": 0, "type": "fader", "label": "A" }
                  ]
                }
                """);

            var catalog = new PresetCatalog(official, user);
            var layout = catalog.Resolve("USB SMC-Mixer Port");
            Assert.Equal("Demo Mixer", layout.Name);
            Assert.Single(layout.Controls);
        }
        finally
        {
            Directory.Delete(official, recursive: true);
            Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public void Resolve_FallsBackToGeneric()
    {
        var official = Path.Combine(Path.GetTempPath(), $"midi-official-{Guid.NewGuid():N}");
        var user = Path.Combine(Path.GetTempPath(), $"midi-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            var catalog = new PresetCatalog(official, user);
            var layout = catalog.Resolve("Unknown Arduino Box");
            Assert.Equal("Generic Custom Grid", layout.Name);
            Assert.True(layout.Controls.Count > 0);
        }
        finally
        {
            Directory.Delete(official, recursive: true);
            Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public void Resolve_PrefersUserOverOfficial()
    {
        var official = Path.Combine(Path.GetTempPath(), $"midi-official-{Guid.NewGuid():N}");
        var user = Path.Combine(Path.GetTempPath(), $"midi-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            File.WriteAllText(
                Path.Combine(official, "a.json"),
                """{"name":"Official","deviceMatch":["Box"],"columns":1,"rows":1,"controls":[]}""");
            File.WriteAllText(
                Path.Combine(user, "b.json"),
                """{"name":"User","deviceMatch":["Box"],"columns":1,"rows":1,"controls":[]}""");

            var catalog = new PresetCatalog(official, user);
            Assert.Equal("User", catalog.Resolve("My Box").Name);
        }
        finally
        {
            Directory.Delete(official, recursive: true);
            Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public void BuildFactoryBindings_FromBakedHardware()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "Test",
            Controls =
            [
                new MidiLayoutControl
                {
                    Id = "e1",
                    Type = MidiControlType.Encoder,
                    Controller = 16,
                    DefaultMode = MidiValueMode.Relative,
                    RelativeEncoding = MidiRelativeEncoding.OffsetBinary
                },
                new MidiLayoutControl
                {
                    Id = "f1",
                    Type = MidiControlType.Fader,
                    Controller = 0,
                    IsPitchBend = true,
                    DefaultMode = MidiValueMode.Absolute
                },
                new MidiLayoutControl
                {
                    Id = "m1",
                    Type = MidiControlType.Button,
                    Controller = 16,
                    IsNote = true
                },
                new MidiLayoutControl { Id = "orphan", Type = MidiControlType.Button }
            ]
        };

        var bindings = PresetCatalog.BuildFactoryBindings(layout, "SMC-Mixer");
        Assert.Equal(3, bindings.Count);

        var enc = Assert.Single(bindings, b => b.ControlId == "e1");
        Assert.Equal(16, enc.Controller);
        Assert.False(enc.IsNote);
        Assert.False(enc.IsPitchBend);
        Assert.Equal(MidiValueMode.Relative, enc.Mode);
        Assert.False(enc.HasSonarChannel);

        var fader = Assert.Single(bindings, b => b.ControlId == "f1");
        Assert.True(fader.IsPitchBend);
        Assert.Equal("SMC-Mixer|P|0", fader.BindingKey);

        var mute = Assert.Single(bindings, b => b.ControlId == "m1");
        Assert.True(mute.IsNote);
        Assert.Equal(MidiBindingAction.None, mute.Action);
        Assert.Equal(MidiBindingAction.None, enc.Action);
        Assert.Equal(MidiBindingAction.None, fader.Action);
    }

    [Fact]
    public void OfficialSmcMixerPreset_BakesDawModeHardware()
    {
        var official = Path.Combine(AppContext.BaseDirectory, "Presets");
        if (!File.Exists(Path.Combine(official, "m-vave-smc-mixer.json")))
        {
            // Fallback: load from source tree when tests run without content copy.
            official = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "steelseries-sonar-tray", "Presets"));
        }

        var catalog = new PresetCatalog(official, Path.Combine(Path.GetTempPath(), $"midi-user-{Guid.NewGuid():N}"));
        var layout = catalog.Resolve("SMC-Mixer");
        Assert.Equal("M-VAVE SMC-Mixer", layout.Name);
        Assert.NotEmpty(layout.Regions);
        Assert.Contains(layout.Regions, r => r.Id == "chassis");
        Assert.Contains(layout.Regions, r => r.Id == "strip1");
        Assert.True(layout.Regions.All(r => r.HideBorder));
        Assert.True(layout.Regions.Single(r => r.Id == "strip1").KeepSpacing);
        Assert.True(layout.Regions.Single(r => r.Id == "transport").KeepSpacing);
        Assert.False(layout.Regions.Single(r => r.Id == "chassis").KeepSpacing);
        Assert.Equal(MidiContentJustify.SpaceBetween, layout.Regions.Single(r => r.Id == "encoders").ContentJustify);
        Assert.Equal(MidiContentJustify.SpaceEvenly, layout.Regions.Single(r => r.Id == "transport").ContentJustify);
        Assert.Equal(MidiContentJustify.Pack, layout.Regions.Single(r => r.Id == "strips").ContentJustify);
        MidiLayoutTreeOps.SyncRootGridExtent(layout);
        Assert.Equal(3, layout.Rows);
        Assert.Equal(8, layout.Columns);
        Assert.Equal("encoders", layout.Controls.Single(c => c.Id == "e1").RegionId);
        Assert.Equal("strip1", layout.Controls.Single(c => c.Id == "f1").RegionId);
        Assert.Equal("strip1", layout.Controls.Single(c => c.Id == "m1").RegionId);
        Assert.Equal("transport", layout.Controls.Single(c => c.Id == "tr_play").RegionId);

        var e1 = Assert.Single(layout.Controls, c => c.Id == "e1");
        Assert.Equal(16, e1.Controller);
        Assert.Equal(MidiValueMode.Relative, e1.DefaultMode);

        var f1 = Assert.Single(layout.Controls, c => c.Id == "f1");
        Assert.True(f1.IsPitchBend);
        Assert.Equal(0, f1.Controller);

        var m1 = Assert.Single(layout.Controls, c => c.Id == "m1");
        Assert.True(m1.IsNote);
        Assert.Equal(16, m1.Controller);

        var factory = PresetCatalog.BuildFactoryBindings(layout, "SMC-Mixer");
        Assert.True(factory.Count >= 40); // 8 enc + 8 fader + 32 channel buttons + 5 transport
        Assert.Contains(factory, b => b.ControlId == "e1" && b.Mode == MidiValueMode.Relative && b.Controller == 16);
        Assert.Contains(factory, b => b.ControlId == "f8" && b.IsPitchBend && b.Controller == 7);
        Assert.True(factory.All(b => b.Action == MidiBindingAction.None));
        Assert.Null(m1.DefaultAction);
    }
}

public class MidiBlueprintCellPanelJustifyTests
{
    [Fact]
    public void ComputeMainGaps_Pack_LeavesNoDistributedGaps()
    {
        var (before, between) = Controls.MidiBlueprintCellPanel.ComputeMainGaps(
            MidiContentJustify.Pack, available: 300, content: 200, trackCount: 3);
        Assert.Equal(0, before);
        Assert.Equal(0, between);
    }

    [Fact]
    public void ComputeMainGaps_SpaceBetween_SplitsExtraAcrossInnerGaps()
    {
        var (before, between) = Controls.MidiBlueprintCellPanel.ComputeMainGaps(
            MidiContentJustify.SpaceBetween, available: 300, content: 200, trackCount: 3);
        Assert.Equal(0, before);
        Assert.Equal(50, between); // 100 / (3-1)
    }

    [Fact]
    public void ComputeMainGaps_SpaceEvenly_IncludesEdgeGaps()
    {
        var (before, between) = Controls.MidiBlueprintCellPanel.ComputeMainGaps(
            MidiContentJustify.SpaceEvenly, available: 300, content: 200, trackCount: 3);
        Assert.Equal(25, before); // 100 / (3+1)
        Assert.Equal(25, between);
    }
}

public class MidiDevicePortNamingTests
{
    [Fact]
    public void IsAutoDuplicatePort_HidesMidiIn2WhenPrimaryExists()
    {
        var available = new[] { "SMC-Mixer", "MIDIIN2 (SMC-Mixer)" };
        Assert.True(MidiDevicePortNaming.IsAutoDuplicatePort("MIDIIN2 (SMC-Mixer)", available));
        Assert.False(MidiDevicePortNaming.IsAutoDuplicatePort("SMC-Mixer", available));
    }

    [Fact]
    public void PreferPrimaryDeviceName_SkipsSecondary()
    {
        Assert.Equal(
            "SMC-Mixer",
            MidiDevicePortNaming.PreferPrimaryDeviceName(["MIDIIN2 (SMC-Mixer)", "SMC-Mixer"]));
    }

    [Fact]
    public void MappingStore_HideAndReveal_Persists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-map-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MidiMappingStore(path);
            store.SetEnabledDevices(["SMC-Mixer", "MIDIIN2 (SMC-Mixer)"]);
            var available = new[] { "SMC-Mixer", "MIDIIN2 (SMC-Mixer)" };

            Assert.True(store.IsEffectivelyHidden("MIDIIN2 (SMC-Mixer)", available));
            Assert.Equal(1, store.DisableHiddenEnabledDevices(available));
            Assert.DoesNotContain(
                store.EnabledDevices,
                d => d.Equals("MIDIIN2 (SMC-Mixer)", StringComparison.OrdinalIgnoreCase));

            store.RevealDevice("MIDIIN2 (SMC-Mixer)");
            Assert.False(store.IsEffectivelyHidden("MIDIIN2 (SMC-Mixer)", available));

            store.HideDevice("SMC-Mixer");
            Assert.True(store.IsEffectivelyHidden("SMC-Mixer", available));
            Assert.DoesNotContain(store.EnabledDevices, d => d.Equals("SMC-Mixer", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FindByController_MatchesSiblingPrimaryPort()
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-map-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MidiMappingStore(path);
            store.Upsert(new MidiBinding
            {
                DeviceName = "MIDIIN2 (SMC-Mixer)",
                Controller = 0,
                IsPitchBend = true,
                ChannelId = "game",
                ControlId = "f1"
            });

            var found = store.FindByController("SMC-Mixer", 0, isNote: false, isPitchBend: true);
            Assert.NotNull(found);
            Assert.Equal("game", found!.ChannelId);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void MigrateSecondaryPortBindings_MovesGameOntoPrimary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-map-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MidiMappingStore(path);
            store.Upsert(new MidiBinding
            {
                DeviceName = "MIDIIN2 (SMC-Mixer)",
                Controller = 0,
                IsPitchBend = true,
                ChannelId = "game",
                ControlId = "f1"
            });
            store.Upsert(new MidiBinding
            {
                DeviceName = "MIDIIN2 (SMC-Mixer)",
                Controller = 1,
                IsPitchBend = true,
                ChannelId = "chatRender",
                ControlId = "f2"
            });

            var migrated = store.MigrateSecondaryPortBindings(["SMC-Mixer", "MIDIIN2 (SMC-Mixer)"]);
            Assert.True(migrated >= 2);
            Assert.Contains(store.Bindings, b =>
                b.DeviceName == "SMC-Mixer" && b.IsPitchBend && b.Controller == 0 && b.ChannelId == "game");
            Assert.Contains(store.Bindings, b =>
                b.DeviceName == "SMC-Mixer" && b.IsPitchBend && b.Controller == 1 && b.ChannelId == "chatRender");
            Assert.DoesNotContain(store.Bindings, b =>
                b.DeviceName.StartsWith("MIDIIN", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

public class FaderPriorityGuardTests
{
    [Fact]
    public async Task SchedulesRollback_WhenVolumeDriftsFromHardware()
    {
        using var guard = new FaderPriorityGuard();
        var binding = new MidiBinding
        {
            DeviceName = "Fader",
            Controller = 7,
            ChannelId = "master",
            Mode = MidiValueMode.Absolute,
            Path = SonarMixerPath.Monitoring
        };

        guard.RememberHardwareVolume(binding, 0.40f);

        VolumeNotificationState? rolled = null;
        var rolledTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.RollbackRequested += (_, volume, _, notification) =>
        {
            rolled = notification;
            Assert.Equal(0.40f, volume, precision: 5);
            rolledTcs.TrySetResult();
        };

        var snapshot = new SonarMixerSnapshot
        {
            IsStreamerMode = false,
            EnabledChannels = SonarChannels.All.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Channels = new Dictionary<string, SonarChannelSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["master"] = new SonarChannelSettings
                {
                    Monitoring = new SonarChannelState { Volume = 0.80f, Muted = false }
                }
            }
        };

        guard.ObserveSnapshot(snapshot, [binding]);

        var completed = await Task.WhenAny(rolledTcs.Task, Task.Delay(FaderPriorityGuard.RollbackWindowMs + 1500));
        Assert.Same(rolledTcs.Task, completed);
        Assert.NotNull(rolled);
        Assert.Contains("locked", rolled!.Value.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelsRollback_WhenHardwareOverwrites()
    {
        using var guard = new FaderPriorityGuard();
        var binding = new MidiBinding
        {
            DeviceName = "Fader",
            Controller = 7,
            ChannelId = "game",
            Mode = MidiValueMode.Absolute
        };

        guard.RememberHardwareVolume(binding, 0.25f);

        var rollbackFired = false;
        guard.RollbackRequested += (_, _, _, _) => rollbackFired = true;

        var drifted = new SonarMixerSnapshot
        {
            IsStreamerMode = false,
            EnabledChannels = SonarChannels.All.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Channels = new Dictionary<string, SonarChannelSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["game"] = new SonarChannelSettings
                {
                    Monitoring = new SonarChannelState { Volume = 0.90f }
                }
            }
        };

        guard.ObserveSnapshot(drifted, [binding]);
        await Task.Delay(200);
        guard.RememberHardwareVolume(binding, 0.55f);

        await Task.Delay(FaderPriorityGuard.RollbackWindowMs + 500);
        Assert.False(rollbackFired);
    }
}

public class MidiLayoutConstructorTests
{
    [Fact]
    public void ResolveDropZone_EdgesAndCenter()
    {
        Assert.Equal(MidiLayoutDropZone.Left, MidiLayoutTreeOps.ResolveDropZone(5, 50, 100, 100));
        Assert.Equal(MidiLayoutDropZone.Right, MidiLayoutTreeOps.ResolveDropZone(95, 50, 100, 100));
        Assert.Equal(MidiLayoutDropZone.Top, MidiLayoutTreeOps.ResolveDropZone(50, 5, 100, 100));
        Assert.Equal(MidiLayoutDropZone.Bottom, MidiLayoutTreeOps.ResolveDropZone(50, 95, 100, 100));
        Assert.Equal(MidiLayoutDropZone.Inside, MidiLayoutTreeOps.ResolveDropZone(50, 50, 100, 100));
    }

    [Fact]
    public void ResolveDropZoneBesideOnly_NeverReturnsInside()
    {
        Assert.Equal(MidiLayoutDropZone.Left, MidiLayoutTreeOps.ResolveDropZoneBesideOnly(5, 50, 100, 100));
        Assert.NotEqual(MidiLayoutDropZone.Inside, MidiLayoutTreeOps.ResolveDropZoneBesideOnly(50, 50, 100, 100));
        Assert.Equal(MidiLayoutDropZone.Left, MidiLayoutTreeOps.ResolveDropZoneBesideOnly(40, 50, 100, 100));
    }

    [Fact]
    public void PlaceNewControl_RejectsInsideOnControlTarget()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "T",
            Columns = 2,
            Rows = 1,
            Controls =
            [
                new MidiLayoutControl { Id = "a", Row = 0, Col = 0, Type = MidiControlType.Button, Label = "A" }
            ]
        };

        var next = new MidiLayoutControl { Id = "b", Type = MidiControlType.Button, Label = "B" };
        Assert.False(MidiLayoutTreeOps.PlaceNewControl(
            layout,
            next,
            targetRegionId: null,
            targetControlId: "a",
            MidiLayoutDropZone.Inside));
        Assert.DoesNotContain(layout.Controls, c => c.Id == "b");

        Assert.True(MidiLayoutTreeOps.PlaceNewControl(
            layout,
            next,
            targetRegionId: null,
            targetControlId: "a",
            MidiLayoutDropZone.Right));
        Assert.Contains(layout.Controls, c => c.Id == "b");
    }

    [Fact]
    public void MoveControl_RejectsSelfTarget_AndKeepsControl()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "T",
            Columns = 2,
            Rows = 1,
            Controls =
            [
                new MidiLayoutControl { Id = "a", Row = 0, Col = 0, Type = MidiControlType.Button, Label = "A" },
                new MidiLayoutControl { Id = "b", Row = 0, Col = 1, Type = MidiControlType.Button, Label = "B" }
            ]
        };

        Assert.False(MidiLayoutTreeOps.MoveControl(
            layout,
            "a",
            targetRegionId: null,
            targetControlId: "a",
            MidiLayoutDropZone.Right));
        Assert.Contains(layout.Controls, c => c.Id == "a");
        Assert.Equal(2, layout.Controls.Count);
    }

    [Fact]
    public void InsertControl_LeftOfB_ShiftsB()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "T",
            Columns = 2,
            Rows = 1,
            Controls =
            [
                new MidiLayoutControl { Id = "a", Row = 0, Col = 0, Type = MidiControlType.Button, Label = "A" },
                new MidiLayoutControl { Id = "b", Row = 0, Col = 1, Type = MidiControlType.Button, Label = "B" }
            ]
        };

        var x = new MidiLayoutControl { Id = "x", Type = MidiControlType.Button, Label = "X" };
        var slot = MidiLayoutTreeOps.SlotBesideControl(layout.Controls.Single(c => c.Id == "b"), MidiLayoutDropZone.Left);
        Assert.Equal(1, slot.Col);
        Assert.True(MidiLayoutTreeOps.InsertControl(layout, x, slot));

        Assert.Equal(0, layout.Controls.Single(c => c.Id == "a").Col);
        Assert.Equal(1, layout.Controls.Single(c => c.Id == "x").Col);
        Assert.Equal(2, layout.Controls.Single(c => c.Id == "b").Col);
    }

    [Fact]
    public void InsertControl_BetweenAAndB_Horizontally()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "T",
            Columns = 2,
            Rows = 1,
            Controls =
            [
                new MidiLayoutControl { Id = "a", Row = 0, Col = 0, Type = MidiControlType.Button, Label = "A" },
                new MidiLayoutControl { Id = "b", Row = 0, Col = 1, Type = MidiControlType.Button, Label = "B" }
            ]
        };

        // Between A and B == insert at B's col (horizontal).
        var slot = new MidiDropSlot(null, 0, 1, MidiLayoutShiftAxis.Horizontal);
        var x = new MidiLayoutControl { Id = "x", Type = MidiControlType.Button, Label = "X" };
        Assert.True(MidiLayoutTreeOps.InsertControl(layout, x, slot));
        Assert.Equal(0, layout.Controls.Single(c => c.Id == "a").Col);
        Assert.Equal(1, layout.Controls.Single(c => c.Id == "x").Col);
        Assert.Equal(2, layout.Controls.Single(c => c.Id == "b").Col);
    }

    [Fact]
    public void InsertControl_Below_ShiftsRows()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "T",
            Columns = 1,
            Rows = 2,
            Controls =
            [
                new MidiLayoutControl { Id = "a", Row = 0, Col = 0, Type = MidiControlType.Button, Label = "A" },
                new MidiLayoutControl { Id = "b", Row = 1, Col = 0, Type = MidiControlType.Button, Label = "B" }
            ]
        };

        var slot = MidiLayoutTreeOps.SlotBesideControl(layout.Controls.Single(c => c.Id == "a"), MidiLayoutDropZone.Bottom);
        var x = new MidiLayoutControl { Id = "x", Type = MidiControlType.Button, Label = "X" };
        Assert.True(MidiLayoutTreeOps.InsertControl(layout, x, slot));
        Assert.Equal(0, layout.Controls.Single(c => c.Id == "a").Row);
        Assert.Equal(1, layout.Controls.Single(c => c.Id == "x").Row);
        Assert.Equal(2, layout.Controls.Single(c => c.Id == "b").Row);
    }

    [Fact]
    public void MoveControlToSlot_RejectsSameCell()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "T",
            Columns = 1,
            Rows = 1,
            Controls =
            [
                new MidiLayoutControl { Id = "a", Row = 0, Col = 0, Type = MidiControlType.Button, Label = "A" }
            ]
        };

        Assert.False(MidiLayoutTreeOps.MoveControlToSlot(
            layout,
            "a",
            new MidiDropSlot(null, 0, 0, MidiLayoutShiftAxis.Horizontal)));
        Assert.Single(layout.Controls);
    }

    [Fact]
    public void TryResolveInsertSlot_GapBetweenChildren()
    {
        var children = new List<MidiDropHitChild>
        {
            new()
            {
                Id = "a",
                Row = 0,
                Col = 0,
                Bounds = new System.Windows.Rect(0, 0, 40, 40)
            },
            new()
            {
                Id = "b",
                Row = 0,
                Col = 1,
                Bounds = new System.Windows.Rect(50, 0, 40, 40)
            }
        };

        Assert.True(MidiLayoutTreeOps.TryResolveInsertSlot(
            children, x: 45, y: 20, parentRegionId: null, rowSpan: 1, colSpan: 1, excludeId: null, out var slot));
        Assert.Equal(MidiLayoutShiftAxis.Horizontal, slot.Axis);
        Assert.Equal(1, slot.Col);
        Assert.Equal(0, slot.Row);

        // Pointer in empty space (not on child, not in gap) must not invent a free-cell slot.
        Assert.False(MidiLayoutTreeOps.TryResolveInsertSlot(
            children, x: 200, y: 200, parentRegionId: null, rowSpan: 1, colSpan: 1, excludeId: null, out _,
            allowEmptyFreeCell: false));
    }

    [Fact]
    public void PlaceNewRegion_NestedInsideAndBeside()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "Nest",
            Columns = 4,
            Rows = 2,
            Controls = []
        };

        Assert.True(MidiLayoutTreeOps.PlaceNewRegion(layout, null, MidiLayoutDropZone.Inside, "Chassis", out var chassis));
        Assert.True(MidiLayoutTreeOps.PlaceNewRegion(layout, chassis, MidiLayoutDropZone.Inside, "Strip", out var strip));
        Assert.True(MidiLayoutTreeOps.PlaceNewRegion(layout, strip, MidiLayoutDropZone.Right, "Next", out var next));

        var stripRegion = layout.Regions.Single(r => r.Id == strip);
        var nextRegion = layout.Regions.Single(r => r.Id == next);
        Assert.Equal(chassis, stripRegion.ParentRegionId);
        Assert.Equal(chassis, nextRegion.ParentRegionId);
        Assert.True(nextRegion.Col > stripRegion.Col);
    }

    [Fact]
    public void MoveControl_BetweenRegions_UpdatesRegionMembership()
    {
        var layout = new MidiDeviceLayout
        {
            Name = "Pad",
            Columns = 4,
            Rows = 2,
            Regions =
            [
                new MidiLayoutRegion { Id = "a", Label = "A", Row = 0, Col = 0 },
                new MidiLayoutRegion { Id = "b", Label = "B", Row = 0, Col = 1 }
            ],
            Controls =
            [
                new MidiLayoutControl { Id = "btn1", RegionId = "a", Row = 0, Col = 0, Type = MidiControlType.Button, Label = "1" },
                new MidiLayoutControl { Id = "btn2", RegionId = "b", Row = 0, Col = 0, Type = MidiControlType.Button, Label = "2" },
                new MidiLayoutControl { Id = "f1", RegionId = "a", Row = 1, Col = 0, Type = MidiControlType.Fader, Label = "F" }
            ]
        };

        Assert.True(MidiLayoutTreeOps.MoveControl(
            layout,
            "btn1",
            targetRegionId: "b",
            targetControlId: null,
            MidiLayoutDropZone.Inside));

        Assert.Equal("b", layout.Controls.Single(c => c.Id == "btn1").RegionId);
        Assert.Equal("a", layout.Controls.Single(c => c.Id == "f1").RegionId);
        Assert.Equal("b", layout.Controls.Single(c => c.Id == "btn2").RegionId);
    }

    [Fact]
    public void SaveUserLayout_RoundTripsAndRestoreDeletesOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), $"midi-presets-{Guid.NewGuid():N}");
        var official = Path.Combine(root, "official");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            File.WriteAllText(Path.Combine(official, "box.json"), """
                {
                  "name": "Factory",
                  "deviceMatch": ["DiyPad"],
                  "columns": 2,
                  "rows": 1,
                  "controls": [
                    { "id": "f1", "row": 0, "col": 0, "type": "fader", "label": "F1" }
                  ]
                }
                """);

            var catalog = new PresetCatalog(official, user);
            var layout = catalog.Resolve("DiyPad");
            layout.Name = "DiyPad Custom";
            layout.Columns = 4;
            layout.Controls.Add(new MidiLayoutControl
            {
                Id = "e_custom_1",
                Row = 0,
                Col = 1,
                Type = MidiControlType.Encoder,
                Label = "ENC"
            });
            layout.DeviceMatch = ["DiyPad"];

            var path = catalog.SaveUserLayout(layout);
            Assert.True(File.Exists(path));

            var reloaded = new PresetCatalog(official, user).Resolve("DiyPad");
            Assert.Equal("DiyPad Custom", reloaded.Name);
            Assert.Equal(4, reloaded.Columns);
            Assert.Contains(reloaded.Controls, c => c.Id == "e_custom_1");

            Assert.Equal(1, catalog.DeleteUserLayoutOverride("DiyPad"));
            var afterRestore = new PresetCatalog(official, user).Resolve("DiyPad");
            Assert.Equal("Factory", afterRestore.Name);
            Assert.DoesNotContain(afterRestore.Controls, c => c.Id == "e_custom_1");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MultipleUserPresets_CanSelectIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), $"midi-multi-{Guid.NewGuid():N}");
        var official = Path.Combine(root, "official");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            File.WriteAllText(Path.Combine(official, "box.json"), """
                {
                  "name": "Factory",
                  "deviceMatch": ["MultiPad"],
                  "controls": [ { "id": "f1", "row": 0, "col": 0, "type": "fader", "label": "F1" } ]
                }
                """);

            var catalog = new PresetCatalog(official, user);
            var a = catalog.Resolve("MultiPad", MidiPresetSelectionStore.OfficialKey);
            a.Name = "Night";
            a.DeviceMatch = ["MultiPad"];
            var pathA = catalog.SaveUserLayout(a, createNewFile: true);

            var b = catalog.Resolve("MultiPad", MidiPresetSelectionStore.OfficialKey);
            b.Name = "Day";
            b.DeviceMatch = ["MultiPad"];
            var pathB = catalog.SaveUserLayout(b, createNewFile: true);

            Assert.NotEqual(Path.GetFileName(pathA), Path.GetFileName(pathB));

            var presets = catalog.ListPresetsForDevice("MultiPad");
            Assert.Equal(3, presets.Count); // official + 2 user
            Assert.Contains(presets, p => p.IsOfficial);
            Assert.Equal(2, presets.Count(p => p.IsUser));

            catalog.SetActivePresetKey("MultiPad", MidiPresetSelectionStore.UserKey(Path.GetFileName(pathB)));
            Assert.Equal("Day", catalog.Resolve("MultiPad").Name);

            catalog.SetActivePresetKey("MultiPad", MidiPresetSelectionStore.OfficialKey);
            Assert.Equal("Factory", catalog.Resolve("MultiPad").Name);

            Assert.True(catalog.DeleteUserPresetFile(Path.GetFileName(pathA)));
            Assert.Equal(2, catalog.ListPresetsForDevice("MultiPad").Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TrySaveLayoutPresetAs_CreatesNamedUserPreset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"midi-saveas-{Guid.NewGuid():N}");
        var official = Path.Combine(root, "official");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            File.WriteAllText(Path.Combine(official, "box.json"), """
                {
                  "name": "Factory",
                  "deviceMatch": ["SaveAsPad"],
                  "controls": [ { "id": "f1", "row": 0, "col": 0, "type": "fader", "label": "F1", "controller": 1 } ]
                }
                """);

            var catalog = new PresetCatalog(official, user);
            using var midi = new MidiControlService(new SonarQuickMixer.Settings.AppSettings(), new MidiMappingStore(Path.Combine(root, "m.json")));
            using var controller = new MidiConfigController(midi, catalog);
            controller.SelectedDeviceName = "SaveAsPad";

            Assert.True(controller.TrySaveLayoutPresetAs("Streaming Mix", out var error), error);
            Assert.Equal("Streaming Mix", controller.LayoutName);
            Assert.Contains(catalog.ListPresetsForDevice("SaveAsPad"), p => p.IsUser && p.DisplayName.Contains("Streaming Mix"));
            Assert.Equal(2, catalog.ListPresetsForDevice("SaveAsPad").Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Constructor_MoveOrSwap_AndSaveCleansOrphanBindings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"midi-ctor-{Guid.NewGuid():N}");
        var official = Path.Combine(root, "official");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            File.WriteAllText(Path.Combine(official, "pad.json"), """
                {
                  "name": "Pad",
                  "deviceMatch": ["CtorPad"],
                  "columns": 2,
                  "rows": 1,
                  "controls": [
                    { "id": "f1", "row": 0, "col": 0, "type": "fader", "label": "F1", "controller": 1 },
                    { "id": "f2", "row": 0, "col": 1, "type": "fader", "label": "F2", "controller": 2 }
                  ]
                }
                """);

            var mapPath = Path.Combine(root, "midi-mappings.json");
            var store = new MidiMappingStore(mapPath);
            store.Upsert(new MidiBinding
            {
                DeviceName = "CtorPad",
                Controller = 1,
                IsPitchBend = false,
                ChannelId = "game",
                ControlId = "f1",
                Mode = MidiValueMode.Absolute
            });
            store.Upsert(new MidiBinding
            {
                DeviceName = "CtorPad",
                Controller = 2,
                ChannelId = "media",
                ControlId = "f2",
                Mode = MidiValueMode.Absolute
            });

            var catalog = new PresetCatalog(official, user);
            var settings = new SonarQuickMixer.Settings.AppSettings();
            using var midi = new MidiControlService(settings, store);
            using var controller = new MidiConfigController(midi, catalog);

            controller.SelectedDeviceName = "CtorPad";
            Assert.Equal(2, controller.Controls.Count(c => !c.IsPlaceholder));

            controller.EnterLayoutConstructor();
            Assert.True(controller.IsLayoutConstructorMode);
            Assert.True(controller.ConstructorDrop(
                "control:f1",
                targetRegionId: null,
                targetControlId: "f2",
                MidiLayoutDropZone.Left));

            Assert.Contains(controller.Controls, c => c.Id == "f1");
            Assert.Contains(controller.Controls, c => c.Id == "f2");

            Assert.True(controller.DeleteDraftControl("f2"));
            Assert.True(controller.SaveLayoutConstructor());
            Assert.False(controller.IsLayoutConstructorMode);
            Assert.True(catalog.HasUserOverride("CtorPad"));

            Assert.Contains(store.Bindings, b => b.ControlId == "f1");
            Assert.DoesNotContain(store.Bindings, b => b.ControlId == "f2");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Constructor_delete_chrome_only_when_selected_removes_that_item_only()
    {
        var root = Path.Combine(Path.GetTempPath(), $"midi-del-{Guid.NewGuid():N}");
        var official = Path.Combine(root, "official");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            File.WriteAllText(Path.Combine(official, "nested.json"), """
                {
                  "name": "Nested",
                  "deviceMatch": ["DelNest"],
                  "columns": 2,
                  "rows": 1,
                  "regions": [
                    { "id": "outer", "row": 0, "col": 0, "colSpan": 2, "hideBorder": true },
                    { "id": "inner", "parentRegionId": "outer", "row": 0, "col": 0, "hideBorder": true }
                  ],
                  "controls": [
                    { "id": "b1", "regionId": "inner", "row": 0, "col": 0, "type": "button", "label": "B", "controller": 1 }
                  ]
                }
                """);

            var catalog = new PresetCatalog(official, user);
            var settings = new SonarQuickMixer.Settings.AppSettings();
            using var midi = new MidiControlService(settings, new MidiMappingStore(Path.Combine(root, "midi-mappings.json")));
            using var controller = new MidiConfigController(midi, catalog);

            controller.SelectedDeviceName = "DelNest";
            controller.EnterLayoutConstructor();

            var outer = FindRegion(controller.ConstructorRoots, "outer");
            var inner = FindRegion(controller.ConstructorRoots, "inner");
            var button = controller.Controls.Single(c => c.Id == "b1");
            Assert.NotNull(outer);
            Assert.NotNull(inner);
            Assert.False(outer!.ShowDeleteButton);
            Assert.False(inner!.ShowDeleteButton);
            Assert.False(button.ShowDeleteButton);

            // × is the protection against deleting the wrong nested item: only the selected one shows it.
            inner.IsSelected = true;
            Assert.True(inner.ShowDeleteButton);
            Assert.False(outer.ShowDeleteButton);
            Assert.False(button.ShowDeleteButton);

            inner.IsSelected = false;
            button.IsSelected = true;
            Assert.True(button.ShowDeleteButton);
            Assert.False(inner.ShowDeleteButton);

            Assert.True(controller.DeleteDraftControl("b1"));
            Assert.DoesNotContain(controller.Controls, c => c.Id == "b1");
            Assert.NotNull(FindRegion(controller.ConstructorRoots, "outer"));
            Assert.NotNull(FindRegion(controller.ConstructorRoots, "inner"));

            Assert.True(controller.DeleteDraftRegion("inner", deleteContents: false));
            Assert.Null(FindRegion(controller.ConstructorRoots, "inner"));
            Assert.NotNull(FindRegion(controller.ConstructorRoots, "outer"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void BlueprintZoom_ClampsAndReportsPercent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"midi-zoom-{Guid.NewGuid():N}");
        var official = Path.Combine(root, "official");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(user);

        try
        {
            File.WriteAllText(Path.Combine(official, "pad.json"), """
                {
                  "name": "Pad",
                  "deviceMatch": ["ZoomPad"],
                  "columns": 1,
                  "rows": 1,
                  "controls": [
                    { "id": "f1", "row": 0, "col": 0, "type": "fader", "label": "F1", "controller": 1 }
                  ]
                }
                """);

            var catalog = new PresetCatalog(official, user);
            using var midi = new MidiControlService(new SonarQuickMixer.Settings.AppSettings(), new MidiMappingStore(Path.Combine(root, "m.json")));
            using var controller = new MidiConfigController(midi, catalog);
            controller.SelectedDeviceName = "ZoomPad";

            Assert.Equal(1.0, controller.BlueprintZoom);
            Assert.Equal("100%", controller.BlueprintZoomPercent);

            controller.ZoomBlueprintIn();
            Assert.Equal(1.1, controller.BlueprintZoom, 3);
            Assert.Equal("110%", controller.BlueprintZoomPercent);

            controller.BlueprintZoom = 99;
            Assert.Equal(MidiConfigController.BlueprintZoomMax, controller.BlueprintZoom);
            Assert.False(controller.CanZoomBlueprintIn);

            controller.BlueprintZoom = 0.01;
            Assert.Equal(MidiConfigController.BlueprintZoomMin, controller.BlueprintZoom);
            Assert.False(controller.CanZoomBlueprintOut);

            controller.ResetBlueprintZoom();
            Assert.Equal(1.0, controller.BlueprintZoom);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void BlueprintControl_ChannelCaption_AndPassive_DependOnSonarChannel()
    {
        var vm = new BlueprintControlVm
        {
            Id = "f1",
            Label = "CH1",
            Type = MidiControlType.Fader,
            Row = 0,
            Col = 0
        };

        Assert.True(vm.IsPassive);
        Assert.False(vm.ShowChannelCaption);

        Assert.True(vm.ShowMissingHardwareWarning);
        vm.Controller = 16;
        Assert.False(vm.ShowMissingHardwareWarning);
        vm.Controller = null;
        Assert.True(vm.ShowMissingHardwareWarning);

        var ctorVm = new BlueprintControlVm
        {
            Id = "f2",
            Label = "CH2",
            Type = MidiControlType.Fader,
            Row = 0,
            Col = 0,
            IsConstructorMode = true
        };
        Assert.False(ctorVm.IsPassive);

        vm.ChannelId = "game";
        Assert.False(vm.IsPassive);
        Assert.True(vm.HasSonarChannel);
        Assert.True(vm.ShowChannelCaption);
        Assert.Equal(SonarChannels.GetDisplayName("game"), vm.ChannelCaption);
        Assert.DoesNotContain("CC", vm.ChannelCaption, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PB", vm.ChannelCaption, StringComparison.OrdinalIgnoreCase);

        vm.ChannelId = MidiBinding.UnassignedChannelId;
        Assert.True(vm.IsPassive);
        Assert.False(vm.ShowChannelCaption);
    }

    private static BlueprintRegionVm? FindRegion(IEnumerable<object> roots, string id)
    {
        foreach (var root in roots)
        {
            if (root is BlueprintRegionVm region)
            {
                if (string.Equals(region.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return region;
                }

                var nested = FindRegion(region.Children, id);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}

public class MidiControlStateStoreTests
{
    [Fact]
    public void RoundTrip_PersistsClampedVolumeByBindingKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-state-{Guid.NewGuid():N}.json");
        try
        {
            using (var store = new MidiControlStateStore(path))
            {
                store.Set("Pad|C|1", 1.5f);
                store.Set("Pad|P|0", -0.2f);
                store.Flush();
            }

            using var reloaded = new MidiControlStateStore(path);
            Assert.True(reloaded.TryGet("Pad|C|1", out var abs));
            Assert.Equal(1f, abs, precision: 5);
            Assert.True(reloaded.TryGet("Pad|P|0", out var pb));
            Assert.Equal(0f, pb, precision: 5);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void IsPersistableAbsoluteVolume_RequiresAbsoluteVolumeWithChannel()
    {
        Assert.True(MidiControlStateStore.IsPersistableAbsoluteVolume(new MidiBinding
        {
            DeviceName = "Pad",
            Controller = 1,
            ChannelId = "game",
            Mode = MidiValueMode.Absolute,
            Action = MidiBindingAction.Volume
        }));

        Assert.False(MidiControlStateStore.IsPersistableAbsoluteVolume(new MidiBinding
        {
            DeviceName = "Pad",
            Controller = 2,
            ChannelId = "game",
            Mode = MidiValueMode.Relative,
            Action = MidiBindingAction.Volume
        }));

        Assert.False(MidiControlStateStore.IsPersistableAbsoluteVolume(new MidiBinding
        {
            DeviceName = "Pad",
            Controller = 3,
            ChannelId = "game",
            Mode = MidiValueMode.Absolute,
            Action = MidiBindingAction.MuteToggle,
            IsNote = true
        }));

        Assert.False(MidiControlStateStore.IsPersistableAbsoluteVolume(new MidiBinding
        {
            DeviceName = "Pad",
            Controller = 4,
            ChannelId = MidiBinding.UnassignedChannelId,
            Mode = MidiValueMode.Absolute,
            Action = MidiBindingAction.Volume
        }));
    }

    [Fact]
    public void SetFromBinding_IgnoresRelativeAndPruneDropsOrphans()
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-state-{Guid.NewGuid():N}.json");
        try
        {
            using var store = new MidiControlStateStore(path);
            var absolute = new MidiBinding
            {
                DeviceName = "Pad",
                Controller = 1,
                ChannelId = "media",
                Mode = MidiValueMode.Absolute,
                Action = MidiBindingAction.Volume
            };
            var relative = new MidiBinding
            {
                DeviceName = "Pad",
                Controller = 2,
                ChannelId = "media",
                Mode = MidiValueMode.Relative,
                Action = MidiBindingAction.Volume
            };

            store.SetFromBinding(absolute, 0.42f);
            store.SetFromBinding(relative, 0.8f);
            store.Set("orphan|C|9", 0.1f);
            store.Flush();

            Assert.True(store.TryGet(absolute.BindingKey, out var v));
            Assert.Equal(0.42f, v, precision: 5);
            Assert.False(store.TryGet(relative.BindingKey, out _));

            store.PruneTo([absolute.BindingKey]);
            store.Flush();
            Assert.False(store.TryGet("orphan|C|9", out _));
            Assert.True(store.TryGet(absolute.BindingKey, out _));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void MidiControlService_TryGetAbsoluteVisual_PrefersPersistedPosition()
    {
        var root = Path.Combine(Path.GetTempPath(), $"midi-visual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "state.json");
            using var state = new MidiControlStateStore(statePath);
            using var midi = new MidiControlService(
                new SonarQuickMixer.Settings.AppSettings(),
                new MidiMappingStore(Path.Combine(root, "map.json")),
                controlStateStore: state);

            var binding = new MidiBinding
            {
                DeviceName = "Pad",
                Controller = 3,
                ChannelId = "game",
                Mode = MidiValueMode.Absolute,
                Action = MidiBindingAction.Volume
            };
            state.Set(binding.BindingKey, 0.73f);

            Assert.True(midi.TryGetAbsoluteVisual(binding, out var volume));
            Assert.Equal(0.73f, volume, precision: 5);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void BlueprintControlVm_NormalizedValue_DrivesFaderOffset()
    {
        var vm = new BlueprintControlVm
        {
            Id = "f1",
            Label = "F1",
            Type = MidiControlType.Fader,
            Mode = MidiValueMode.Absolute,
            Row = 0,
            Col = 0,
            TallFader = false
        };

        Assert.Equal(0f, vm.NormalizedValue);
        Assert.Equal(vm.FaderTrackHeight - 14.0, vm.FaderOffset, precision: 5);

        vm.NormalizedValue = 1f;
        Assert.Equal(0.0, vm.FaderOffset, precision: 5);

        vm.NormalizedValue = 0.5f;
        Assert.Equal((1.0 - 0.5) * (vm.FaderTrackHeight - 14.0), vm.FaderOffset, precision: 5);
    }
}
