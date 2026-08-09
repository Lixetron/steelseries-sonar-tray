using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Listens to multiple MIDI input devices concurrently. Every event is tagged with its source DeviceName.
/// </summary>
public sealed class MidiInputHub : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, InputDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledDeviceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly EventHandler<MidiEventReceivedEventArgs> _eventHandler;
    private readonly System.Threading.Timer _refreshTimer;

    private bool _listening;
    private bool _disposed;

    public event Action<MidiIncomingEvent>? EventReceived;
    public event Action? DevicesChanged;

    public MidiInputHub()
    {
        _eventHandler = OnDeviceEventReceived;
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
            return InputDevice.GetAll()
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

    public IReadOnlyList<string> GetEnabledDeviceNames()
    {
        lock (_sync)
        {
            return _enabledDeviceNames.ToList();
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
                DetachDevice(device);
            }

            _devices.Clear();
        }
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
        var changed = false;

        lock (_sync)
        {
            foreach (var name in _devices.Keys.ToList())
            {
                if (!listening || !wanted.Contains(name) || !available.Contains(name))
                {
                    if (_devices.Remove(name, out var device))
                    {
                        DetachDevice(device);
                        changed = true;
                    }
                }
            }

            if (listening)
            {
                foreach (var name in wanted)
                {
                    if (!available.Contains(name) || _devices.ContainsKey(name))
                    {
                        continue;
                    }

                    try
                    {
                        var device = InputDevice.GetByName(name);
                        device.EventReceived += _eventHandler;
                        device.StartEventsListening();
                        _devices[name] = device;
                        changed = true;
                    }
                    catch
                    {
                        // Device may have disconnected between enumeration and open.
                    }
                }
            }
        }

        if (changed)
        {
            DevicesChanged?.Invoke();
        }
    }

    private void OnDeviceEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        if (sender is not InputDevice device)
        {
            return;
        }

        var deviceName = device.Name;
        switch (e.Event)
        {
            case ControlChangeEvent cc:
                EventReceived?.Invoke(new MidiIncomingEvent(
                    deviceName,
                    (int)cc.ControlNumber,
                    (int)cc.ControlValue,
                    IsNote: false,
                    IsNoteOn: false));
                break;

            case PitchBendEvent pitchBend:
                // E0–E7 → channel 0–7. RawValue keeps full 14-bit pitch for volume mapping.
                EventReceived?.Invoke(new MidiIncomingEvent(
                    deviceName,
                    (int)pitchBend.Channel,
                    (int)pitchBend.PitchValue,
                    IsNote: false,
                    IsNoteOn: false,
                    IsPitchBend: true));
                break;

            case NoteOnEvent noteOn:
                EventReceived?.Invoke(new MidiIncomingEvent(
                    deviceName,
                    (int)noteOn.NoteNumber,
                    (int)noteOn.Velocity,
                    IsNote: true,
                    IsNoteOn: noteOn.Velocity > 0));
                break;

            case NoteOffEvent noteOff:
                EventReceived?.Invoke(new MidiIncomingEvent(
                    deviceName,
                    (int)noteOff.NoteNumber,
                    0,
                    IsNote: true,
                    IsNoteOn: false));
                break;
        }
    }

    private void DetachDevice(InputDevice device)
    {
        try
        {
            device.EventReceived -= _eventHandler;
        }
        catch
        {
            // Ignore unsubscribe issues when disposing.
        }

        try
        {
            if (device.IsListeningForEvents)
            {
                device.StopEventsListening();
            }
        }
        catch
        {
            // Best-effort.
        }

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
