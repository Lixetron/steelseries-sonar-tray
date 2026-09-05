using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Explicit "Learn MIDI" workflow, plus passive auto-discovery of unbound hardware from raw
/// MIDI/feedback traffic (CC-shape heuristics, unbound-slot picking). Split out of
/// MidiConfigController.cs purely to keep that file a manageable size — same partial class,
/// same fields/state, no behavior change.
/// </summary>
public partial class MidiConfigController
{
    public async Task StartLearnAsync(BlueprintControlVm control)
    {
        if (!CanLearnHardware)
        {
            StatusText = "Use device and open Edit layout to Learn hardware.";
            return;
        }

        if (Devices.All(d => !d.IsEnabled) && string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Enable a MIDI device first.";
            return;
        }

        CancelLearn();
        _learningControl = control;
        control.IsLearning = true;
        StatusText = "Listening… move the hardware control now (or Cancel Learn).";

        _learnCts = new CancellationTokenSource();
        try
        {
            var mode = control.DefaultMode ?? MidiValueMode.Absolute;
            // null device filter → any enabled port (avoids MIDIIN2 vs primary mismatch)
            var evt = await _midi.BeginLearnAsync(
                deviceName: null,
                control.Id,
                mode,
                _learnCts.Token).ConfigureAwait(true);

            ApplyLearnedBinding(control, evt, mode);
        }
        catch (OperationCanceledException)
        {
            StatusText = "MIDI Learn cancelled.";
        }
        finally
        {
            control.IsLearning = false;
            if (ReferenceEquals(_learningControl, control))
            {
                _learningControl = null;
            }
        }
    }

    public void CancelLearn()
    {
        _learnCts?.Cancel();
        _learnCts?.Dispose();
        _learnCts = null;
        _midi.CancelLearn();
        if (_learningControl is not null)
        {
            _learningControl.IsLearning = false;
            _learningControl = null;
        }
    }

    private void ApplyLearnedBinding(BlueprintControlVm control, MidiIncomingEvent evt, MidiValueMode mode)
    {
        var action = MidiBindingActions.DefaultFor(control.Type);

        // Explicit Learn may steal hardware from another slot (needed when devices share CCs across controls).
        var previousOwner = Controls.FirstOrDefault(c =>
            !c.IsPlaceholder
            && !ReferenceEquals(c, control)
            && c.Controller == evt.Controller
            && c.IsNote == evt.IsNote
            && c.IsPitchBend == evt.IsPitchBend);
        if (previousOwner is not null)
        {
            ResetControlVm(previousOwner);
        }

        _midi.MappingStore.RemoveMatching(b =>
            b.Controller == evt.Controller
            && b.IsNote == evt.IsNote
            && b.IsPitchBend == evt.IsPitchBend
            && DevicesShareProduct(b.DeviceName, evt.DeviceName)
            && !string.Equals(b.ControlId, control.Id, StringComparison.OrdinalIgnoreCase));

        var previous = _midi.MappingStore.FindByControlId(evt.DeviceName, control.Id)
                       ?? (!string.IsNullOrWhiteSpace(SelectedDeviceName)
                           ? _midi.MappingStore.FindByControlId(SelectedDeviceName, control.Id)
                           : null);

        var resolvedMode = control.Type == MidiControlType.Button
            ? MidiValueMode.Absolute
            : mode;
        if (control.Type == MidiControlType.Encoder && !evt.IsNote && !evt.IsPitchBend)
        {
            var probe = GetProbe(evt);
            if (probe.LooksLikeRelativeTicks)
            {
                resolvedMode = MidiValueMode.Relative;
            }
        }

        if (evt.IsPitchBend)
        {
            resolvedMode = MidiValueMode.Absolute;
        }

        var binding = new MidiBinding
        {
            DeviceName = evt.DeviceName,
            Controller = evt.Controller,
            IsNote = evt.IsNote,
            IsPitchBend = evt.IsPitchBend,
            ChannelId = previous is { HasSonarChannel: true }
                ? previous.ChannelId
                : MidiBinding.UnassignedChannelId,
            Path = previous?.Path ?? SonarMixerPath.Monitoring,
            Mode = resolvedMode,
            Action = action,
            ControlId = control.Id,
            IsMotorized = previous?.IsMotorized ?? false,
            RelativeEncoding = previous?.RelativeEncoding ?? MidiRelativeEncoding.OffsetBinary,
            RelativeStep = previous?.RelativeStep
        };

        _midi.MappingStore.Upsert(binding);
        ApplyBindingToVm(control, binding);
        control.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
        var stolen = previousOwner is null ? string.Empty : $" (moved from {previousOwner.Label})";
        var hw = MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend);
        StatusText = binding.HasSonarChannel
            ? $"Linked {control.Label} to {hw} → {MidiBinding.FormatChannelLabel(binding.ChannelId)}.{stolen}"
            : $"Discovered {control.Label} as {hw} on {evt.DeviceName}.{stolen}";
    }

    private BlueprintControlVm? FindControlOwningHardware(MidiIncomingEvent evt) =>
        Controls.FirstOrDefault(c =>
            !c.IsPlaceholder
            && c.Controller == evt.Controller
            && c.IsNote == evt.IsNote
            && c.IsPitchBend == evt.IsPitchBend);

    private void OnRawEventForDiscovery(MidiIncomingEvent evt)
    {
        if (_disposed || IsLearningActive)
        {
            return;
        }

        if (!IsEventFromActiveDevice(evt.DeviceName))
        {
            return;
        }

        if (evt.IsNote && !evt.IsNoteOn)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            var hw = MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend);
            var raw = MidiValueParser.FormatRawDisplay(evt.RawValue, evt.IsPitchBend);
            StatusText = $"MIDI ← {evt.DeviceName}: {hw} = {raw}";
            TryAutoDiscover(evt);
        });
    }

    private void TryAutoDiscover(MidiIncomingEvent evt)
    {
        if (IsLearningActive)
        {
            return;
        }

        ObserveCc(evt);
        var probe = GetProbe(evt);

        var existingVm = Controls.FirstOrDefault(c =>
            !c.IsPlaceholder
            && c.Controller == evt.Controller
            && c.IsNote == evt.IsNote
            && c.IsPitchBend == evt.IsPitchBend);

        if (existingVm is not null)
        {
            existingVm.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
            if (existingVm.Type == MidiControlType.Button)
            {
                existingVm.IsPressed = evt.RawValue > 0;
            }

            // Shared-CC hardware: a button may share a controller with an encoder on the same device.
            if (existingVm.Type is MidiControlType.Encoder or MidiControlType.Fader
                && !evt.IsPitchBend
                && (evt.IsNote || probe.LooksLikeButton || evt.RawValue is 0 or 127))
            {
                StatusText =
                    $"{MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend)} is already on {existingVm.Label}. " +
                    "Same CC as this button. If your device has a Note/Pitch Bend mode for unique buttons+faders, use that, " +
                    "or Reset that encoder and Learn the button instead.";
            }

            return;
        }

        // Another blueprint slot already owns this hardware — do not silently create a duplicate.
        var owner = FindControlOwningHardware(evt);
        if (owner is not null)
        {
            owner.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
            StatusText =
                $"{MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend)} already bound to {owner.Label}. " +
                "Click a free slot and Learn to reassign, or Reset the owner first.";
            return;
        }

        // Wait until we know if this CC is continuous (encoder/fader) or a 0/127 button.
        // Pitch Bend faders (SMC-Mixer DAW Mode / Mode A) are ready immediately.
        if (!evt.IsNote && !evt.IsPitchBend && !probe.IsReadyToClassify)
        {
            return;
        }

        var storeBinding = FindStoredBinding(evt);
        if (storeBinding is not null)
        {
            var byId = !string.IsNullOrWhiteSpace(storeBinding.ControlId)
                ? Controls.FirstOrDefault(c =>
                    string.Equals(c.Id, storeBinding.ControlId, StringComparison.OrdinalIgnoreCase)
                    && c.Controller is null
                    && !c.IsPlaceholder)
                : null;

            var target = byId ?? PickUnboundControl(evt, probe);
            if (target is null)
            {
                return;
            }

            storeBinding.ControlId = target.Id;
            storeBinding.DeviceName = evt.DeviceName;
            storeBinding.IsPitchBend = evt.IsPitchBend;
            storeBinding.Action = MidiBindingActions.DefaultFor(target.Type);
            storeBinding.Mode = target.Type == MidiControlType.Encoder
                ? (target.DefaultMode ?? MidiValueMode.Absolute)
                : MidiValueMode.Absolute;
            if (target.Type == MidiControlType.Button || evt.IsPitchBend)
            {
                storeBinding.Mode = MidiValueMode.Absolute;
            }

            _midi.MappingStore.Upsert(storeBinding);
            ApplyBindingToVm(target, storeBinding);
            target.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
            if (target.Type == MidiControlType.Button)
            {
                target.IsPressed = evt.RawValue > 0;
            }

            return;
        }

        var free = PickUnboundControl(evt, probe);
        if (free is null)
        {
            return;
        }

        var mode = free.Type switch
        {
            MidiControlType.Button => MidiValueMode.Absolute,
            _ when evt.IsPitchBend => MidiValueMode.Absolute,
            MidiControlType.Encoder when probe.LooksLikeRelativeTicks => MidiValueMode.Relative,
            MidiControlType.Encoder => free.DefaultMode ?? MidiValueMode.Absolute,
            _ => free.DefaultMode ?? MidiValueMode.Absolute
        };

        var binding = new MidiBinding
        {
            DeviceName = evt.DeviceName,
            Controller = evt.Controller,
            IsNote = evt.IsNote,
            IsPitchBend = evt.IsPitchBend,
            ChannelId = MidiBinding.UnassignedChannelId,
            Path = SonarMixerPath.Monitoring,
            Mode = mode,
            Action = MidiBindingActions.DefaultFor(free.Type),
            ControlId = free.Id,
            IsMotorized = false
        };

        _midi.MappingStore.Upsert(binding);
        ApplyBindingToVm(free, binding);
        free.ApplyIncomingVisual(evt.RawValue, evt.IsPitchBend);
        if (free.Type == MidiControlType.Button)
        {
            free.IsPressed = evt.RawValue > 0;
        }

        StatusText =
            $"Discovered {free.Label} as {MidiBinding.FormatHardwareLabel(evt.IsNote, evt.Controller, evt.IsPitchBend)}. Assign a Sonar channel when ready.";
    }

    private MidiBinding? FindStoredBinding(MidiIncomingEvent evt)
    {
        var direct = _midi.MappingStore.FindByController(
            evt.DeviceName,
            evt.Controller,
            evt.IsNote,
            evt.IsPitchBend);
        if (direct is not null)
        {
            return direct;
        }

        // Same physical box may appear under a secondary port name vs the primary product name.
        return _midi.MappingStore.Bindings.FirstOrDefault(b =>
            b.Controller == evt.Controller
            && b.IsNote == evt.IsNote
            && b.IsPitchBend == evt.IsPitchBend
            && DevicesShareProduct(b.DeviceName, evt.DeviceName));
    }

    private void OnControlFeedback(MidiControlFeedback feedback)
    {
        if (_disposed)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            if (!IsEventFromActiveDevice(feedback.DeviceName))
            {
                return;
            }

            var matched = false;
            foreach (var control in Controls)
            {
                if (control.Controller == feedback.Controller
                    && control.IsNote == feedback.IsNote
                    && control.IsPitchBend == feedback.IsPitchBend)
                {
                    if (control.UsesRelativeNeedle)
                    {
                        control.ApplyIncomingVisual(feedback.RawValue, feedback.IsPitchBend);
                    }
                    else
                    {
                        // Prefer normalized (restore / absolute) over re-deriving from raw.
                        control.NormalizedValue = feedback.NormalizedValue;
                    }

                    if (control.Type == MidiControlType.Button)
                    {
                        control.IsPressed = feedback.RawValue > 0;
                    }

                    matched = true;
                }
            }

            if (!matched)
            {
                TryAutoDiscover(new MidiIncomingEvent(
                    feedback.DeviceName,
                    feedback.Controller,
                    feedback.RawValue,
                    feedback.IsNote,
                    IsNoteOn: feedback.IsNote && feedback.RawValue > 0,
                    feedback.IsPitchBend));
            }
        });
    }

    private bool IsEventFromActiveDevice(string deviceName)
    {
        var enabled = _midi.MappingStore.EnabledDevices;
        if (enabled.Count > 0)
        {
            return enabled.Any(d => string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase));
        }

        return !string.IsNullOrWhiteSpace(SelectedDeviceName)
               && string.Equals(deviceName, SelectedDeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DevicesShareProduct(string left, string right) =>
        MidiDevicePortNaming.DevicesShareProduct(left, right);

    private BlueprintControlVm? PickUnboundControl(MidiIncomingEvent evt, CcProbe probe)
    {
        var unbound = Controls.Where(c => !c.IsPlaceholder && c.Controller is null).ToList();
        if (unbound.Count == 0)
        {
            return null;
        }

        // DAW Mode faders: Pitch Bend channel 0–7 → CH1–CH8 by column.
        if (evt.IsPitchBend)
        {
            if (evt.Controller is >= 0 and <= 7)
            {
                var byCol = unbound.FirstOrDefault(c =>
                    c.Type == MidiControlType.Fader && c.Col == evt.Controller);
                if (byCol is not null)
                {
                    return byCol;
                }
            }

            return unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
                   ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Encoder)
                   ?? unbound[0];
        }

        if (evt.IsNote || probe.LooksLikeButton)
        {
            return unbound.FirstOrDefault(c => c.Type == MidiControlType.Button)
                   ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
                   ?? unbound[0];
        }

        if (probe.LooksLikeRelativeTicks || probe.LooksLikeEncoderBurst)
        {
            return unbound.FirstOrDefault(c => c.Type == MidiControlType.Encoder)
                   ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
                   ?? unbound[0];
        }

        // Continuous absolute stream (knobs/faders sending repeating absolute values).
        if (probe.LooksLikeContinuous)
        {
            return unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
                   ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Encoder)
                   ?? unbound[0];
        }

        return unbound.FirstOrDefault(c => c.Type == MidiControlType.Fader)
               ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Encoder)
               ?? unbound.FirstOrDefault(c => c.Type == MidiControlType.Button)
               ?? unbound[0];
    }

    private void ObserveCc(MidiIncomingEvent evt)
    {
        if (evt.IsNote || evt.IsPitchBend)
        {
            return;
        }

        var probe = GetProbe(evt);
        var now = DateTime.UtcNow;
        if (probe.Events > 0 && (now - probe.LastEventUtc).TotalMilliseconds <= 40)
        {
            probe.RapidBursts++;
        }

        probe.LastEventUtc = now;
        probe.Events++;
        if (evt.RawValue is 1 or 2 or 3)
        {
            probe.SeenRelativePlus = true;
        }

        if (evt.RawValue is >= 65 and <= 67)
        {
            probe.SeenRelativeMinus = true;
        }

        if (evt.RawValue is > 0 and < 127)
        {
            probe.SeenIntermediate = true;
            if (evt.RawValue is not (1 or 2 or 3 or >= 65 and <= 67))
            {
                probe.SeenBroadAbsolute = true;
            }
        }

        if (evt.RawValue == 0)
        {
            probe.SeenZero = true;
        }

        if (evt.RawValue == 127)
        {
            probe.SeenMax = true;
        }
    }

    private CcProbe GetProbe(MidiIncomingEvent evt)
    {
        var kind = evt.IsPitchBend ? "P" : evt.IsNote ? "N" : "C";
        var key = $"{evt.DeviceName}|{kind}|{evt.Controller}";
        if (!_ccProbes.TryGetValue(key, out var probe))
        {
            probe = new CcProbe();
            _ccProbes[key] = probe;
        }

        return probe;
    }

    private sealed class CcProbe
    {
        public int Events { get; set; }
        public int RapidBursts { get; set; }
        public DateTime LastEventUtc { get; set; }
        public bool SeenIntermediate { get; set; }
        public bool SeenBroadAbsolute { get; set; }
        public bool SeenRelativePlus { get; set; }
        public bool SeenRelativeMinus { get; set; }
        public bool SeenZero { get; set; }
        public bool SeenMax { get; set; }

        public bool LooksLikeContinuous => SeenIntermediate;

        public bool LooksLikeEncoderBurst => SeenIntermediate && RapidBursts >= 2;

        /// <summary>DAW Mode knobs: only +ticks (1..3) and/or -ticks (65..67).</summary>
        public bool LooksLikeRelativeTicks =>
            !SeenBroadAbsolute
            && !SeenZero
            && !SeenMax
            && (SeenRelativePlus || SeenRelativeMinus)
            && Events >= 1;

        public bool LooksLikeButton =>
            !SeenIntermediate && SeenZero && SeenMax && Events >= 2;

        public bool IsReadyToClassify =>
            LooksLikeContinuous || LooksLikeButton || LooksLikeRelativeTicks;
    }
}
