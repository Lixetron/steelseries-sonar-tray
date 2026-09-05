using System.IO;

namespace SonarQuickMixer.Updates;

/// <summary>
/// Coordinates update checking, downloading, and application.
/// Raises events that UI can subscribe to for showing progress dialogs.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly GitHubUpdateChecker _updateChecker = new();
    private readonly UpdateDownloader _updateDownloader = new();

    public event EventHandler<UpdateCheckEventArgs>? UpdateCheckCompleted;
    public event EventHandler<UpdateProgressEventArgs>? DownloadProgressChanged;
    public event EventHandler<UpdateDownloadCompleteEventArgs>? DownloadCompleted;

    /// <summary>
    /// Checks for updates asynchronously without blocking.
    /// Raises UpdateCheckCompleted event when done.
    /// </summary>
    public void CheckForUpdateAsync()
    {
        _ = Task.Run(async () =>
        {
            var result = await _updateChecker.CheckForUpdateAsync();
            UpdateCheckCompleted?.Invoke(this, new UpdateCheckEventArgs(result));
        });
    }

    /// <summary>
    /// Downloads and stages the update.
    /// Raises DownloadProgressChanged and DownloadCompleted events.
    /// </summary>
    public async Task<bool> DownloadAndStageUpdateAsync(
        Version targetVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Download the ZIP file with progress reporting
            var zipPath = await _updateDownloader.DownloadLatestReleaseAsync(
                targetVersion,
                progress =>
                {
                    DownloadProgressChanged?.Invoke(
                        this,
                        new UpdateProgressEventArgs(progress.downloaded, progress.total));
                },
                cancellationToken);

            if (zipPath is null)
            {
                DownloadCompleted?.Invoke(this, new UpdateDownloadCompleteEventArgs(false));
                return false;
            }

            // Extract to staging directory
            var success = UpdateDownloader.ExtractUpdateToStagingDirectory(zipPath);
            DownloadCompleted?.Invoke(this, new UpdateDownloadCompleteEventArgs(success));

            return success;
        }
        catch
        {
            DownloadCompleted?.Invoke(this, new UpdateDownloadCompleteEventArgs(false));
            return false;
        }
    }

    /// <summary>
    /// Checks if there's a pending update staged for the next restart.
    /// </summary>
    public static bool IsPendingUpdateAvailable() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, ".update_pending"));

    /// <summary>
    /// Cancels an in-progress download.
    /// </summary>
    public void CancelDownload()
    {
        _updateDownloader.CancelDownload();
    }

    public void Dispose()
    {
        _updateChecker.Dispose();
        _updateDownloader.Dispose();
    }
}

public sealed class UpdateCheckEventArgs : EventArgs
{
    public UpdateCheckEventArgs(UpdateCheckResult? result)
    {
        Result = result;
        IsUpdateAvailable = result?.IsUpdateAvailable ?? false;
    }

    public bool IsUpdateAvailable { get; }
    public UpdateCheckResult? Result { get; }
}

public sealed class UpdateProgressEventArgs : EventArgs
{
    public UpdateProgressEventArgs(long downloaded, long total)
    {
        Downloaded = downloaded;
        Total = total;
        Percentage = total > 0 ? (int)((downloaded * 100) / total) : 0;
    }

    public long Downloaded { get; }
    public long Total { get; }
    public int Percentage { get; }

    public string FormatSize() =>
        $"{FormatBytes(Downloaded)} / {FormatBytes(Total)}";

    private static string FormatBytes(long bytes)
    {
        const double kilobyte = 1024.0;
        const double megabyte = kilobyte * 1024.0;

        return bytes switch
        {
            >= (long)megabyte => $"{bytes / megabyte:F1} MB",
            >= (long)kilobyte => $"{bytes / kilobyte:F1} KB",
            _ => $"{bytes} B"
        };
    }
}

public sealed class UpdateDownloadCompleteEventArgs : EventArgs
{
    public UpdateDownloadCompleteEventArgs(bool success)
    {
        Success = success;
    }

    public bool Success { get; }
}
