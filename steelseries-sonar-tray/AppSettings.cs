using System.IO;
using System.Text.Json;

namespace SonarQuickMixer;

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lixetron",
        "SonarQuickMixer",
        "settings.json");

    public bool RunAtWindowsStartup { get; set; }
    public bool MediaKeysOverride { get; set; }
    public string MediaKeysOverrideChannel { get; set; } = "master";
    public bool VolumeOverlayEnabled { get; set; } = true;
    public bool DiscordScreenshareEchoFix { get; set; }
    public bool AudioVisualizerEnabled { get; set; } = true;
    public TrayIconStyle TrayIconStyle { get; set; } = TrayIconStyle.Auto;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Settings persistence is best-effort for the MVP.
        }
    }
}
