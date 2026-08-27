using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Mixing;

public sealed class MixerSnapshotCoordinator
{
    private static readonly string[] MasterProportionalChannels = SonarChannels.MasterProportional;

    private readonly SonarApiClient _apiClient;
    private readonly MixerControlRegistry _registry;
    private readonly Action<string> _setStatusText;
    private readonly HashSet<string> _enabledChannels = new(StringComparer.OrdinalIgnoreCase);

    private bool _isUpdatingFromApi;
    private bool _mixerSyncInProgress;
    private string? _cachedStatusText;
    private SonarMixerSnapshot? _cachedMixerSnapshot;

    public MixerSnapshotCoordinator(
        SonarApiClient apiClient,
        MixerControlRegistry registry,
        Action<string> setStatusText)
    {
        _apiClient = apiClient;
        _registry = registry;
        _setStatusText = setStatusText;
    }

    public bool IsUpdatingFromApi => _isUpdatingFromApi;
    public bool IsSyncInProgress => _mixerSyncInProgress;
    public SonarMixerSnapshot? CachedSnapshot => _cachedMixerSnapshot;

    public bool IsChannelEnabled(string channel) => _enabledChannels.Contains(channel);

    public bool HasCachedConnectionStatus() =>
        !string.IsNullOrWhiteSpace(_cachedStatusText) && _apiClient.IsConnected;

    public void RestoreOrShowConnectingStatus()
    {
        _setStatusText(HasCachedConnectionStatus()
            ? _cachedStatusText!
            : "Connecting to Sonar...");
    }

    public void ClearCachedStatusText()
    {
        _cachedStatusText = null;
        _cachedMixerSnapshot = null;
    }

    public void ApplyCachedSnapshotIfAvailable() =>
        ApplyUiState(_cachedMixerSnapshot, applyVolumes: true);

    public async Task<SonarMixerSnapshot?> FetchSnapshotAsync()
    {
        try
        {
            if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
            {
                return null;
            }

            return await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task SyncSnapshotAsync(bool applyVolumes = true)
    {
        if (_mixerSyncInProgress)
        {
            return;
        }

        _mixerSyncInProgress = true;
        try
        {
            var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(true);
            if (!_apiClient.IsConnected)
            {
                return;
            }

            ApplyUiState(snapshot, applyVolumes);
        }
        finally
        {
            _mixerSyncInProgress = false;
        }
    }

    public void ApplySnapshot(SonarMixerSnapshot snapshot, bool applyVolumes = true) =>
        ApplyUiState(snapshot, applyVolumes);

    public void ApplyProportionalChannelsUi(SonarMixerPath path, double oldMaster, double newMaster)
    {
        if (oldMaster <= 0.01)
        {
            return;
        }

        var ratio = newMaster / oldMaster;
        _isUpdatingFromApi = true;
        try
        {
            foreach (var channel in GetActiveProportionalChannels())
            {
                var channelSlider = _registry.FindSlider(channel, path);
                if (channelSlider is null)
                {
                    continue;
                }

                channelSlider.Value = Math.Clamp(Math.Round(channelSlider.Value * ratio, 1), 0, 100);
            }

            _registry.UpdateDisplayedValues();
        }
        finally
        {
            _isUpdatingFromApi = false;
        }
    }

    public void ApplyChannelSettingsToUi(
        IReadOnlyDictionary<string, SonarChannelSettings> settings,
        bool applyVolumes = true,
        Action? onLevelsChanged = null)
    {
        _isUpdatingFromApi = true;
        try
        {
            foreach (var (slider, binding) in _registry.SliderBindings)
            {
                if (!IsChannelEnabled(binding.Channel))
                {
                    continue;
                }

                if (!settings.TryGetValue(binding.Channel, out var channelSettings))
                {
                    continue;
                }

                var state = binding.Path == SonarMixerPath.Streaming
                    ? channelSettings.Streaming
                    : channelSettings.Monitoring;

                if (state is null)
                {
                    continue;
                }

                if (applyVolumes && state.Volume is float volume)
                {
                    slider.Value = Math.Round(volume * 100, 1);
                }

                if (_registry.FindMuteToggleForSlider(slider) is ToggleButton muteToggle)
                {
                    muteToggle.IsChecked = state.Muted == true;
                }

                if (_registry.FindMixToggleForSlider(slider) is ToggleButton mixToggle)
                {
                    mixToggle.IsChecked = state.MixIncluded != false;
                }

                _registry.UpdateSliderVisual(slider);
            }

            if (applyVolumes)
            {
                _registry.UpdateDisplayedValues();
            }
        }
        finally
        {
            _isUpdatingFromApi = false;
        }

        onLevelsChanged?.Invoke();
    }

    /// <summary>
    /// Optimistic single-channel UI update from MIDI / media keys without a full Sonar GET.
    /// </summary>
    public void ApplyExternalVolumeToUi(string channelId, SonarMixerPath path, float volume, bool isMuted)
    {
        var channel = SonarChannels.NormalizeChannel(channelId);
        var slider = _registry.FindSlider(channel, path);
        if (slider is null)
        {
            return;
        }

        _isUpdatingFromApi = true;
        try
        {
            slider.Value = Math.Clamp(Math.Round(volume * 100, 1), 0, 100);

            if (_registry.FindMuteToggleForSlider(slider) is ToggleButton muteToggle)
            {
                muteToggle.IsChecked = isMuted;
            }

            _registry.UpdateSliderVisual(slider);
            _registry.UpdateDisplayedValues();
        }
        finally
        {
            _isUpdatingFromApi = false;
        }
    }

    private void ApplyUiState(SonarMixerSnapshot? snapshot, bool applyVolumes)
    {
        if (snapshot is null)
        {
            return;
        }

        _cachedMixerSnapshot = snapshot;
        _enabledChannels.Clear();
        foreach (var channel in snapshot.EnabledChannels)
        {
            _enabledChannels.Add(channel);
        }

        ApplyChannelVisibility(snapshot);
        ApplyStreamerModeLayout(snapshot.IsStreamerMode);
        ApplyChannelSettingsToUi(snapshot.Channels, applyVolumes);

        if (!_apiClient.IsConnected)
        {
            ClearCachedStatusText();
            _setStatusText("Sonar API unavailable");
        }
        else
        {
            _cachedStatusText = BuildStatusText(snapshot);
            _setStatusText(_cachedStatusText);
        }
    }

    private string BuildStatusText(SonarMixerSnapshot snapshot)
    {
        if (!_apiClient.IsConnected)
        {
            return "Sonar API unavailable";
        }

        var portSuffix = _apiClient.Port is int port ? $" · port {port}" : string.Empty;
        var modeLabel = snapshot.IsStreamerMode ? "Streamer mode" : "Classic mode";
        var channelCount = snapshot.EnabledChannels.Count;
        return $"{modeLabel} · {channelCount} channels{portSuffix}";
    }

    private void ApplyChannelVisibility(SonarMixerSnapshot snapshot)
    {
        foreach (var (channel, section) in _registry.ChannelSections)
        {
            section.Visibility = snapshot.IsChannelEnabled(channel)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplyStreamerModeLayout(bool streamerMode)
    {
        var visibility = streamerMode ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in _registry.StreamerOnlyElements)
        {
            element.Visibility = visibility;
        }
    }

    private IEnumerable<string> GetActiveProportionalChannels()
    {
        foreach (var channel in MasterProportionalChannels)
        {
            if (IsChannelEnabled(channel))
            {
                yield return channel;
            }
        }
    }
}
