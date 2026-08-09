using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Midi;

public sealed class MidiMappingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly object _sync = new();
    private MidiMappingsDocument _document = new();

    public MidiMappingStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
        Load();
    }

    public static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lixetron",
            "SonarQuickMixer",
            "midi-mappings.json");

    public IReadOnlyList<MidiBinding> Bindings
    {
        get
        {
            lock (_sync)
            {
                return _document.Bindings.ToList();
            }
        }
    }

    public IReadOnlyList<string> EnabledDevices
    {
        get
        {
            lock (_sync)
            {
                return _document.EnabledDevices.ToList();
            }
        }
    }

    public IReadOnlyList<string> HiddenDevices
    {
        get
        {
            lock (_sync)
            {
                return _document.HiddenDevices.ToList();
            }
        }
    }

    public IReadOnlyList<string> RevealedDevices
    {
        get
        {
            lock (_sync)
            {
                return _document.RevealedDevices.ToList();
            }
        }
    }

    public void SetEnabledDevices(IEnumerable<string> devices)
    {
        lock (_sync)
        {
            _document.EnabledDevices = devices
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        Save();
    }

    public void HideDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        lock (_sync)
        {
            _document.RevealedDevices.RemoveAll(d =>
                string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase));
            if (!_document.HiddenDevices.Any(d =>
                    string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase)))
            {
                _document.HiddenDevices.Add(deviceName);
            }

            _document.EnabledDevices.RemoveAll(d =>
                string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase));
        }

        Save();
    }

    public void RevealDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        lock (_sync)
        {
            _document.HiddenDevices.RemoveAll(d =>
                string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase));
            if (!_document.RevealedDevices.Any(d =>
                    string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase)))
            {
                _document.RevealedDevices.Add(deviceName);
            }
        }

        Save();
    }

    public bool IsEffectivelyHidden(string deviceName, IReadOnlyList<string> availableNames)
    {
        var revealed = RevealedDevices;
        if (revealed.Any(d => string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (HiddenDevices.Any(d => string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return MidiDevicePortNaming.IsAutoDuplicatePort(deviceName, availableNames);
    }

    /// <summary>Disables listening on ports that are effectively hidden.</summary>
    public int DisableHiddenEnabledDevices(IReadOnlyList<string> availableNames)
    {
        lock (_sync)
        {
            var before = _document.EnabledDevices.Count;
            _document.EnabledDevices = _document.EnabledDevices
                .Where(name => !IsEffectivelyHiddenUnlocked(name, availableNames))
                .ToList();
            var removed = before - _document.EnabledDevices.Count;
            if (removed > 0)
            {
                // Save outside lock via fallthrough
            }
            else
            {
                return 0;
            }
        }

        Save();
        return 1;
    }

    private bool IsEffectivelyHiddenUnlocked(string deviceName, IReadOnlyList<string> availableNames)
    {
        if (_document.RevealedDevices.Any(d =>
                string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_document.HiddenDevices.Any(d =>
                string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return MidiDevicePortNaming.IsAutoDuplicatePort(deviceName, availableNames);
    }

    public MidiBinding? FindByController(string deviceName, int controller, bool isNote, bool isPitchBend = false)
    {
        lock (_sync)
        {
            var matches = _document.Bindings
                .Where(b =>
                    b.Controller == controller
                    && b.IsNote == isNote
                    && b.IsPitchBend == isPitchBend
                    && MidiDevicePortNaming.DevicesShareProduct(b.DeviceName, deviceName))
                .ToList();

            return matches.FirstOrDefault(b =>
                       string.Equals(b.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                   ?? matches.FirstOrDefault();
        }
    }

    /// <summary>
    /// Rewrites bindings stored under MIDIIN2 (Product) onto the primary Product port when both exist,
    /// merging Sonar channel assignments so Game/Chat etc. keep working after hiding the duplicate port.
    /// </summary>
    public int MigrateSecondaryPortBindings(IReadOnlyList<string> availableNames)
    {
        var changed = 0;
        lock (_sync)
        {
            var primaries = availableNames
                .Where(n => !MidiDevicePortNaming.IsSecondaryPortName(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < _document.Bindings.Count; i++)
            {
                var binding = _document.Bindings[i];
                if (!MidiDevicePortNaming.IsSecondaryPortName(binding.DeviceName))
                {
                    continue;
                }

                var primary = primaries.FirstOrDefault(p =>
                    MidiDevicePortNaming.DevicesShareProduct(p, binding.DeviceName));
                if (primary is null)
                {
                    continue;
                }

                var duplicateOnPrimary = _document.Bindings.FirstOrDefault(b =>
                    !ReferenceEquals(b, binding)
                    && string.Equals(b.DeviceName, primary, StringComparison.OrdinalIgnoreCase)
                    && b.Controller == binding.Controller
                    && b.IsNote == binding.IsNote
                    && b.IsPitchBend == binding.IsPitchBend);

                if (duplicateOnPrimary is not null)
                {
                    if (!duplicateOnPrimary.HasSonarChannel && binding.HasSonarChannel)
                    {
                        duplicateOnPrimary.ChannelId = binding.ChannelId;
                        duplicateOnPrimary.Path = binding.Path;
                        duplicateOnPrimary.Mode = binding.Mode;
                        duplicateOnPrimary.Action = binding.Action;
                        duplicateOnPrimary.ControlId = binding.ControlId ?? duplicateOnPrimary.ControlId;
                        duplicateOnPrimary.RelativeEncoding = binding.RelativeEncoding;
                        duplicateOnPrimary.RelativeStep = binding.RelativeStep;
                    }
                    else if (string.IsNullOrWhiteSpace(duplicateOnPrimary.ControlId)
                             && !string.IsNullOrWhiteSpace(binding.ControlId))
                    {
                        duplicateOnPrimary.ControlId = binding.ControlId;
                    }

                    _document.Bindings.RemoveAt(i);
                    i--;
                    changed++;
                    continue;
                }

                binding.DeviceName = primary;
                changed++;
            }
        }

        if (changed > 0)
        {
            Save();
        }

        return changed;
    }

    public MidiBinding? FindByControlId(string deviceName, string controlId)
    {
        lock (_sync)
        {
            var matches = _document.Bindings
                .Where(b =>
                    string.Equals(b.ControlId, controlId, StringComparison.OrdinalIgnoreCase)
                    && MidiDevicePortNaming.DevicesShareProduct(b.DeviceName, deviceName))
                .ToList();

            return matches.FirstOrDefault(b =>
                       string.Equals(b.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                   ?? matches.FirstOrDefault();
        }
    }

    /// <summary>
    /// Returns absolute non-motorized bindings that already control the same Sonar channel/path,
    /// excluding an optional binding being replaced.
    /// </summary>
    public IReadOnlyList<MidiBinding> FindConflictingAbsoluteFaders(
        string channelId,
        SonarMixerPath path,
        string? excludeBindingKey = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) || !SonarChannels.IsValidChannel(channelId))
        {
            return [];
        }

        var normalized = SonarChannels.NormalizeChannel(channelId);
        lock (_sync)
        {
            return _document.Bindings
                .Where(b =>
                    b.HasSonarChannel
                    && b.Mode == MidiValueMode.Absolute
                    && !b.IsMotorized
                    && b.Action == MidiBindingAction.Volume
                    && !b.IsNote
                    && b.Path == path
                    && string.Equals(SonarChannels.NormalizeChannel(b.ChannelId), normalized, StringComparison.OrdinalIgnoreCase)
                    && (excludeBindingKey is null
                        || !string.Equals(b.BindingKey, excludeBindingKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    public void Upsert(MidiBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.ChannelId = string.IsNullOrWhiteSpace(binding.ChannelId) || !SonarChannels.IsValidChannel(binding.ChannelId)
            ? MidiBinding.UnassignedChannelId
            : SonarChannels.NormalizeChannel(binding.ChannelId);

        lock (_sync)
        {
            var existing = _document.Bindings.FindIndex(b =>
                string.Equals(b.BindingKey, binding.BindingKey, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(binding.ControlId)
                    && string.Equals(b.DeviceName, binding.DeviceName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(b.ControlId, binding.ControlId, StringComparison.OrdinalIgnoreCase)));

            if (existing >= 0)
            {
                _document.Bindings[existing] = binding;
            }
            else
            {
                _document.Bindings.Add(binding);
            }
        }

        Save();
    }

    public bool Remove(string bindingKey)
    {
        lock (_sync)
        {
            var removed = _document.Bindings.RemoveAll(b =>
                string.Equals(b.BindingKey, bindingKey, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }
        }

        Save();
        return true;
    }

    public int RemoveMatching(Func<MidiBinding, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int removed;
        lock (_sync)
        {
            removed = _document.Bindings.RemoveAll(b => predicate(b));
        }

        if (removed > 0)
        {
            Save();
        }

        return removed;
    }

    public int ClearAllBindings()
    {
        int removed;
        lock (_sync)
        {
            removed = _document.Bindings.Count;
            _document.Bindings.Clear();
        }

        if (removed > 0)
        {
            Save();
        }

        return removed;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                lock (_sync)
                {
                    _document = new MidiMappingsDocument();
                }

                return;
            }

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<MidiMappingsDocument>(json, JsonOptions)
                         ?? new MidiMappingsDocument();

            lock (_sync)
            {
                _document = loaded;
                _document.HiddenDevices ??= [];
                _document.RevealedDevices ??= [];
                _document.EnabledDevices ??= [];
                _document.Bindings ??= [];
                foreach (var binding in _document.Bindings)
                {
                    binding.ChannelId = string.IsNullOrWhiteSpace(binding.ChannelId)
                                        || !SonarChannels.IsValidChannel(binding.ChannelId)
                        ? MidiBinding.UnassignedChannelId
                        : SonarChannels.NormalizeChannel(binding.ChannelId);
                    binding.Action = MidiBindingActions.NormalizeFromHardware(binding.IsNote, binding.Action);
                }
            }
        }
        catch
        {
            lock (_sync)
            {
                _document = new MidiMappingsDocument();
            }
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            MidiMappingsDocument snapshot;
            lock (_sync)
            {
                snapshot = new MidiMappingsDocument
                {
                    Bindings = _document.Bindings.Select(Clone).ToList(),
                    EnabledDevices = _document.EnabledDevices.ToList(),
                    HiddenDevices = _document.HiddenDevices.ToList(),
                    RevealedDevices = _document.RevealedDevices.ToList()
                };
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Persistence is best-effort.
        }
    }

    private static MidiBinding Clone(MidiBinding source) => new()
    {
        DeviceName = source.DeviceName,
        Controller = source.Controller,
        IsNote = source.IsNote,
        IsPitchBend = source.IsPitchBend,
        ChannelId = source.ChannelId,
        Path = source.Path,
        Mode = source.Mode,
        Action = source.Action,
        IsMotorized = source.IsMotorized,
        RelativeEncoding = source.RelativeEncoding,
        RelativeStep = source.RelativeStep,
        ControlId = source.ControlId
    };
}
