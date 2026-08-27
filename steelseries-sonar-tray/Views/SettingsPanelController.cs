using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonarQuickMixer.Midi;
using SonarQuickMixer.Services;
using SonarQuickMixer.Settings;
using SonarQuickMixer.Sonar;
using SonarQuickMixer.Updates;

namespace SonarQuickMixer.Views;

public sealed class SettingsPanelController
{
    private readonly AppSettings _settings;
    private readonly MediaKeysOverrideService _mediaKeysOverride;
    private readonly DiscordScreenshareEchoFixService _discordScreenshareEchoFix;
    private readonly VolumeOverlayService _volumeOverlay;
    private readonly MidiControlService? _midiControl;
    private readonly Action _applyTrayIcon;
    private readonly ToggleButton _runAtWindowsStartupToggle;
    private readonly ToggleButton _mediaKeysOverrideToggle;
    private readonly ToggleButton _volumeOverlayToggle;
    private readonly ToggleButton _discordEchoFixToggle;
    private readonly ToggleButton _audioVisualizerToggle;
    private readonly ToggleButton? _midiEnabledToggle;
    private readonly ToggleButton _showDeviceNameToggle;
    private readonly ToggleButton _showDeviceBatteryToggle;
    private readonly ToggleButton _showDeviceConnectionToggle;
    private readonly ToggleButton _showOutputDeviceSelectorToggle;
    private readonly ToggleButton _showMicrophoneDeviceSelectorToggle;
    private readonly System.Windows.Controls.ComboBox _mediaKeysOverrideChannelCombo;
    private readonly FrameworkElement _mediaKeysOverrideChannelPanel;
    private readonly System.Windows.Controls.ComboBox _trayIconStyleCombo;
    private readonly Action? _onDeviceSelectorVisibilityChanged;

    private bool _suppressFeatureToggleChanges;
    private bool _suppressMediaKeysChannelChange;
    private bool _suppressTrayIconStyleChange;

    public SettingsPanelController(
        AppSettings settings,
        MediaKeysOverrideService mediaKeysOverride,
        DiscordScreenshareEchoFixService discordScreenshareEchoFix,
        VolumeOverlayService volumeOverlay,
        Action applyTrayIcon,
        ToggleButton runAtWindowsStartupToggle,
        ToggleButton mediaKeysOverrideToggle,
        ToggleButton volumeOverlayToggle,
        ToggleButton discordEchoFixToggle,
        ToggleButton audioVisualizerToggle,
        System.Windows.Controls.ComboBox mediaKeysOverrideChannelCombo,
        FrameworkElement mediaKeysOverrideChannelPanel,
        System.Windows.Controls.ComboBox trayIconStyleCombo,
        ToggleButton showDeviceNameToggle,
        ToggleButton showDeviceBatteryToggle,
        ToggleButton showDeviceConnectionToggle,
        ToggleButton showOutputDeviceSelectorToggle,
        ToggleButton showMicrophoneDeviceSelectorToggle,
        MidiControlService? midiControl = null,
        ToggleButton? midiEnabledToggle = null,
        Action? onDeviceSelectorVisibilityChanged = null)
    {
        _settings = settings;
        _mediaKeysOverride = mediaKeysOverride;
        _discordScreenshareEchoFix = discordScreenshareEchoFix;
        _volumeOverlay = volumeOverlay;
        _midiControl = midiControl;
        _applyTrayIcon = applyTrayIcon;
        _runAtWindowsStartupToggle = runAtWindowsStartupToggle;
        _mediaKeysOverrideToggle = mediaKeysOverrideToggle;
        _volumeOverlayToggle = volumeOverlayToggle;
        _discordEchoFixToggle = discordEchoFixToggle;
        _audioVisualizerToggle = audioVisualizerToggle;
        _midiEnabledToggle = midiEnabledToggle;
        _showDeviceNameToggle = showDeviceNameToggle;
        _showDeviceBatteryToggle = showDeviceBatteryToggle;
        _showDeviceConnectionToggle = showDeviceConnectionToggle;
        _showOutputDeviceSelectorToggle = showOutputDeviceSelectorToggle;
        _showMicrophoneDeviceSelectorToggle = showMicrophoneDeviceSelectorToggle;
        _mediaKeysOverrideChannelCombo = mediaKeysOverrideChannelCombo;
        _mediaKeysOverrideChannelPanel = mediaKeysOverrideChannelPanel;
        _trayIconStyleCombo = trayIconStyleCombo;
        _onDeviceSelectorVisibilityChanged = onDeviceSelectorVisibilityChanged;
    }

    public bool AudioVisualizerEnabled => _settings.AudioVisualizerEnabled;
    public bool ShowOutputDeviceSelector => _settings.ShowOutputDeviceSelector;
    public bool ShowMicrophoneDeviceSelector => _settings.ShowMicrophoneDeviceSelector;

    public void InitializeFromSettings()
    {
        _suppressFeatureToggleChanges = true;
        try
        {
            _runAtWindowsStartupToggle.IsChecked = _settings.RunAtWindowsStartup;
            _mediaKeysOverrideToggle.IsChecked = _settings.MediaKeysOverride;
            _volumeOverlayToggle.IsChecked = _settings.VolumeOverlayEnabled;
            _discordEchoFixToggle.IsChecked = _settings.DiscordScreenshareEchoFix;
            _audioVisualizerToggle.IsChecked = _settings.AudioVisualizerEnabled;
            if (_midiEnabledToggle is not null)
            {
                _midiEnabledToggle.IsChecked = _settings.MidiEnabled;
            }

            _showDeviceNameToggle.IsChecked = _settings.ShowDeviceName;
            _showDeviceBatteryToggle.IsChecked = _settings.ShowDeviceBattery;
            _showDeviceConnectionToggle.IsChecked = _settings.ShowDeviceConnection;
            _showOutputDeviceSelectorToggle.IsChecked = _settings.ShowOutputDeviceSelector;
            _showMicrophoneDeviceSelectorToggle.IsChecked = _settings.ShowMicrophoneDeviceSelector;
        }
        finally
        {
            _suppressFeatureToggleChanges = false;
        }

        PopulateMediaKeysOverrideChannelCombo();
        SelectMediaKeysOverrideChannel(_settings.MediaKeysOverrideChannel);
        PopulateTrayIconStyleCombo();
        SelectTrayIconStyle(_settings.TrayIconStyle);
        ApplyMediaKeysOverrideSettings();
        ApplyDiscordScreenshareEchoFixSettings();
        ApplyMidiSettings();
        _onDeviceSelectorVisibilityChanged?.Invoke();
    }

    public void SyncFeatureSettingsFromUi()
    {
        _settings.RunAtWindowsStartup = _runAtWindowsStartupToggle.IsChecked == true;
        _settings.MediaKeysOverride = _mediaKeysOverrideToggle.IsChecked == true;
        _settings.VolumeOverlayEnabled = _volumeOverlayToggle.IsChecked == true;
        _settings.DiscordScreenshareEchoFix = _discordEchoFixToggle.IsChecked == true;
        _settings.AudioVisualizerEnabled = _audioVisualizerToggle.IsChecked == true;
        if (_midiEnabledToggle is not null)
        {
            _settings.MidiEnabled = _midiEnabledToggle.IsChecked == true;
        }

        _settings.ShowDeviceName = _showDeviceNameToggle.IsChecked == true;
        _settings.ShowDeviceBattery = _showDeviceBatteryToggle.IsChecked == true;
        _settings.ShowDeviceConnection = _showDeviceConnectionToggle.IsChecked == true;
        _settings.ShowOutputDeviceSelector = _showOutputDeviceSelectorToggle.IsChecked == true;
        _settings.ShowMicrophoneDeviceSelector = _showMicrophoneDeviceSelectorToggle.IsChecked == true;
    }

    public void OnFeatureToggleChanged(object sender, RoutedEventArgs e, Action onVisualizerChanged)
    {
        if (_suppressFeatureToggleChanges)
        {
            return;
        }

        if (sender == _runAtWindowsStartupToggle && !ApplyRunAtWindowsStartupSetting())
        {
            return;
        }

        SyncFeatureSettingsFromUi();
        _settings.Save();

        onVisualizerChanged();
        ApplyMediaKeysOverrideSettings();
        ApplyDiscordScreenshareEchoFixSettings();
        ApplyMidiSettings();
        _onDeviceSelectorVisibilityChanged?.Invoke();

        if (!_settings.VolumeOverlayEnabled)
        {
            _volumeOverlay.HideImmediately();
        }
    }

    public void OnMediaKeysOverrideChannelChanged()
    {
        if (_suppressMediaKeysChannelChange || _mediaKeysOverrideChannelCombo.SelectedItem is null)
        {
            return;
        }

        _settings.MediaKeysOverrideChannel = GetSelectedMediaKeysOverrideChannel();
        _settings.Save();
        ApplyMediaKeysOverrideSettings();
    }

    public void OnTrayIconStyleChanged()
    {
        if (_suppressTrayIconStyleChange || _trayIconStyleCombo.SelectedItem is null)
        {
            return;
        }

        _settings.TrayIconStyle = GetSelectedTrayIconStyle();
        _settings.Save();
        _applyTrayIcon();
    }

    public void ApplyMediaKeysOverrideSettings()
    {
        var enabled = _mediaKeysOverrideToggle.IsChecked == true;
        _mediaKeysOverrideChannelPanel.IsEnabled = enabled;
        _mediaKeysOverrideChannelPanel.Opacity = enabled ? 1.0 : 0.55;

        var channel = GetSelectedMediaKeysOverrideChannel();
        _settings.MediaKeysOverrideChannel = channel;
        _mediaKeysOverride.SetTargetChannel(channel);
        _mediaKeysOverride.SetEnabled(enabled);
    }

    public void ApplyDiscordScreenshareEchoFixSettings()
    {
        var enabled = _discordEchoFixToggle.IsChecked == true;
        _settings.DiscordScreenshareEchoFix = enabled;
        _discordScreenshareEchoFix.SetEnabled(enabled);
    }

    public void ApplyMidiSettings()
    {
        if (_midiControl is null)
        {
            return;
        }

        var enabled = _midiEnabledToggle?.IsChecked == true;
        _settings.MidiEnabled = enabled;
        _midiControl.SetEnabled(enabled);
    }

    private bool ApplyRunAtWindowsStartupSetting()
    {
        var enabled = _runAtWindowsStartupToggle.IsChecked == true;
        if (WindowsStartupRegistration.TrySetEnabled(enabled))
        {
            _settings.RunAtWindowsStartup = enabled;
            return true;
        }

        _suppressFeatureToggleChanges = true;
        try
        {
            _runAtWindowsStartupToggle.IsChecked = _settings.RunAtWindowsStartup;
        }
        finally
        {
            _suppressFeatureToggleChanges = false;
        }

        return false;
    }

    private void PopulateTrayIconStyleCombo()
    {
        _trayIconStyleCombo.Items.Clear();
        _trayIconStyleCombo.Items.Add(new ComboBoxItem { Content = "Auto (match Windows theme)", Tag = TrayIconStyle.Auto });
        _trayIconStyleCombo.Items.Add(new ComboBoxItem { Content = "Accent (cyan)", Tag = TrayIconStyle.Accent });
        _trayIconStyleCombo.Items.Add(new ComboBoxItem { Content = "White", Tag = TrayIconStyle.White });
        _trayIconStyleCombo.Items.Add(new ComboBoxItem { Content = "Dark", Tag = TrayIconStyle.Dark });
    }

    private void SelectTrayIconStyle(TrayIconStyle style)
    {
        _suppressTrayIconStyleChange = true;
        try
        {
            foreach (ComboBoxItem item in _trayIconStyleCombo.Items)
            {
                if (item.Tag is TrayIconStyle candidate && candidate == style)
                {
                    _trayIconStyleCombo.SelectedItem = item;
                    return;
                }
            }

            _trayIconStyleCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressTrayIconStyleChange = false;
        }
    }

    private TrayIconStyle GetSelectedTrayIconStyle()
    {
        if (_trayIconStyleCombo.SelectedItem is ComboBoxItem { Tag: TrayIconStyle style })
        {
            return style;
        }

        return TrayIconStyle.Auto;
    }

    private void PopulateMediaKeysOverrideChannelCombo()
    {
        _mediaKeysOverrideChannelCombo.Items.Clear();

        foreach (var channel in SonarChannels.All)
        {
            _mediaKeysOverrideChannelCombo.Items.Add(new ComboBoxItem
            {
                Content = SonarChannels.GetDisplayName(channel),
                Tag = channel
            });
        }
    }

    private void SelectMediaKeysOverrideChannel(string channel)
    {
        var normalizedChannel = SonarChannels.NormalizeChannel(channel);

        _suppressMediaKeysChannelChange = true;
        try
        {
            for (var i = 0; i < _mediaKeysOverrideChannelCombo.Items.Count; i++)
            {
                if (_mediaKeysOverrideChannelCombo.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, normalizedChannel, StringComparison.OrdinalIgnoreCase))
                {
                    _mediaKeysOverrideChannelCombo.SelectedIndex = i;
                    return;
                }
            }

            _mediaKeysOverrideChannelCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressMediaKeysChannelChange = false;
        }
    }

    private string GetSelectedMediaKeysOverrideChannel()
    {
        if (_mediaKeysOverrideChannelCombo.SelectedItem is ComboBoxItem item)
        {
            return SonarChannels.NormalizeChannel(item.Tag as string);
        }

        return SonarChannels.NormalizeChannel(_settings.MediaKeysOverrideChannel);
    }
}

public sealed class UpdateNotificationController
{
    private readonly GitHubUpdateChecker _updateChecker;
    private readonly TextBlock _settingsVersionText;
    private readonly FrameworkElement _updateNotificationDot;
    private readonly FrameworkElement _updateAvailablePanel;
    private readonly TextBlock _updateAvailableText;
    private readonly System.Windows.Controls.Button _openSettingsButton;
    private readonly Action<bool> _setTrayUpdateAvailable;

    private UpdateCheckResult? _updateCheckResult;

    public UpdateNotificationController(
        GitHubUpdateChecker updateChecker,
        TextBlock settingsVersionText,
        FrameworkElement updateNotificationDot,
        FrameworkElement updateAvailablePanel,
        TextBlock updateAvailableText,
        System.Windows.Controls.Button openSettingsButton,
        Action<bool> setTrayUpdateAvailable)
    {
        _updateChecker = updateChecker;
        _settingsVersionText = settingsVersionText;
        _updateNotificationDot = updateNotificationDot;
        _updateAvailablePanel = updateAvailablePanel;
        _updateAvailableText = updateAvailableText;
        _openSettingsButton = openSettingsButton;
        _setTrayUpdateAvailable = setTrayUpdateAvailable;
    }

    public void InitializeVersionText() =>
        _settingsVersionText.Text = $"Installed: {AppVersion.Display}";

    public async Task CheckForUpdatesAsync()
    {
        var result = await _updateChecker.CheckForUpdateAsync().ConfigureAwait(false);
        await System.Windows.Application.Current.Dispatcher
            .InvokeAsync(() => ApplyUpdateCheckResult(result))
            .Task.ConfigureAwait(false);
    }

    public void OpenReleasePage()
    {
        var url = _updateCheckResult?.ReleaseUrl
            ?? "https://github.com/lixetron/steelseries-sonar-tray/releases/latest";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: browser launch can fail in restricted environments.
        }
    }

    private void ApplyUpdateCheckResult(UpdateCheckResult? result)
    {
        _updateCheckResult = result;
        _settingsVersionText.Text = $"Installed: {AppVersion.Display}";

        if (result?.IsUpdateAvailable == true)
        {
            _updateNotificationDot.Visibility = Visibility.Visible;
            _updateAvailablePanel.Visibility = Visibility.Visible;
            _updateAvailableText.Text =
                $"Version {AppVersion.Format(result.LatestVersion)} is available on GitHub.";
            _openSettingsButton.ToolTip = "Settings — update available";
            _setTrayUpdateAvailable(true);
            return;
        }

        _updateNotificationDot.Visibility = Visibility.Collapsed;
        _updateAvailablePanel.Visibility = Visibility.Collapsed;
        _openSettingsButton.ToolTip = "Settings";
        _setTrayUpdateAvailable(false);
    }
}
