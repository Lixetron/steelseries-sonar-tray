using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SonarQuickMixer.Localization;

/// <summary>
/// Reads translation packs from embedded resources (shipped languages) and from an optional
/// <c>Languages</c> folder beside the executable (drop-in translations / overrides).
/// </summary>
public static class LanguagePackLoader
{
    public const string ExternalFolderName = "Languages";

    private const string EmbeddedPrefix = "SonarQuickMixer.Localization.Languages.";

    /// <summary>
    /// Loads embedded packs, then external ones. An external file with the same language code
    /// replaces the embedded pack, so translators can iterate without rebuilding the app.
    /// </summary>
    public static IReadOnlyList<LanguagePack> LoadAll(string? externalDirectory = null)
    {
        var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in LoadEmbedded())
        {
            packs[pack.Info.Code] = pack;
        }

        foreach (var pack in LoadExternal(externalDirectory ?? GetDefaultExternalDirectory()))
        {
            packs[pack.Info.Code] = pack;
        }

        return packs.Values
            .OrderBy(p => p.Info.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string GetDefaultExternalDirectory() =>
        Path.Combine(AppContext.BaseDirectory, ExternalFolderName);

    public static IEnumerable<LanguagePack> LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(LanguagePackLoader).Assembly;

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(EmbeddedPrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            var pack = Parse(reader.ReadToEnd(), resourceName);
            if (pack is not null)
            {
                yield return pack;
            }
        }
    }

    public static IEnumerable<LanguagePack> LoadExternal(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            // A locked/inaccessible drop-in folder must not prevent the app from starting.
            yield break;
        }

        foreach (var path in files)
        {
            LanguagePack? pack;
            try
            {
                pack = Parse(File.ReadAllText(path), path);
            }
            catch
            {
                // A malformed drop-in file must not prevent the app from starting.
                continue;
            }

            if (pack is not null)
            {
                yield return pack;
            }
        }
    }

    /// <summary>Parses one JSON pack. Returns null for malformed content instead of throwing.</summary>
    public static LanguagePack? Parse(string json, string source)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var code = ReadString(root, "code");
            var stringsElement = root.TryGetProperty("strings", out var value) ? value : default;
            if (string.IsNullOrWhiteSpace(code) || stringsElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var nativeName = ReadString(root, "nativeName") ?? ReadString(root, "name") ?? code;
            var culture = ReadString(root, "culture") ?? code;

            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in stringsElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    strings[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return new LanguagePack(
                new LanguageInfo(code, nativeName, culture),
                strings,
                source);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim()
            : null;
}
