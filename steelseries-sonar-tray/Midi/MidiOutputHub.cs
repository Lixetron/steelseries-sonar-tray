using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Opens MIDI output ports for enabled devices and sends config-driven feedback messages (LEDs, etc.).
/// </summary>
public sealed class MidiOutputHub : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, OutputDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledDeviceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _refreshTimer;

    private bool _listening;
    private bool _disposed;

    public MidiOutputHub()
    {
        _refreshTimer = new System.Threading.Timer(
            _ => RefreshDevicesSafe(),
            null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3));
    }

    public IReadOnlyList<string> GetAvailableDeviceNames()
    {
        try
        {
            return OutputDevice.GetAll()
                .Select(d => d.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void SetEnabledDevices(IEnumerable<string> deviceNames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            _enabledDeviceNames.Clear();
            foreach (var name in deviceNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                _enabledDeviceNames.Add(name);
            }
        }

        RefreshDevicesSafe();
    }

    public void SetListening(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            _listening = enabled;
        }

        RefreshDevicesSafe();
    }

    /// <summary>Sends a feedback message to the best matching output port for <paramref name="deviceName"/>.</summary>
    public bool TrySend(string deviceName, MidiFeedbackMessage message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(deviceName) || message is null)
        {
            return false;
        }

        MidiEvent midiEvent = message.Kind switch
        {
            MidiFeedbackKind.Cc => new ControlChangeEvent(
                (SevenBitNumber)Math.Clamp(message.Controller, 0, 127),
                (SevenBitNumber)Math.Clamp(message.Value, 0, 127))
            {
                Channel = ToMidiChannel(message.Channel)
            },
            MidiFeedbackKind.PitchBend => CreatePitchBendEvent(message),
            _ => new NoteOnEvent(
                (SevenBitNumber)Math.Clamp(message.Controller, 0, 127),
                (SevenBitNumber)Math.Clamp(message.Value, 0, 127))
            {
                Channel = ToMidiChannel(message.Channel)
            }
        };

        lock (_sync)
        {
            if (!_listening)
            {
                return false;
            }

            var device = ResolveOutputDevice(deviceName);
            if (device is null)
            {
                return false;
            }

            try
            {
                device.SendEvent(midiEvent);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Dispose();

        lock (_sync)
        {
            _listening = false;
            foreach (var device in _devices.Values)
            {
                try
                {
                    device.Dispose();
                }
                catch
                {
                    // Best-effort.
                }
            }

            _devices.Clear();
        }
    }

    private static FourBitNumber ToMidiChannel(int channel1To16)
    {
        var zeroBased = Math.Clamp(channel1To16, 1, 16) - 1;
        return (FourBitNumber)zeroBased;
    }

    /// <summary>MSB in <see cref="MidiFeedbackMessage.Value"/>, LSB 0 (MCU fader wire style).</summary>
    private static PitchBendEvent CreatePitchBendEvent(MidiFeedbackMessage message)
    {
        var msb = Math.Clamp(message.Value, 0, 127);
        var pitch14 = (ushort)(msb << 7);
        return new PitchBendEvent(pitch14)
        {
            Channel = ToMidiChannel(message.Channel)
        };
    }

    private OutputDevice? ResolveOutputDevice(string deviceName)
    {
        if (_devices.TryGetValue(deviceName, out var exact))
        {
            return exact;
        }

        foreach (var (name, device) in _devices)
        {
            if (MidiDevicePortNaming.DevicesShareProduct(name, deviceName))
            {
                return device;
            }
        }

        return null;
    }

    private void RefreshDevicesSafe()
    {
        try
        {
            RefreshDevices();
        }
        catch
        {
            // Hot-plug refresh is best-effort.
        }
    }

    private void RefreshDevices()
    {
        if (_disposed)
        {
            return;
        }

        bool listening;
        HashSet<string> wanted;
        lock (_sync)
        {
            listening = _listening;
            wanted = new HashSet<string>(_enabledDeviceNames, StringComparer.OrdinalIgnoreCase);
        }

        var available = GetAvailableDeviceNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            foreach (var name in _devices.Keys.ToList())
            {
                if (!listening || !wanted.Contains(name) || !available.Contains(name))
                {
                    if (_devices.Remove(name, out var device))
                    {
                        try
                        {
                            device.Dispose();
                        }
                        catch
                        {
                            // Best-effort.
                        }
                    }
                }
            }

            if (!listening)
            {
                return;
            }

            foreach (var name in wanted)
            {
                if (!available.Contains(name) || _devices.ContainsKey(name))
                {
                    continue;
                }

                try
                {
                    var device = OutputDevice.GetByName(name);
                    _devices[name] = device;
                }
                catch
                {
                    // Some hosts expose input-only names; try product match among available outs.
                    var match = available.FirstOrDefault(a =>
                        MidiDevicePortNaming.DevicesShareProduct(a, name));
                    if (match is null || _devices.ContainsKey(match))
                    {
                        continue;
                    }

                    try
                    {
                        _devices[match] = OutputDevice.GetByName(match);
                    }
                    catch
                    {
                        // Output port unavailable.
                    }
                }
            }
        }
    }
}
