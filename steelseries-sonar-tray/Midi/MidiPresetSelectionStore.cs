using System.IO;
using System.Text.Json;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Remembers which layout preset is active per MIDI product (official vs a user JSON file).
/// </summary>
public sealed class MidiPresetSelectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public const string OfficialKey = "official";

    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string, string> _activeByDeviceKey = new(StringComparer.OrdinalIgnoreCase);

    public MidiPresetSelectionStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
        Load();
    }

    public static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lixetron",
            "SonarQuickMixer",
            "midi-preset-selection.json");

    public static string UserKey(string fileName) =>
        "user:" + Path.GetFileName(fileName);

    public static bool IsUserKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.StartsWith("user:", StringComparison.OrdinalIgnoreCase);

    public static string? UserFileNameFromKey(string key) =>
        IsUserKey(key) ? key["user:".Length..] : null;

    public string? GetActiveKey(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return null;
        }

        lock (_sync)
        {
            return _activeByDeviceKey.TryGetValue(deviceKey, out var key) ? key : null;
        }
    }

    public void SetActiveKey(string deviceKey, string presetKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) || string.IsNullOrWhiteSpace(presetKey))
        {
            return;
        }

        lock (_sync)
        {
            _activeByDeviceKey[deviceKey] = presetKey;
        }

        Save();
    }

    public void ClearActiveKey(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return;
        }

        lock (_sync)
        {
            _activeByDeviceKey.Remove(deviceKey);
        }

        Save();
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
            var doc = JsonSerializer.Deserialize<Document>(json, JsonOptions);
            lock (_sync)
            {
                _activeByDeviceKey = doc?.ActiveByDeviceKey is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(doc.ActiveByDeviceKey, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            lock (_sync)
            {
                _activeByDeviceKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Document snapshot;
            lock (_sync)
            {
                snapshot = new Document
                {
                    ActiveByDeviceKey = new Dictionary<string, string>(_activeByDeviceKey, StringComparer.OrdinalIgnoreCase)
                };
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch
        {
            // Best-effort.
        }
    }

    private sealed class Document
    {
        public Dictionary<string, string> ActiveByDeviceKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>One selectable entry in the layout preset list.</summary>
public sealed class MidiPresetOption
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public bool IsOfficial { get; init; }
    public string? FileName { get; init; }

    public bool IsUser => !IsOfficial;
}
