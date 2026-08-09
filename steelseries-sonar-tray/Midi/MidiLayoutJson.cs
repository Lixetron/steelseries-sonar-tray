using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Serialize / parse / validate MIDI layout preset JSON for import/export.
/// </summary>
public static class MidiLayoutJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(MidiDeviceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return JsonSerializer.Serialize(layout, JsonOptions);
    }

    /// <summary>
    /// Parses layout JSON. On failure, <paramref name="error"/> includes line/position when available.
    /// </summary>
    public static bool TryParse(string? json, out MidiDeviceLayout? layout, out string error)
    {
        layout = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "JSON is empty.";
            return false;
        }

        try
        {
            // Syntax check first so JsonException can report line/byte.
            using (JsonDocument.Parse(json))
            {
            }
        }
        catch (JsonException ex)
        {
            error = FormatJsonException("Invalid JSON syntax", ex);
            return false;
        }

        MidiDeviceLayout? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MidiDeviceLayout>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = FormatJsonException("JSON does not match the layout schema", ex);
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"JSON does not match the layout schema: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "JSON deserialized to an empty layout.";
            return false;
        }

        parsed.DeviceMatch ??= [];
        parsed.Regions ??= [];
        parsed.Controls ??= [];

        if (!TryValidateSemantics(parsed, out error))
        {
            return false;
        }

        layout = parsed;
        return true;
    }

    public static bool TryValidateSemantics(MidiDeviceLayout layout, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(layout.Name))
        {
            error = "Layout \"name\" is required.";
            return false;
        }

        var regionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < layout.Regions.Count; i++)
        {
            var region = layout.Regions[i];
            if (string.IsNullOrWhiteSpace(region.Id))
            {
                error = $"regions[{i}]: \"id\" is required.";
                return false;
            }

            if (!regionIds.Add(region.Id))
            {
                error = $"Duplicate region id \"{region.Id}\".";
                return false;
            }
        }

        for (var i = 0; i < layout.Regions.Count; i++)
        {
            var region = layout.Regions[i];
            if (string.IsNullOrWhiteSpace(region.ParentRegionId))
            {
                continue;
            }

            if (!regionIds.Contains(region.ParentRegionId))
            {
                error = $"regions[{i}] (\"{region.Id}\"): parentRegionId \"{region.ParentRegionId}\" does not exist.";
                return false;
            }

            if (string.Equals(region.Id, region.ParentRegionId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"regions[{i}] (\"{region.Id}\"): parentRegionId cannot reference itself.";
                return false;
            }
        }

        if (HasRegionCycle(layout.Regions))
        {
            error = "regions contain a parentRegionId cycle.";
            return false;
        }

        var controlIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < layout.Controls.Count; i++)
        {
            var control = layout.Controls[i];
            if (string.IsNullOrWhiteSpace(control.Id))
            {
                error = $"controls[{i}]: \"id\" is required.";
                return false;
            }

            if (!controlIds.Add(control.Id))
            {
                error = $"Duplicate control id \"{control.Id}\".";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(control.RegionId) && !regionIds.Contains(control.RegionId))
            {
                error = $"controls[{i}] (\"{control.Id}\"): regionId \"{control.RegionId}\" does not exist.";
                return false;
            }

            if (control.Controller is < 0 or > 127)
            {
                error = $"controls[{i}] (\"{control.Id}\"): controller must be 0–127 when set.";
                return false;
            }

            if (control.IsPitchBend && control.Controller is > 15)
            {
                error = $"controls[{i}] (\"{control.Id}\"): pitch-bend controller must be MIDI channel 0–15.";
                return false;
            }

            if (control.RowSpan < 1 || control.ColSpan < 1)
            {
                error = $"controls[{i}] (\"{control.Id}\"): rowSpan/colSpan must be ≥ 1.";
                return false;
            }

            if (!TryValidateFeedback(control, i, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateFeedback(MidiLayoutControl control, int index, out string error)
    {
        error = string.Empty;
        var feedback = control.Feedback;
        if (feedback is null || feedback.Source == MidiFeedbackSource.None)
        {
            return true;
        }

        if (feedback.Source is not (MidiFeedbackSource.Mute or MidiFeedbackSource.ChannelAssigned))
        {
            error = $"controls[{index}] (\"{control.Id}\"): unsupported feedback.source.";
            return false;
        }

        if (!TryValidateFeedbackMessage(feedback.On, control.Id, index, "on", out error))
        {
            return false;
        }

        if (!TryValidateFeedbackMessage(feedback.Off, control.Id, index, "off", out error))
        {
            return false;
        }

        // Defaults derive from input identity — require a factory controller when on/off omitted.
        if ((feedback.On is null || feedback.Off is null) && control.Controller is null)
        {
            error =
                $"controls[{index}] (\"{control.Id}\"): feedback with omitted on/off requires controller so defaults can be derived.";
            return false;
        }

        return true;
    }

    private static bool TryValidateFeedbackMessage(
        MidiFeedbackMessage? message,
        string controlId,
        int index,
        string which,
        out string error)
    {
        error = string.Empty;
        if (message is null)
        {
            return true;
        }

        if (message.Controller is < 0 or > 127)
        {
            error = $"controls[{index}] (\"{controlId}\"): feedback.{which}.controller must be 0–127.";
            return false;
        }

        if (message.Value is < 0 or > 127)
        {
            error = $"controls[{index}] (\"{controlId}\"): feedback.{which}.value must be 0–127.";
            return false;
        }

        if (message.Channel is < 1 or > 16)
        {
            error = $"controls[{index}] (\"{controlId}\"): feedback.{which}.channel must be 1–16.";
            return false;
        }

        return true;
    }

    private static bool HasRegionCycle(IReadOnlyList<MidiLayoutRegion> regions)
    {
        var parent = regions.ToDictionary(
            r => r.Id,
            r => r.ParentRegionId,
            StringComparer.OrdinalIgnoreCase);

        foreach (var id in parent.Keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = id;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (!seen.Add(current))
                {
                    return true;
                }

                if (!parent.TryGetValue(current, out var next) || string.IsNullOrWhiteSpace(next))
                {
                    break;
                }

                current = next!;
            }
        }

        return false;
    }

    private static string FormatJsonException(string prefix, JsonException ex)
    {
        // LineNumber / BytePositionInLine are 0-based in System.Text.Json.
        if (ex.LineNumber is long line && ex.BytePositionInLine is long column)
        {
            return $"{prefix} at line {line + 1}, position {column + 1}: {ex.Message}";
        }

        if (ex.LineNumber is long lineOnly)
        {
            return $"{prefix} at line {lineOnly + 1}: {ex.Message}";
        }

        return $"{prefix}: {ex.Message}";
    }
}
