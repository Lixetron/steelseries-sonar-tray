using System.Windows.Controls;
using SonarQuickMixer.Audio;
using SonarQuickMixer.Controls;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Mixing;

public sealed class AudioVisualizerCoordinator
{
    private const double VisualizerDisplayGain = 1.45;

    private static readonly string[] MasterProportionalChannels = SonarChannels.MasterProportional;

    private readonly MixerControlRegistry _registry;
    private readonly MixerSnapshotCoordinator _snapshotCoordinator;
    private readonly SonarChannelLevelMonitor _levelMonitor;
    private readonly Func<bool> _isVisualizerEnabled;
    private readonly Dictionary<string, double> _lastRawChannelLevels = new(StringComparer.OrdinalIgnoreCase);

    public AudioVisualizerCoordinator(
        MixerControlRegistry registry,
        MixerSnapshotCoordinator snapshotCoordinator,
        SonarChannelLevelMonitor levelMonitor,
        Func<bool> isVisualizerEnabled)
    {
        _registry = registry;
        _snapshotCoordinator = snapshotCoordinator;
        _levelMonitor = levelMonitor;
        _isVisualizerEnabled = isVisualizerEnabled;
    }

    public void PollAndRefreshLevels()
    {
        var levels = _levelMonitor.PollLevels();

        foreach (var (channel, level) in levels)
        {
            _lastRawChannelLevels[channel] = level;
        }

        RefreshAllSliderLevels();
    }

    public void ResetLevelMeters()
    {
        _registry.ResetLevelMeters(slider => SliderLevelProperties.SetLevel(slider, 0));
    }

    public void ClearCachedLevels() => _lastRawChannelLevels.Clear();

    public void RefreshDevices() => _levelMonitor.RefreshDevices();

    public void Suspend() => _levelMonitor.Suspend();

    public void RefreshAllSliderLevels()
    {
        foreach (var (channel, sliders) in _registry.ChannelSliders)
        {
            if (!_snapshotCoordinator.IsChannelEnabled(channel))
            {
                continue;
            }

            if (string.Equals(channel, "master", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawLevel = _lastRawChannelLevels.TryGetValue(channel, out var value) ? value : 0d;
            foreach (var slider in sliders)
            {
                ApplyCappedLevel(slider, rawLevel);
            }
        }

        if (!_registry.ChannelSliders.TryGetValue("master", out var masterSliders))
        {
            return;
        }

        foreach (var masterSlider in masterSliders)
        {
            if (!_registry.SliderBindings.TryGetValue(masterSlider, out var binding))
            {
                continue;
            }

            var mixPeak = ComputeMixPeak(binding.Path);
            ApplyCappedLevel(masterSlider, mixPeak);
        }
    }

    private double ComputeMixPeak(SonarMixerPath path)
    {
        var peak = 0d;

        foreach (var channel in MasterProportionalChannels)
        {
            if (!_snapshotCoordinator.IsChannelEnabled(channel))
            {
                continue;
            }

            var rawLevel = _lastRawChannelLevels.TryGetValue(channel, out var value) ? value : 0d;
            var channelSlider = _registry.FindSlider(channel, path);
            if (channelSlider is null)
            {
                continue;
            }

            if (_registry.IsSliderMuted(channelSlider) || _registry.IsSliderMixExcluded(channelSlider))
            {
                continue;
            }

            var volumeFactor = channelSlider.Value / 100d;
            peak = Math.Max(peak, rawLevel * volumeFactor);
        }

        return peak;
    }

    private void ApplyCappedLevel(Slider slider, double rawLevel)
    {
        if (!_isVisualizerEnabled())
        {
            SliderLevelProperties.SetLevel(slider, 0);
            return;
        }

        if (_registry.IsSliderMuted(slider) || _registry.IsSliderMixExcluded(slider))
        {
            SliderLevelProperties.SetLevel(slider, 0);
            return;
        }

        var volumeFactor = slider.Value / 100d;
        SliderLevelProperties.SetLevel(slider, MapVisualizerLevel(rawLevel, volumeFactor));
    }

    private static double MapVisualizerLevel(double rawLevel, double volumeFactor) =>
        Math.Min(rawLevel * VisualizerDisplayGain, 1d) * volumeFactor;
}
