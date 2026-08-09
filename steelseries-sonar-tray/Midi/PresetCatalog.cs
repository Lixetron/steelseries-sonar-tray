using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Midi;

public sealed class PresetCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _officialDirectory;
    private readonly string _userDirectory;
    private readonly MidiPresetSelectionStore _selectionStore;

    public PresetCatalog(
        string? officialDirectory = null,
        string? userDirectory = null,
        MidiPresetSelectionStore? selectionStore = null)
    {
        _officialDirectory = officialDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "Presets");
        _userDirectory = userDirectory ?? GetDefaultUserPresetsDirectory();
        _selectionStore = selectionStore
            ?? new MidiPresetSelectionStore(Path.Combine(_userDirectory, "midi-preset-selection.json"));
        EnsureUserDirectory();
    }

    public static string GetDefaultUserPresetsDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lixetron",
            "SonarQuickMixer",
            "UserPresets");

    public string OfficialDirectory => _officialDirectory;

    public string UserDirectory => _userDirectory;

    public MidiPresetSelectionStore SelectionStore => _selectionStore;

    /// <summary>
    /// Resolves the active layout for a device (selection store → official / named user file).
    /// </summary>
    public MidiDeviceLayout Resolve(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return CreateGenericLayout();
        }

        var deviceKey = MidiDevicePortNaming.CoreProductName(deviceName);
        var activeKey = _selectionStore.GetActiveKey(deviceKey);
        return Resolve(deviceName, activeKey);
    }

    /// <summary>Resolves a specific preset key (<see cref="MidiPresetSelectionStore.OfficialKey"/> or user:file.json).</summary>
    public MidiDeviceLayout Resolve(string? deviceName, string? presetKey)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return CreateGenericLayout();
        }

        if (MidiPresetSelectionStore.IsUserKey(presetKey))
        {
            var fileName = MidiPresetSelectionStore.UserFileNameFromKey(presetKey!);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var path = Path.Combine(_userDirectory, fileName);
                var user = TryLoad(path);
                if (user is not null && LayoutMatchesDevice(user, deviceName))
                {
                    return CloneLayout(user);
                }
            }
        }

        // No selection / official / missing user file:
        // Prefer official; if none and user files exist without an explicit official choice, use first user (legacy).
        var official = FindMatchingLayout(_officialDirectory, deviceName);
        if (official is not null)
        {
            if (string.Equals(presetKey, MidiPresetSelectionStore.OfficialKey, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(presetKey))
            {
                // Legacy: when nothing selected yet but user overrides exist, keep old "user wins" behaviour.
                if (string.IsNullOrWhiteSpace(presetKey))
                {
                    var firstUser = FindMatchingLayout(_userDirectory, deviceName);
                    if (firstUser is not null)
                    {
                        return CloneLayout(firstUser);
                    }
                }

                return CloneLayout(official);
            }
        }

        if (string.IsNullOrWhiteSpace(presetKey)
            || string.Equals(presetKey, MidiPresetSelectionStore.OfficialKey, StringComparison.OrdinalIgnoreCase))
        {
            var firstUser = FindMatchingLayout(_userDirectory, deviceName);
            if (firstUser is not null)
            {
                return CloneLayout(firstUser);
            }

            return official is not null ? CloneLayout(official) : CreateGenericLayout();
        }

        return official is not null ? CloneLayout(official) : CreateGenericLayout();
    }

    public IReadOnlyList<MidiPresetOption> ListPresetsForDevice(string? deviceName)
    {
        var list = new List<MidiPresetOption>();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return list;
        }

        var official = FindMatchingLayout(_officialDirectory, deviceName);
        list.Add(new MidiPresetOption
        {
            Key = MidiPresetSelectionStore.OfficialKey,
            DisplayName = official is null
                ? "Built-in generic grid"
                : $"{official.Name} (official)",
            IsOfficial = true
        });

        if (!Directory.Exists(_userDirectory))
        {
            return list;
        }

        foreach (var file in Directory.EnumerateFiles(_userDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (IsReservedUserFile(file))
            {
                continue;
            }

            var layout = TryLoad(file);
            if (layout is null || !LayoutMatchesDevice(layout, deviceName))
            {
                continue;
            }

            var fileName = Path.GetFileName(file);
            var label = string.IsNullOrWhiteSpace(layout.Name) ? fileName : layout.Name;
            list.Add(new MidiPresetOption
            {
                Key = MidiPresetSelectionStore.UserKey(fileName),
                DisplayName = $"{label} — {fileName}",
                IsOfficial = false,
                FileName = fileName
            });
        }

        return list;
    }

    public string GetActivePresetKey(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return MidiPresetSelectionStore.OfficialKey;
        }

        var deviceKey = MidiDevicePortNaming.CoreProductName(deviceName);
        var stored = _selectionStore.GetActiveKey(deviceKey);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            // Drop stale user keys.
            if (MidiPresetSelectionStore.IsUserKey(stored))
            {
                var file = MidiPresetSelectionStore.UserFileNameFromKey(stored);
                if (file is null || !File.Exists(Path.Combine(_userDirectory, file)))
                {
                    return MidiPresetSelectionStore.OfficialKey;
                }
            }

            return stored;
        }

        // Legacy default: first user override if any.
        var firstUser = ListPresetsForDevice(deviceName).FirstOrDefault(p => p.IsUser);
        return firstUser?.Key ?? MidiPresetSelectionStore.OfficialKey;
    }

    public void SetActivePresetKey(string? deviceName, string presetKey)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(presetKey))
        {
            return;
        }

        _selectionStore.SetActiveKey(MidiDevicePortNaming.CoreProductName(deviceName), presetKey);
    }

    public MidiDeviceLayout? FindOfficialLayout(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        var layout = FindMatchingLayout(_officialDirectory, deviceName);
        return layout is null ? null : CloneLayout(layout);
    }

    public bool HasUserOverride(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        return ListPresetsForDevice(deviceName).Any(p => p.IsUser);
    }

    public IReadOnlyList<MidiDeviceLayout> ListAll()
    {
        var layouts = new List<MidiDeviceLayout>();
        layouts.AddRange(LoadAllFromDirectory(_userDirectory).Select(CloneLayout));
        layouts.AddRange(LoadAllFromDirectory(_officialDirectory).Select(CloneLayout));
        return layouts;
    }

    /// <summary>
    /// Writes a user preset JSON.
    /// When <paramref name="targetFileName"/> is set, overwrites that file.
    /// When <paramref name="createNewFile"/> is true (or no target), creates a unique new file.
    /// </summary>
    public string SaveUserLayout(
        MidiDeviceLayout layout,
        string? targetFileName = null,
        bool createNewFile = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        EnsureUserDirectory();

        string path;
        if (!createNewFile && !string.IsNullOrWhiteSpace(targetFileName))
        {
            path = Path.Combine(_userDirectory, Path.GetFileName(targetFileName));
        }
        else
        {
            path = Path.Combine(_userDirectory, MakeUniqueFileName(layout));
        }

        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            path += ".json";
        }

        if (!Path.GetDirectoryName(path)?.Equals(_userDirectory, StringComparison.OrdinalIgnoreCase) ?? true)
        {
            path = Path.Combine(_userDirectory, Path.GetFileName(path));
        }

        var json = JsonSerializer.Serialize(layout, JsonOptions);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Deletes a single user preset file by name.</summary>
    public bool DeleteUserPresetFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var path = Path.Combine(_userDirectory, Path.GetFileName(fileName));
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes all user override JSON(s) that match the device; returns deleted count.</summary>
    public int DeleteUserLayoutOverride(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return 0;
        }

        EnsureUserDirectory();
        if (!Directory.Exists(_userDirectory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_userDirectory, "*.json", SearchOption.TopDirectoryOnly).ToList())
        {
            if (IsReservedUserFile(file))
            {
                continue;
            }

            var layout = TryLoad(file);
            if (layout is null || !LayoutMatchesDevice(layout, deviceName))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch
            {
                // Best-effort.
            }
        }

        return deleted;
    }

    private static bool IsReservedUserFile(string path) =>
        string.Equals(Path.GetFileName(path), "midi-preset-selection.json", StringComparison.OrdinalIgnoreCase);

    public static MidiDeviceLayout CreateGenericLayout(int columns = 4, int rows = 3)
    {
        var controls = new List<MidiLayoutControl>();
        for (var col = 0; col < columns; col++)
        {
            controls.Add(new MidiLayoutControl
            {
                Id = $"f{col + 1}",
                Row = 0,
                Col = col,
                Type = MidiControlType.Fader,
                Label = $"CH{col + 1}",
                DefaultMode = MidiValueMode.Absolute
            });
            controls.Add(new MidiLayoutControl
            {
                Id = $"e{col + 1}",
                Row = 1,
                Col = col,
                Type = MidiControlType.Encoder,
                Label = $"ENC{col + 1}",
                DefaultMode = MidiValueMode.Relative
            });
            controls.Add(new MidiLayoutControl
            {
                Id = $"b{col + 1}",
                Row = 2,
                Col = col,
                Type = MidiControlType.Button,
                Label = "MUTE"
            });
        }

        return new MidiDeviceLayout
        {
            Name = "Generic Custom Grid",
            DeviceMatch = [],
            Columns = columns,
            Rows = rows,
            Controls = controls
        };
    }

    public static MidiDeviceLayout CloneLayout(MidiDeviceLayout source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new MidiDeviceLayout
        {
            Name = source.Name,
            DeviceMatch = source.DeviceMatch.ToList(),
            Hint = source.Hint,
            Columns = source.Columns,
            Rows = source.Rows,
            Regions = source.Regions.Select(CloneRegion).ToList(),
            Controls = source.Controls.Select(CloneControl).ToList()
        };
    }

    public static MidiLayoutRegion CloneRegion(MidiLayoutRegion source) => new()
    {
        Id = source.Id,
        ParentRegionId = source.ParentRegionId,
        Label = source.Label,
        Row = source.Row,
        Col = source.Col,
        RowSpan = Math.Max(1, source.RowSpan),
        ColSpan = Math.Max(1, source.ColSpan),
        HideBorder = source.HideBorder,
        KeepSpacing = source.KeepSpacing,
        ContentJustify = source.ContentJustify,
        ContentAlign = source.ContentAlign
    };

    public static MidiLayoutControl CloneControl(MidiLayoutControl source) => new()
    {
        Id = source.Id,
        RegionId = source.RegionId,
        Row = source.Row,
        Col = source.Col,
        RowSpan = Math.Max(1, source.RowSpan),
        ColSpan = Math.Max(1, source.ColSpan),
        Type = source.Type,
        Label = source.Label,
        DefaultMode = source.DefaultMode,
        Controller = source.Controller,
        IsNote = source.IsNote,
        IsPitchBend = source.IsPitchBend,
        RelativeEncoding = source.RelativeEncoding,
        DefaultAction = source.DefaultAction,
        Feedback = MidiFeedbackResolver.Clone(source.Feedback)
    };

    /// <summary>
    /// Builds unassigned Sonar bindings from baked preset hardware (e.g. SMC-Mixer DAW Mode / Mode A map).
    /// Skips controls without <see cref="MidiLayoutControl.Controller"/>.
    /// </summary>
    public static IReadOnlyList<MidiBinding> BuildFactoryBindings(MidiDeviceLayout layout, string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return [];
        }

        var list = new List<MidiBinding>();
        foreach (var control in layout.Controls)
        {
            if (!control.HasFactoryHardware || control.Controller is not int controller)
            {
                continue;
            }

            list.Add(CreateFactoryBinding(control, deviceName, controller));
        }

        return list;
    }

    public static MidiBinding CreateFactoryBinding(MidiLayoutControl control, string deviceName, int? controllerOverride = null)
    {
        var controller = controllerOverride ?? control.Controller
            ?? throw new ArgumentException("Factory control requires Controller.", nameof(control));

        var mode = control.DefaultMode
                   ?? (control.Type == MidiControlType.Encoder ? MidiValueMode.Relative : MidiValueMode.Absolute);
        if (control.IsPitchBend)
        {
            mode = MidiValueMode.Absolute;
        }

        var action = MidiBindingActions.Normalize(
            control.Type,
            control.DefaultAction ?? MidiBindingActions.DefaultFor(control.Type));

        return new MidiBinding
        {
            DeviceName = deviceName,
            Controller = controller,
            IsNote = control.IsNote,
            IsPitchBend = control.IsPitchBend,
            ChannelId = MidiBinding.UnassignedChannelId,
            Path = SonarMixerPath.Monitoring,
            Mode = mode,
            Action = action,
            ControlId = control.Id,
            IsMotorized = false,
            RelativeEncoding = control.RelativeEncoding ?? MidiRelativeEncoding.OffsetBinary
        };
    }

    private MidiDeviceLayout? FindMatchingLayout(string directory, string deviceName)
    {
        foreach (var layout in LoadAllFromDirectory(directory))
        {
            if (LayoutMatchesDevice(layout, deviceName))
            {
                return layout;
            }
        }

        return null;
    }

    private string? FindMatchingLayoutFile(string directory, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (IsReservedUserFile(file))
            {
                continue;
            }

            var layout = TryLoad(file);
            if (layout is not null && LayoutMatchesDevice(layout, deviceName))
            {
                return file;
            }
        }

        return null;
    }

    private static bool LayoutMatchesDevice(MidiDeviceLayout layout, string deviceName)
    {
        if (layout.DeviceMatch.Count > 0
            && layout.DeviceMatch.Any(match =>
                deviceName.Contains(match, StringComparison.OrdinalIgnoreCase)
                || match.Contains(deviceName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return deviceName.Contains(layout.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeSafeFileName(MidiDeviceLayout layout)
    {
        var seed = layout.DeviceMatch.FirstOrDefault()
                   ?? layout.Name
                   ?? "custom-layout";
        var safe = Regex.Replace(seed.Trim(), @"[^\w\-]+", "-");
        safe = Regex.Replace(safe, @"-+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "custom-layout";
        }

        return safe.ToLowerInvariant();
    }

    private string MakeUniqueFileName(MidiDeviceLayout layout)
    {
        var baseName = MakeSafeFileName(layout);
        var candidate = baseName + ".json";
        var n = 2;
        while (File.Exists(Path.Combine(_userDirectory, candidate)))
        {
            candidate = $"{baseName}-{n}.json";
            n++;
        }

        return candidate;
    }

    private static IEnumerable<MidiDeviceLayout> LoadAllFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (IsReservedUserFile(file))
            {
                continue;
            }

            var layout = TryLoad(file);
            if (layout is not null)
            {
                yield return layout;
            }
        }
    }

    private static MidiDeviceLayout? TryLoad(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MidiDeviceLayout>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureUserDirectory()
    {
        try
        {
            Directory.CreateDirectory(_userDirectory);
        }
        catch
        {
            // Best-effort.
        }
    }
}
