# Automatic Updates System

This document describes how the in-app update system works in Sonar Quick Mixer.

## Architecture

The update system consists of several components:

```
┌─────────────────────────────────────────────────────────────┐
│ Startup                                                     │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│ 1. Launcher.ApplyPendingUpdateIfExists()                   │
│    └─> Checks for .update_pending marker                  │
│    └─> Applies staged files from .update_staging           │
│    └─> Cleans up old staging directory                     │
│                                                             │
│ 2. App.OnStartup()                                          │
│    └─> Initialize UpdateService                            │
│    └─> CheckForUpdateAsync() (one-time)                   │
│    └─> Schedule periodic checks (every 30 min)            │
│                                                             │
│ 3. UpdateService.UpdateCheckCompleted event               │
│    └─> Show UpdateWindow if update available              │
│                                                             │
└─────────────────────────────────────────────────────────────┘

                        ↓

┌─────────────────────────────────────────────────────────────┐
│ Download Phase (in UpdateWindow)                            │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│ User sees:                                                  │
│  ┌───────────────────────────────────────────────────┐    │
│  │ Update Available v1.2.1                           │    │
│  │ Downloading... [████████░░] 65%                   │    │
│  │ 25 MB / 40 MB                                     │    │
│  │ [Cancel]                                          │    │
│  └───────────────────────────────────────────────────┘    │
│                                                             │
│ Behind the scenes:                                          │
│  1. UpdateDownloader.DownloadLatestReleaseAsync()          │
│     └─> Stream ZIP from GitHub releases                   │
│     └─> Report progress to UpdateWindow                   │
│     └─> Save to temp directory                            │
│                                                             │
│  2. UpdateDownloader.ExtractUpdateToStagingDirectory()    │
│     └─> Unzip to .update_staging folder                   │
│     └─> Create .update_pending marker file                │
│     └─> Clean up temp ZIP                                 │
│                                                             │
└─────────────────────────────────────────────────────────────┘

                        ↓

┌─────────────────────────────────────────────────────────────┐
│ Ready Phase (in UpdateWindow)                               │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│ User can:                                                   │
│  ┌───────────────────────────────────────────────────┐    │
│  │ ✅ Update downloaded and ready to install.        │    │
│  │ Restart now or it will apply automatically       │    │
│  │ at next startup.                                  │    │
│  │                                                   │    │
│  │ [Later]  [Restart Now]                           │    │
│  └───────────────────────────────────────────────────┘    │
│                                                             │
│ Option 1: [Restart Now]                                    │
│  └─> Restart the application immediately                  │
│  └─> Launcher applies update on startup                   │
│                                                             │
│ Option 2: [Later]                                          │
│  └─> Close dialog, keep using app                         │
│  └─> Update will apply at next manual restart             │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

## File Structure

```
AppContext.BaseDirectory/
├── SonarQuickMixer.exe              (main executable)
├── NAudio.dll
├── Presets/                         (official MIDI layouts)
├── .update_pending                  (marker: "path/to/.update_staging")
└── .update_staging/                 (staged files from latest release)
    ├── SonarQuickMixer.exe          (new version)
    ├── NAudio.dll                   (if changed)
    └── Presets/                     (if changed)
```

## Key Classes

### GitHubUpdateChecker
- Checks GitHub API for latest release
- Compares versions using semantic versioning
- Returns `UpdateCheckResult` with available update info

### UpdateDownloader
- Downloads ZIP from GitHub releases
- Streams data (memory-efficient)
- Reports download progress
- Extracts to `.update_staging` directory
- Creates `.update_pending` marker file

### UpdateService
- Coordinates checking and downloading
- Fires events: `UpdateCheckCompleted`, `DownloadProgressChanged`, `DownloadCompleted`
- Non-blocking async operations

### Launcher
- Called at app startup before MainWindow creation
- Applies pending updates if `.update_pending` exists
- Recursively copies files from `.update_staging` to app directory
- Creates backups of existing files (.backup)
- Cleans up staging directory

### UpdateWindow
- WPF window showing three states:
  - **Downloading**: Progress bar, cancel button
  - **Ready**: Success message, restart/later options
  - **Failed**: Error message, close/github link buttons

## Update Flow

### Normal Update Scenario

```
1. User is running v1.2.0
2. App checks GitHub API → finds v1.2.1 available
3. UpdateWindow opens automatically
4. User clicks "Download" (automatic)
5. Files stream and extract to .update_staging
6. .update_pending marker created
7. User chooses "Restart Now"
8. Application closes
9. New instance starts
10. Launcher.ApplyPendingUpdateIfExists() called first
11. Files copied from .update_staging to app directory
12. Update marker and staging directory cleaned up
13. Main app loads and shows v1.2.1
```

### Delayed Update Scenario

```
1-7. [Same as above]
8. User clicks "Later"
9. UpdateWindow closes, app continues running
10. User manually restarts later (or when computer restarts)
11. Launcher applies update silently
12. App loads v1.2.1
```

### Failed Download Scenario

```
1-3. [Same as above]
4. Network error or timeout
5. UpdateWindow shows error message
6. User can retry or visit GitHub Releases manually
7. No .update_pending marker created
```

## Configuration

### Update Check Interval
Edit `App.xaml.cs`:
```csharp
_updateCheckTimer = new DispatcherTimer
{
    Interval = TimeSpan.FromMinutes(30)  // Change interval here
};
```

### Download Timeout
Edit `UpdateDownloader.cs`:
```csharp
private readonly HttpClient _httpClient = new()
{
    Timeout = TimeSpan.FromMinutes(5)  // Change timeout here
};
```

## GitHub Release Requirements

For updates to work, releases must:

1. Be tagged as `v{Major}.{Minor}.{Build}` (e.g., `v1.2.1`)
2. Include asset named `SonarQuickMixer-v1.2.1-single.zip`
   - This is the self-contained single-file build
   - Contains `SonarQuickMixer.exe` + `Presets\` folder
   - Built by CI/CD pipeline

Current release URL format:
```
https://github.com/lixetron/steelseries-sonar-tray/releases/download/v1.2.1/SonarQuickMixer-v1.2.1-single.zip
```

## CI/CD Integration

The GitHub Actions workflow (`release.yml`) already creates the required ZIP file:

```yaml
- name: Package release assets
  run: |
    Compress-Archive -Path (Join-Path $singleDir '*') `
      -DestinationPath "SonarQuickMixer-$tag-single.zip" -Force
```

This ZIP contains:
- `SonarQuickMixer.exe` (self-contained)
- `Presets/` folder (alongside exe)

No additional changes needed to CI/CD.

## Security Considerations

1. **ZIP Extraction**: Files are extracted to temporary `.update_staging` directory
   - No arbitrary path traversal (ZIP entries normalized)
   - Old staging directory deleted before new extraction

2. **File Replacement**: Updated files overwrite old ones with backups
   - Original files saved as `.backup` for recovery if needed
   - Permissions preserved

3. **Network**: Uses HTTPS for GitHub API and downloads
   - 10-second timeout for API checks
   - 5-minute timeout for large downloads

4. **User Control**:
   - Update is optional (user can click "Later")
   - Transparent UI showing what's happening
   - Can be cancelled during download

## Troubleshooting

### Update Never Completes
- Check network connectivity
- Verify GitHub is accessible
- Check temp directory has write permissions
- Look at `.update_staging` directory — if it exists but old, delete it

### Update Applied but Exe Didn't Change
- Check if app was fully closed before restart
- Verify `.update_staging` directory was deleted
- Check `.update_pending` marker was removed
- Backup files (`.backup`) indicate old versions are still there

### Infinite Update Loop
- Very unlikely, but delete `.update_pending` file manually
- App will start normally without update

## Future Improvements

1. **Delta Updates**: Download only changed files (2-10 MB instead of 50 MB)
2. **Automatic Restart**: Option to restart automatically after download
3. **Rollback**: Keep previous version to rollback if update fails
4. **Update Manifest**: Include checksums and release notes in metadata
5. **Background Check**: Check for updates even when tray icon minimized

## Testing

To test the update system locally:

```csharp
// In App.xaml.cs, manually trigger update check
_updateService?.CheckForUpdateAsync();

// To test staging, manually create marker:
var stagingDir = Path.Combine(AppContext.BaseDirectory, ".update_staging");
File.WriteAllText(
    Path.Combine(AppContext.BaseDirectory, ".update_pending"),
    stagingDir);
```

Then restart the app to see Launcher apply the staged files.
