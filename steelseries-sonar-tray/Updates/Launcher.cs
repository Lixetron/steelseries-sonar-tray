using System.Diagnostics;
using System.IO;

namespace SonarQuickMixer.Updates;

/// <summary>
/// Launcher logic: checks for pending updates and applies them before starting the main app.
/// This runs on every startup to apply any staged updates.
///
/// NOTE: In production, this can be extracted to a separate launcher.exe executable
/// that runs before SonarQuickMixer.exe. For simplicity, we integrate it into App startup.
/// </summary>
public static class Launcher
{
    /// <summary>
    /// Checks if there's a pending update and applies it.
    /// Should be called during App startup, before MainWindow creation.
    /// </summary>
    public static void ApplyPendingUpdateIfExists()
    {
        var appDirectory = AppContext.BaseDirectory;
        var updateMarkerPath = Path.Combine(appDirectory, ".update_pending");

        if (!File.Exists(updateMarkerPath))
        {
            return; // No pending update
        }

        try
        {
            var stagingDirectory = File.ReadAllText(updateMarkerPath).Trim();

            if (!Directory.Exists(stagingDirectory))
            {
                // Staging directory is gone, clean up marker
                File.Delete(updateMarkerPath);
                return;
            }

            // Apply the update: copy staged files to app directory
            ApplyUpdate(stagingDirectory, appDirectory);

            // Clean up
            try
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            catch
            {
                // Non-critical
            }

            try
            {
                File.Delete(updateMarkerPath);
            }
            catch
            {
                // Non-critical
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash the app
            Debug.WriteLine($"Failed to apply update: {ex}");
        }
    }

    /// <summary>
    /// Recursively copies files from staging directory to app directory.
    /// Handles creating subdirectories as needed.
    /// </summary>
    private static void ApplyUpdate(string sourceDir, string targetDir)
    {
        var sourceInfo = new DirectoryInfo(sourceDir);

        foreach (var file in sourceInfo.GetFiles())
        {
            var targetPath = Path.Combine(targetDir, file.Name);

            // Create backup of existing file (optional, for safety)
            if (File.Exists(targetPath))
            {
                var backupPath = targetPath + ".backup";
                File.Copy(targetPath, backupPath, overwrite: true);
            }

            // Copy new file
            file.CopyTo(targetPath, overwrite: true);
        }

        foreach (var subDir in sourceInfo.GetDirectories())
        {
            var targetSubDir = Path.Combine(targetDir, subDir.Name);
            Directory.CreateDirectory(targetSubDir);
            ApplyUpdate(subDir.FullName, targetSubDir);
        }
    }

    /// <summary>
    /// Creates a marker file indicating an update should be applied on next restart.
    /// This is called by UpdateService after successfully staging an update.
    /// </summary>
    internal static void MarkUpdateForNextRestart(string stagingDirectory)
    {
        var markerPath = Path.Combine(AppContext.BaseDirectory, ".update_pending");
        File.WriteAllText(markerPath, stagingDirectory);
    }
}
