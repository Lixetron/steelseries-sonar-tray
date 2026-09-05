using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace SonarQuickMixer.Updates;

/// <summary>
/// Downloads and applies application updates from GitHub releases.
/// </summary>
public sealed class UpdateDownloader : IDisposable
{
    private const string Owner = "lixetron";
    private const string Repo = "steelseries-sonar-tray";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    private CancellationTokenSource? _cancellationTokenSource;

    public UpdateDownloader()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SonarQuickMixer-Updater");
    }

    /// <summary>
    /// Downloads the latest release ZIP from GitHub.
    /// Reports progress via the provided action.
    /// </summary>
    public async Task<string?> DownloadLatestReleaseAsync(
        Version targetVersion,
        Action<(long downloaded, long total)>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var releaseUri = GetReleaseDownloadUri(targetVersion);

            var response = await _httpClient.GetAsync(
                releaseUri,
                HttpCompletionOption.ResponseHeadersRead,
                _cancellationTokenSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength ?? 0;
            var tempPath = Path.Combine(Path.GetTempPath(), $"SonarQuickMixer-{targetVersion}.zip");

            await using (var contentStream = await response.Content
                .ReadAsStreamAsync(_cancellationTokenSource.Token)
                .ConfigureAwait(false))
            {
                await using (var fileStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    var buffer = new byte[8192];
                    var totalRead = 0L;
                    int read;

                    while ((read = await contentStream
                        .ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token)
                        .ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read, _cancellationTokenSource.Token)
                            .ConfigureAwait(false);
                        totalRead += read;
                        onProgress?.Invoke((totalRead, contentLength));
                    }
                }
            }

            return tempPath;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    /// <summary>
    /// Extracts the downloaded ZIP to a temporary update directory.
    /// The launcher will apply these files on next startup.
    /// </summary>
    public static bool ExtractUpdateToStagingDirectory(string zipPath)
    {
        try
        {
            var appDirectory = AppContext.BaseDirectory;
            var updateStagingDir = Path.Combine(appDirectory, ".update_staging");

            // Clean up old staging directory if it exists
            if (Directory.Exists(updateStagingDir))
            {
                Directory.Delete(updateStagingDir, recursive: true);
            }

            Directory.CreateDirectory(updateStagingDir);

            // Extract ZIP contents
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var targetPath = Path.Combine(updateStagingDir, entry.FullName);
                    var targetDirectory = Path.GetDirectoryName(targetPath);

                    if (!string.IsNullOrEmpty(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    if (!entry.FullName.EndsWith('/'))
                    {
                        entry.ExtractToFile(targetPath, overwrite: true);
                    }
                }
            }

            // Clean up ZIP after extraction
            try
            {
                File.Delete(zipPath);
            }
            catch
            {
                // Non-critical
            }

            // Create marker file for launcher
            var updateMarkerPath = Path.Combine(appDirectory, ".update_pending");
            File.WriteAllText(updateMarkerPath, updateStagingDir);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cancels an in-progress download.
    /// </summary>
    public void CancelDownload()
    {
        _cancellationTokenSource?.Cancel();
    }

    /// <summary>
    /// Gets the download URL for a specific release version.
    /// For self-contained single-file builds.
    /// </summary>
    private static string GetReleaseDownloadUri(Version version)
    {
        var versionTag = $"v{version.Major}.{version.Minor}.{version.Build}";
        return $"https://github.com/{Owner}/{Repo}/releases/download/{versionTag}/SonarQuickMixer-{versionTag}-single.zip";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}
