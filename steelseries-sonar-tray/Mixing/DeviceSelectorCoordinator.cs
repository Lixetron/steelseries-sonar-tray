using System.Windows;
using System.Windows.Controls;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Mixing;

internal sealed class DeviceSelectorCoordinator
{
    private readonly SonarApiClient _apiClient;
    private readonly System.Windows.Controls.ComboBox _outputCombo;
    private readonly System.Windows.Controls.ComboBox _microphoneCombo;
    private readonly FrameworkElement _outputRow;
    private readonly FrameworkElement _microphoneRow;
    private readonly FrameworkElement _selectorsPanel;
    private readonly Func<bool> _showOutputSelector;
    private readonly Func<bool> _showMicrophoneSelector;
    private readonly Action? _onVisibilityChanged;
    private readonly Action<string>? _setStatusText;

    private bool _suppressSelectionChanged;
    private bool _isApplyingChange;
    private bool _isRefreshing;

    public DeviceSelectorCoordinator(
        SonarApiClient apiClient,
        System.Windows.Controls.ComboBox outputCombo,
        System.Windows.Controls.ComboBox microphoneCombo,
        FrameworkElement outputRow,
        FrameworkElement microphoneRow,
        FrameworkElement selectorsPanel,
        Func<bool> showOutputSelector,
        Func<bool> showMicrophoneSelector,
        Action? onVisibilityChanged = null,
        Action<string>? setStatusText = null)
    {
        _apiClient = apiClient;
        _outputCombo = outputCombo;
        _microphoneCombo = microphoneCombo;
        _outputRow = outputRow;
        _microphoneRow = microphoneRow;
        _selectorsPanel = selectorsPanel;
        _showOutputSelector = showOutputSelector;
        _showMicrophoneSelector = showMicrophoneSelector;
        _onVisibilityChanged = onVisibilityChanged;
        _setStatusText = setStatusText;
    }

    public void ApplyVisibility()
    {
        var showOutput = _showOutputSelector();
        var showMicrophone = _showMicrophoneSelector();

        _outputRow.Visibility = showOutput ? Visibility.Visible : Visibility.Collapsed;
        _microphoneRow.Visibility = showMicrophone ? Visibility.Visible : Visibility.Collapsed;
        _selectorsPanel.Visibility = showOutput || showMicrophone
            ? Visibility.Visible
            : Visibility.Collapsed;

        _onVisibilityChanged?.Invoke();
    }

    public async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        ApplyVisibility();
        if (_selectorsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var devicesTask = _apiClient.GetAudioDevicesAsync();
            var selectionTask = _apiClient.GetDeviceSelectionAsync();
            await Task.WhenAll(devicesTask, selectionTask).ConfigureAwait(true);

            var devices = await devicesTask.ConfigureAwait(true);
            var selection = await selectionTask.ConfigureAwait(true);

            if (_showOutputSelector())
            {
                PopulateCombo(
                    _outputCombo,
                    SonarAudioDevicesParser.FilterPhysical(devices, SonarAudioDataFlow.Render),
                    selection?.OutputDeviceId);
            }

            if (_showMicrophoneSelector())
            {
                PopulateCombo(
                    _microphoneCombo,
                    SonarAudioDevicesParser.FilterPhysical(devices, SonarAudioDataFlow.Capture),
                    selection?.MicrophoneDeviceId);
            }
        }
        catch
        {
            // Device list refresh is best-effort while the overlay is open.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public async Task OnOutputSelectionChangedAsync()
    {
        if (_suppressSelectionChanged || _isApplyingChange)
        {
            return;
        }

        var deviceId = GetSelectedDeviceId(_outputCombo);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _isApplyingChange = true;
        try
        {
            var success = await _apiClient.SetOutputDeviceAsync(deviceId).ConfigureAwait(true);
            if (!success)
            {
                _setStatusText?.Invoke("Failed to set output device");
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _isApplyingChange = false;
        }
    }

    public async Task OnMicrophoneSelectionChangedAsync()
    {
        if (_suppressSelectionChanged || _isApplyingChange)
        {
            return;
        }

        var deviceId = GetSelectedDeviceId(_microphoneCombo);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _isApplyingChange = true;
        try
        {
            var success = await _apiClient.SetMicrophoneDeviceAsync(deviceId).ConfigureAwait(true);
            if (!success)
            {
                _setStatusText?.Invoke("Failed to set microphone");
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _isApplyingChange = false;
        }
    }

    private void PopulateCombo(
        System.Windows.Controls.ComboBox combo,
        IReadOnlyList<SonarAudioDevice> devices,
        string? selectedDeviceId)
    {
        _suppressSelectionChanged = true;
        try
        {
            var previousSelectedId = GetSelectedDeviceId(combo);
            combo.Items.Clear();

            foreach (var device in devices)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = device.FriendlyName,
                    Tag = device.Id,
                    ToolTip = device.FriendlyName
                });
            }

            if (combo.Items.Count == 0)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = "No devices found",
                    IsEnabled = false
                });
                combo.SelectedIndex = 0;
                return;
            }

            var targetId = !string.IsNullOrWhiteSpace(selectedDeviceId)
                ? selectedDeviceId
                : previousSelectedId;

            if (!string.IsNullOrWhiteSpace(targetId))
            {
                foreach (ComboBoxItem item in combo.Items)
                {
                    if (string.Equals(item.Tag as string, targetId, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = item;
                        return;
                    }
                }
            }

            // Keep current Sonar selection visible even if the device is temporarily missing.
            if (!string.IsNullOrWhiteSpace(selectedDeviceId))
            {
                var missing = new ComboBoxItem
                {
                    Content = "Unknown device",
                    Tag = selectedDeviceId
                };
                combo.Items.Insert(0, missing);
                combo.SelectedItem = missing;
                return;
            }

            combo.SelectedIndex = -1;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private static string? GetSelectedDeviceId(System.Windows.Controls.ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string deviceId } && !string.IsNullOrWhiteSpace(deviceId)
            ? deviceId
            : null;
}
