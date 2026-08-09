using System.IO;
using System.Text.Json;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Persists last absolute fader/knob positions (normalized 0..1) keyed by <see cref="MidiBinding.BindingKey"/>.
/// Separate from routing mappings so state does not pollute midi-mappings.json.
/// </summary>
public sealed class MidiControlStateStore : IDisposable
{
    private const int SaveDebounceMs = 450;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly object _sync = new();
    private readonly Dictionary<string, float> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _saveTimer;
    private bool _disposed;
    private bool _dirty;

    public MidiControlStateStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
        _saveTimer = new System.Threading.Timer(
            _ => FlushIfDirty(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        Load();
    }

    public static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lixetron",
            "SonarQuickMixer",
            "midi-control-state.json");

    /// <summary>
    /// Absolute Volume bindings with a Sonar channel (not notes) participate in persist/restore.
    /// </summary>
    public static bool IsPersistableAbsoluteVolume(MidiBinding binding) =>
        binding.Mode == MidiValueMode.Absolute
        && binding.Action == MidiBindingAction.Volume
        && binding.HasSonarChannel
        && !binding.IsNote;

    public bool TryGet(string bindingKey, out float volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKey);
        lock (_sync)
        {
            if (_volumes.TryGetValue(bindingKey, out volume))
            {
                return true;
            }
        }

        volume = 0f;
        return false;
    }

    public void Set(string bindingKey, float volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKey);
        var clamped = Math.Clamp(volume, 0f, 1f);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _volumes[bindingKey] = clamped;
            _dirty = true;
            ScheduleSave_NoLock();
        }
    }

    public void SetFromBinding(MidiBinding binding, float volume)
    {
        if (!IsPersistableAbsoluteVolume(binding))
        {
            return;
        }

        Set(binding.BindingKey, volume);
    }

    public void Remove(string bindingKey)
    {
        if (string.IsNullOrWhiteSpace(bindingKey))
        {
            return;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_volumes.Remove(bindingKey))
            {
                _dirty = true;
                ScheduleSave_NoLock();
            }
        }
    }

    /// <summary>Drops keys that no longer match any of the given binding keys.</summary>
    public void PruneTo(IEnumerable<string> keepBindingKeys)
    {
        var keep = new HashSet<string>(
            keepBindingKeys.Where(k => !string.IsNullOrWhiteSpace(k)),
            StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var orphans = _volumes.Keys.Where(k => !keep.Contains(k)).ToList();
            if (orphans.Count == 0)
            {
                return;
            }

            foreach (var key in orphans)
            {
                _volumes.Remove(key);
            }

            _dirty = true;
            ScheduleSave_NoLock();
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        FlushIfDirty();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        FlushIfDirty();
        _saveTimer.Dispose();
    }

    private void ScheduleSave_NoLock() =>
        _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);

    private void FlushIfDirty()
    {
        Dictionary<string, float> snapshot;
        lock (_sync)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            snapshot = new Dictionary<string, float>(_volumes, StringComparer.OrdinalIgnoreCase);
        }

        SaveSnapshot(snapshot);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<MidiControlStateDocument>(json, JsonOptions);
            if (loaded?.Volumes is null)
            {
                return;
            }

            lock (_sync)
            {
                _volumes.Clear();
                foreach (var (key, value) in loaded.Volumes)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    _volumes[key] = Math.Clamp(value, 0f, 1f);
                }
            }
        }
        catch
        {
            lock (_sync)
            {
                _volumes.Clear();
            }
        }
    }

    private void SaveSnapshot(Dictionary<string, float> volumes)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new MidiControlStateDocument { Volumes = volumes };
            var json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Persistence is best-effort.
        }
    }

    private sealed class MidiControlStateDocument
    {
        public Dictionary<string, float> Volumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
