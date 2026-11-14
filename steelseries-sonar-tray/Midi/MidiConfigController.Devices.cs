using SonarQuickMixer.Localization;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Device list refresh, enable/disable (Use device), hide/reveal, and factory-binding seeding
/// for newly-enabled devices. Split out of MidiConfigController.cs purely to keep that file a
/// manageable size — same partial class, same fields/state, no behavior change.
/// </summary>
public partial class MidiConfigController
{
    public void RefreshDevices()
    {
        var available = _midi.Hub.GetAvailableDeviceNames().ToList();
        var migrated = _midi.MappingStore.MigrateSecondaryPortBindings(available);
        if (_midi.MappingStore.DisableHiddenEnabledDevices(available) > 0 || migrated > 0)
        {
            _midi.ApplyEnabledDevicesFromStore();
        }

        var enabled = _midi.MappingStore.EnabledDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _dispatcher.Invoke(() =>
        {
            Devices.Clear();
            foreach (var name in available)
            {
                var hidden = _midi.MappingStore.IsEffectivelyHidden(name, available);
                if (hidden && !_showHiddenDevices)
                {
                    continue;
                }

                Devices.Add(new MidiDeviceListItemVm
                {
                    Name = name,
                    IsEnabled = enabled.Contains(name),
                    IsHidden = hidden
                });
            }

            var visibleNames = Devices.Select(d => d.Name).ToList();
            if (SelectedDeviceName is null
                || !visibleNames.Contains(SelectedDeviceName, StringComparer.OrdinalIgnoreCase))
            {
                var preferredSource = enabled.Count > 0
                    ? enabled.Where(n => visibleNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                    : visibleNames;
                SelectedDeviceName = MidiDevicePortNaming.PreferPrimaryDeviceName(preferredSource)
                                    ?? visibleNames.FirstOrDefault();
            }
            else
            {
                OnPropertyChanged(nameof(IsSelectedDeviceInUse));
                OnPropertyChanged(nameof(UseDeviceButtonText));
                OnPropertyChanged(nameof(HasSelectedDevice));
            }
        });
    }

    /// <summary>Enable or disable the currently previewed device for MIDI input.</summary>
    public void ToggleSelectedDeviceUse()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            SetStatus("midi.status.selectMidiDeviceFirst");
            return;
        }

        if (IsSelectedDeviceInUse)
        {
            StopUsingSelectedDevice();
        }
        else
        {
            UseSelectedDevice();
        }
    }

    public void UseSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            SetStatus("midi.status.selectMidiDeviceFirst");
            return;
        }

        var available = _midi.Hub.GetAvailableDeviceNames().ToList();
        if (_midi.MappingStore.IsEffectivelyHidden(SelectedDeviceName, available))
        {
            // Using a hidden/duplicate port: keep it visible after Use.
            _midi.MappingStore.RevealDevice(SelectedDeviceName);
        }

        var device = Devices.FirstOrDefault(d =>
            string.Equals(d.Name, SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            RefreshDevices();
            device = Devices.FirstOrDefault(d =>
                string.Equals(d.Name, SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        }

        if (device is null)
        {
            SetStatus("midi.status.deviceUnavailable");
            return;
        }

        device.IsEnabled = true;
        device.IsHidden = false;
        PersistEnabledDevices();
        SetStatus(Loc.Text("midi.status.usingDevice", SelectedDeviceName));
    }

    public void StopUsingSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            return;
        }

        CancelLearn();
        var device = Devices.FirstOrDefault(d =>
            string.Equals(d.Name, SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (device is not null)
        {
            device.IsEnabled = false;
        }

        PersistEnabledDevices();
        SetStatus(Loc.Text("midi.status.previewOnly", SelectedDeviceName));
    }

    public void HideSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            SetStatus("midi.status.selectDeviceToHide");
            return;
        }

        var name = SelectedDeviceName;
        CancelLearn();
        _midi.MappingStore.HideDevice(name);
        _midi.ApplyEnabledDevicesFromStore();
        SetStatus(Loc.Text("midi.status.hiddenDevice", name));
        RefreshDevices();
    }

    public void RevealSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            SetStatus("midi.status.selectDeviceToShow");
            return;
        }

        var name = SelectedDeviceName;
        _midi.MappingStore.RevealDevice(name);
        SetStatus(Loc.Text("midi.status.showingDevice", name));
        if (!_showHiddenDevices)
        {
            // Ensure it appears even when the filter is off (user explicitly revealed it).
            RefreshDevices();
        }
        else
        {
            RefreshDevices();
        }
    }

    public void PersistEnabledDevices()
    {
        var available = _midi.Hub.GetAvailableDeviceNames().ToList();
        var enabled = Devices
            .Where(d => d.IsEnabled)
            .Select(d => d.Name)
            .Where(name => !_midi.MappingStore.IsEffectivelyHidden(name, available))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Keep an explicitly revealed+used port even if the list item still shows Hidden briefly.
        var selectedInUse = Devices.FirstOrDefault(d =>
            d.IsEnabled
            && string.Equals(d.Name, SelectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (selectedInUse is not null
            && _midi.MappingStore.RevealedDevices.Any(d =>
                string.Equals(d, selectedInUse.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (!enabled.Contains(selectedInUse.Name, StringComparer.OrdinalIgnoreCase))
            {
                enabled.Add(selectedInUse.Name);
            }
        }

        _midi.MappingStore.SetEnabledDevices(enabled);
        _midi.ApplyEnabledDevicesFromStore();

        OnPropertyChanged(nameof(IsSelectedDeviceInUse));
        OnPropertyChanged(nameof(UseDeviceButtonText));
        OnPropertyChanged(nameof(CanLearnHardware));
        OnPropertyChanged(nameof(IsBlueprintInteractive));
        OnPropertyChanged(nameof(CanEditBindings));

        if (enabled.Count == 0)
        {
            ReloadLayoutForSelectedDevice();
            return;
        }

        var seeded = SeedFactoryBindingsForDevices(enabled);
        ReloadLayoutForSelectedDevice();
        if (seeded > 0 && IsSelectedDeviceInUse)
        {
            SetStatus(Loc.Text("midi.status.listeningSeeded", enabled.Count, seeded));
        }
    }

    /// <summary>
    /// Seeds missing factory hardware from the official preset without overwriting Learn/routes.
    /// </summary>
    public int SeedFactoryBindingsForDevices(IEnumerable<string> deviceNames)
    {
        var seeded = 0;
        foreach (var deviceName in deviceNames
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var layout = _presets.Resolve(deviceName);
            foreach (var factory in PresetCatalog.BuildFactoryBindings(layout, deviceName))
            {
                var controlId = factory.ControlId ?? string.Empty;
                var existingByControl = _midi.MappingStore.FindByControlId(deviceName, controlId)
                    ?? _midi.MappingStore.Bindings.FirstOrDefault(b =>
                        string.Equals(b.ControlId, controlId, StringComparison.OrdinalIgnoreCase)
                        && DevicesShareProduct(b.DeviceName, deviceName));

                if (existingByControl is not null)
                {
                    continue;
                }

                var existingHw = _midi.MappingStore.FindByController(
                    deviceName,
                    factory.Controller,
                    factory.IsNote,
                    factory.IsPitchBend)
                    ?? _midi.MappingStore.Bindings.FirstOrDefault(b =>
                        b.Controller == factory.Controller
                        && b.IsNote == factory.IsNote
                        && b.IsPitchBend == factory.IsPitchBend
                        && DevicesShareProduct(b.DeviceName, deviceName));

                if (existingHw is not null)
                {
                    continue;
                }

                _midi.MappingStore.Upsert(factory);
                seeded++;
            }
        }

        return seeded;
    }
}
