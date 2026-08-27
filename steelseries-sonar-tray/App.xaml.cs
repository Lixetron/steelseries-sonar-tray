using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Application = System.Windows.Application;
using SonarQuickMixer.Midi;
using SonarQuickMixer.Services;
using SonarQuickMixer.Settings;
using SonarQuickMixer.Tray;
using SonarQuickMixer.Views;
using WinForms = System.Windows.Forms;

namespace SonarQuickMixer;

public partial class App : Application
{
    private WinForms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private MidiConfigWindow? _midiConfigWindow;
    private AppSettings? _settings;
    private MediaKeysOverrideService? _mediaKeysOverride;
    private MidiControlService? _midiControl;
    private DiscordScreenshareEchoFixService? _discordScreenshareEchoFix;
    private VolumeOverlayService? _volumeOverlay;
    private SingleInstanceManager? _singleInstance;
    private bool _trayUpdateAvailable;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new SingleInstanceManager();
        if (!_singleInstance.TryAcquireOwnership())
        {
            _singleInstance.NotifyExistingInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _settings = AppSettings.Load();
        WindowsStartupRegistration.Apply(_settings.RunAtWindowsStartup);

        _volumeOverlay = new VolumeOverlayService(() => _settings!.VolumeOverlayEnabled);
        _mediaKeysOverride = new MediaKeysOverrideService();
        _mediaKeysOverride.VolumeAdjusted += state => _volumeOverlay.Show(state);

        _midiControl = new MidiControlService(_settings);
        _midiControl.VolumeAdjusted += state => _volumeOverlay.Show(state);
        _midiControl.SetEnabled(_settings.MidiEnabled);

        _discordScreenshareEchoFix = new DiscordScreenshareEchoFixService();
        _discordScreenshareEchoFix.SetEnabled(_settings.DiscordScreenshareEchoFix);
        _mainWindow = new MainWindow(
            _settings,
            _mediaKeysOverride,
            _discordScreenshareEchoFix,
            _volumeOverlay,
            ApplyTrayIcon,
            SetTrayUpdateAvailable,
            _midiControl,
            ShowMidiSetup);
        _ = new WindowInteropHelper(_mainWindow).EnsureHandle();
        _ = _mainWindow.WarmupAsync();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = TrayIconProvider.Load(_settings.TrayIconStyle, _trayUpdateAvailable),
            Text = BuildTrayTooltip(),
            Visible = true
        };

        _notifyIcon.MouseClick += NotifyIcon_MouseClick;

        var contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.Add("Open Mixer", null, (_, _) => ShowMixer());
        contextMenu.Items.Add("MIDI Setup…", null, (_, _) => ShowMidiSetup());
        contextMenu.Items.Add("Exit", null, (_, _) => ShutdownApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;

        _singleInstance.StartListening(() => ShowMixer());
    }

    internal void ApplyTrayIcon()
    {
        if (_notifyIcon is null || _settings is null)
        {
            return;
        }

        var previousIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = TrayIconProvider.Load(_settings.TrayIconStyle, _trayUpdateAvailable);
        _notifyIcon.Text = BuildTrayTooltip();

        if (previousIcon is not null)
        {
            previousIcon.Dispose();
        }
    }

    internal void SetTrayUpdateAvailable(bool available)
    {
        if (_trayUpdateAvailable == available)
        {
            return;
        }

        _trayUpdateAvailable = available;
        ApplyTrayIcon();
    }

    private string BuildTrayTooltip() =>
        _trayUpdateAvailable
            ? "Sonar Quick Mixer — update available"
            : "Sonar Quick Mixer";

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

    private void ShowMidiSetup()
    {
        if (_midiControl is null)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (_midiConfigWindow is null)
            {
                _midiConfigWindow = new MidiConfigWindow(_midiControl);
                _midiConfigWindow.Closed += (_, _) => _midiConfigWindow = null;
            }

            _midiConfigWindow.Show();
            _midiConfigWindow.Activate();
        });
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

        _midiConfigWindow?.Close();
        _midiConfigWindow = null;

        _mediaKeysOverride?.Dispose();
        _mediaKeysOverride = null;

        _midiControl?.Dispose();
        _midiControl = null;

        _discordScreenshareEchoFix?.Dispose();
        _discordScreenshareEchoFix = null;

        _volumeOverlay?.Dispose();
        _volumeOverlay = null;

        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }
}
