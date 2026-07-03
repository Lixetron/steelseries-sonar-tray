using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Mixing;

public sealed class MixerControlRegistry
{
    private readonly Dictionary<Slider, (string Channel, SonarMixerPath Path)> _sliderBindings = new();
    private readonly Dictionary<Slider, TextBlock> _sliderValueLabels = new();
    private readonly Dictionary<ToggleButton, (string Channel, SonarMixerPath Path)> _muteBindings = new();
    private readonly Dictionary<ToggleButton, (string Channel, SonarMixerPath Path)> _mixBindings = new();
    private readonly Dictionary<Slider, ToggleButton> _sliderMuteToggles = new();
    private readonly Dictionary<Slider, ToggleButton> _sliderMixToggles = new();
    private readonly Dictionary<string, List<Slider>> _channelSliders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _channelSections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FrameworkElement> _streamerOnlyElements = [];

    public IReadOnlyDictionary<Slider, (string Channel, SonarMixerPath Path)> SliderBindings => _sliderBindings;
    public IReadOnlyDictionary<Slider, TextBlock> SliderValueLabels => _sliderValueLabels;
    public IReadOnlyDictionary<ToggleButton, (string Channel, SonarMixerPath Path)> MuteBindings => _muteBindings;
    public IReadOnlyDictionary<ToggleButton, (string Channel, SonarMixerPath Path)> MixBindings => _mixBindings;
    public IReadOnlyDictionary<string, List<Slider>> ChannelSliders => _channelSliders;
    public IReadOnlyDictionary<string, FrameworkElement> ChannelSections => _channelSections;
    public IReadOnlyList<FrameworkElement> StreamerOnlyElements => _streamerOnlyElements;

    public void RegisterChannel(
        string channel,
        ToggleButton monitorMuteToggle,
        Slider monitorSlider,
        TextBlock monitorValueLabel,
        ToggleButton streamMuteToggle,
        Slider streamSlider,
        TextBlock streamValueLabel,
        FrameworkElement? streamerMonitorIndicator,
        FrameworkElement streamRow,
        ToggleButton? monitorMixToggle = null,
        ToggleButton? streamMixToggle = null)
    {
        RegisterMixerRow(channel, SonarMixerPath.Monitoring, monitorMuteToggle, monitorSlider, monitorValueLabel, monitorMixToggle);
        RegisterMixerRow(channel, SonarMixerPath.Streaming, streamMuteToggle, streamSlider, streamValueLabel, streamMixToggle);

        if (streamerMonitorIndicator is not null)
        {
            _streamerOnlyElements.Add(streamerMonitorIndicator);
        }

        _streamerOnlyElements.Add(streamRow);
    }

    public void RegisterChannelSection(string channel, FrameworkElement section) =>
        _channelSections[channel] = section;

    public Slider? FindSlider(string channel, SonarMixerPath path)
    {
        foreach (var (slider, binding) in _sliderBindings)
        {
            if (string.Equals(binding.Channel, channel, StringComparison.OrdinalIgnoreCase) && binding.Path == path)
            {
                return slider;
            }
        }

        return null;
    }

    public ToggleButton? FindMuteToggleForSlider(Slider slider) =>
        _sliderMuteToggles.TryGetValue(slider, out var toggle) ? toggle : null;

    public ToggleButton? FindMixToggleForSlider(Slider slider) =>
        _sliderMixToggles.TryGetValue(slider, out var toggle) ? toggle : null;

    public Slider? FindSliderForMuteToggle(ToggleButton muteToggle) =>
        _sliderMuteToggles.FirstOrDefault(pair => pair.Value == muteToggle).Key;

    public Slider? FindSliderForMixToggle(ToggleButton mixToggle) =>
        _sliderMixToggles.FirstOrDefault(pair => pair.Value == mixToggle).Key;

    public bool IsSliderMuted(Slider slider) =>
        _sliderMuteToggles.TryGetValue(slider, out var muteToggle) && muteToggle.IsChecked == true;

    public bool IsSliderMixExcluded(Slider slider) =>
        _sliderMixToggles.TryGetValue(slider, out var mixToggle) && mixToggle.IsChecked != true;

    public void UpdateSliderVisual(Slider slider) =>
        slider.Opacity = IsSliderMuted(slider) || IsSliderMixExcluded(slider) ? 0.45 : 1.0;

    public void UpdateDisplayedValues()
    {
        foreach (var (slider, label) in _sliderValueLabels)
        {
            label.Text = $"{slider.Value:0}%";
        }
    }

    public void ResetLevelMeters(Action<Slider> resetLevel)
    {
        foreach (var sliders in _channelSliders.Values)
        {
            foreach (var slider in sliders)
            {
                resetLevel(slider);
            }
        }
    }

    private void RegisterMixerRow(
        string channel,
        SonarMixerPath path,
        ToggleButton muteToggle,
        Slider slider,
        TextBlock valueLabel,
        ToggleButton? mixToggle = null)
    {
        _muteBindings[muteToggle] = (channel, path);
        _sliderBindings[slider] = (channel, path);
        _sliderValueLabels[slider] = valueLabel;
        _sliderMuteToggles[slider] = muteToggle;

        if (mixToggle is not null)
        {
            _mixBindings[mixToggle] = (channel, path);
            _sliderMixToggles[slider] = mixToggle;
            _streamerOnlyElements.Add(mixToggle);
        }

        if (!_channelSliders.TryGetValue(channel, out var sliders))
        {
            sliders = [];
            _channelSliders[channel] = sliders;
        }

        sliders.Add(slider);
    }
}
