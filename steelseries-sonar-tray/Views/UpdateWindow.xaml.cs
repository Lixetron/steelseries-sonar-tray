using System.Diagnostics;
using System.Windows;
using Application = System.Windows.Application;
using SonarQuickMixer.Localization;
using SonarQuickMixer.Updates;

namespace SonarQuickMixer.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _updateService;
    private readonly Version _targetVersion;
    private CancellationTokenSource? _downloadCancellation;

    public UpdateWindow(Version targetVersion, UpdateService updateService)
    {
        InitializeComponent();
        _targetVersion = targetVersion;
        _updateService = updateService;

        VersionText.Text = BuildVersionText();

        // Subscribe to download events
        _updateService.DownloadProgressChanged += UpdateService_DownloadProgressChanged;
        _updateService.DownloadCompleted += UpdateService_DownloadCompleted;
        Loc.LanguageChanged += OnLanguageChanged;

        // Start downloading
        StartDownload();
    }

    private string BuildVersionText() =>
        Loc.Format("update.versionAvailable", AppVersion.Format(_targetVersion));

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() => VersionText.Text = BuildVersionText());

    private void StartDownload()
    {
        _downloadCancellation = new CancellationTokenSource();
        _ = _updateService.DownloadAndStageUpdateAsync(_targetVersion, _downloadCancellation.Token);
    }

    private void UpdateService_DownloadProgressChanged(object? sender, UpdateProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = e.Percentage;
            ProgressText.Text = Loc.Format("update.progress", e.Percentage, e.FormatSize());
        });
    }

    private void UpdateService_DownloadCompleted(object? sender, UpdateDownloadCompleteEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Success)
            {
                ShowReadyState();
            }
            else
            {
                ShowFailedState();
            }
        });
    }

    private void ShowReadyState()
    {
        DownloadingPanel.Visibility = Visibility.Collapsed;
        ReadyPanel.Visibility = Visibility.Visible;
        FailedPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowFailedState()
    {
        DownloadingPanel.Visibility = Visibility.Collapsed;
        ReadyPanel.Visibility = Visibility.Collapsed;
        FailedPanel.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCancellation?.Cancel();
        _updateService.CancelDownload();
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        // Update is already staged in .update_pending, will apply on next startup
        Close();
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        // Restart the application
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true
        });

        Application.Current.Shutdown();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _downloadCancellation?.Dispose();
        _updateService.DownloadProgressChanged -= UpdateService_DownloadProgressChanged;
        _updateService.DownloadCompleted -= UpdateService_DownloadCompleted;
        Loc.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }
}
