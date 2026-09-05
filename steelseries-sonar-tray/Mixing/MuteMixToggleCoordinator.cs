using System.Windows.Controls.Primitives;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Mixing;

/// <summary>
/// Applies mute/mix-included toggle clicks to Sonar, with optimistic UI revert on failure.
/// Mirrors <see cref="VolumeSendCoordinator"/>'s shape so the mixer window's slider and
/// toggle-button paths follow the same pattern instead of the toggles calling the API directly.
/// </summary>
public sealed class MuteMixToggleCoordinator
{
    private readonly SonarApiClient _apiClient;
    private readonly MixerControlRegistry _mixerRegistry;
    private readonly Func<bool> _isUpdatingFromApi;
    private readonly Action<string> _setStatusText;
    private readonly Action<IReadOnlyDictionary<string, SonarChannelSettings>> _applyChannelSettings;
    private readonly Func<Task> _syncMixerSnapshot;
    private readonly Action _refreshAllSliderLevels;

    public MuteMixToggleCoordinator(
        SonarApiClient apiClient,
        MixerControlRegistry mixerRegistry,
        Func<bool> isUpdatingFromApi,
        Action<string> setStatusText,
        Action<IReadOnlyDictionary<string, SonarChannelSettings>> applyChannelSettings,
        Func<Task> syncMixerSnapshot,
        Action refreshAllSliderLevels)
    {
        _apiClient = apiClient;
        _mixerRegistry = mixerRegistry;
        _isUpdatingFromApi = isUpdatingFromApi;
        _setStatusText = setStatusText;
        _applyChannelSettings = applyChannelSettings;
        _syncMixerSnapshot = syncMixerSnapshot;
        _refreshAllSliderLevels = refreshAllSliderLevels;
    }

    public async Task ToggleMuteAsync(ToggleButton muteToggle)
    {
        if (_isUpdatingFromApi() || !_mixerRegistry.MuteBindings.TryGetValue(muteToggle, out var binding))
        {
            return;
        }

        var muted = muteToggle.IsChecked == true;
        var linkedSlider = _mixerRegistry.FindSliderForMuteToggle(muteToggle);
        if (linkedSlider is not null)
        {
            _mixerRegistry.UpdateSliderVisual(linkedSlider);
        }

        try
        {
            var updatedSettings = await _apiClient.SetMuteAsync(binding.Channel, muted, binding.Path).ConfigureAwait(true);
            if (updatedSettings is null)
            {
                muteToggle.IsChecked = !muted;
                if (linkedSlider is not null)
                {
                    _mixerRegistry.UpdateSliderVisual(linkedSlider);
                }

                _setStatusText("Failed to update mute");
                return;
            }

            _applyChannelSettings(updatedSettings);
            await _syncMixerSnapshot().ConfigureAwait(true);
        }
        catch (Exception)
        {
            muteToggle.IsChecked = !muted;
            if (linkedSlider is not null)
            {
                _mixerRegistry.UpdateSliderVisual(linkedSlider);
            }

            _setStatusText("Failed to update mute");
        }
    }

    public async Task ToggleMixAsync(ToggleButton mixToggle)
    {
        if (_isUpdatingFromApi() || !_mixerRegistry.MixBindings.TryGetValue(mixToggle, out var binding))
        {
            return;
        }

        var included = mixToggle.IsChecked == true;
        var linkedSlider = _mixerRegistry.FindSliderForMixToggle(mixToggle);
        if (linkedSlider is not null)
        {
            _mixerRegistry.UpdateSliderVisual(linkedSlider);
            _refreshAllSliderLevels();
        }

        try
        {
            var updatedSettings = await _apiClient
                .SetMixIncludedAsync(binding.Channel, included, binding.Path)
                .ConfigureAwait(true);

            if (updatedSettings is null)
            {
                mixToggle.IsChecked = !included;
                if (linkedSlider is not null)
                {
                    _mixerRegistry.UpdateSliderVisual(linkedSlider);
                    _refreshAllSliderLevels();
                }

                _setStatusText("Failed to update mix routing");
                return;
            }

            _applyChannelSettings(updatedSettings);
            await _syncMixerSnapshot().ConfigureAwait(true);
        }
        catch (Exception)
        {
            mixToggle.IsChecked = !included;
            if (linkedSlider is not null)
            {
                _mixerRegistry.UpdateSliderVisual(linkedSlider);
                _refreshAllSliderLevels();
            }

            _setStatusText("Failed to update mix routing");
        }
    }
}
