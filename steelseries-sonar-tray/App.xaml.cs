using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Application = System.Windows.Application;
using WinForms = System.Windows.Forms;

namespace SteelSeries.SonarTray;

public partial class App : Application
{
    private WinForms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private AppSettings? _settings;
    private MediaKeysOverrideService? _mediaKeysOverride;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = AppSettings.Load();

        _mediaKeysOverride = new MediaKeysOverrideService();
        _mainWindow = new MainWindow(_settings, _mediaKeysOverride);
        _ = new WindowInteropHelper(_mainWindow).EnsureHandle();
        _ = _mainWindow.WarmupAsync();
        _mediaKeysOverride.SetEnabled(_settings.MediaKeysOverride);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Sonar Quick Mixer",
            Visible = true
        };

        _notifyIcon.MouseClick += NotifyIcon_MouseClick;

        var contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.Add("Open Mixer", null, (_, _) => ShowMixer());
        contextMenu.Items.Add("Exit", null, (_, _) => ShutdownApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void NotifyIcon_MouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button != WinForms.MouseButtons.Left)
        {
            return;
        }

        ShowMixer(new System.Drawing.Point(e.X, e.Y));
    }

    private void ShowMixer(System.Drawing.Point? anchorScreenPoint = null)
    {
        if (_mainWindow is null)
        {
            return;
        }

        Dispatcher.Invoke(() => _ = _mainWindow.ShowInstantlyAsync(anchorScreenPoint));
    }

    private void ShutdownApplication()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _mediaKeysOverride?.Dispose();
        _mediaKeysOverride = null;

        base.OnExit(e);
    }
}
