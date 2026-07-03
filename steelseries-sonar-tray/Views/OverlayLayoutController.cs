using System.Windows;
using System.Windows.Controls;

namespace SonarQuickMixer.Views;

public sealed class OverlayLayoutController
{
    private readonly Window _window;
    private readonly FrameworkElement _mixerHeaderPanel;
    private readonly FrameworkElement _channelsPanel;
    private readonly FrameworkElement _viewHeaderHost;
    private readonly FrameworkElement _viewContentHost;
    private readonly FrameworkElement _mixerTabPanel;
    private readonly FrameworkElement _settingsTabPanel;

    private double? _lockedOverlayHeight;
    private double? _lockedHeaderHostHeight;

    public OverlayLayoutController(
        Window window,
        FrameworkElement mixerHeaderPanel,
        FrameworkElement channelsPanel,
        FrameworkElement viewHeaderHost,
        FrameworkElement viewContentHost,
        FrameworkElement mixerTabPanel,
        FrameworkElement settingsTabPanel)
    {
        _window = window;
        _mixerHeaderPanel = mixerHeaderPanel;
        _channelsPanel = channelsPanel;
        _viewHeaderHost = viewHeaderHost;
        _viewContentHost = viewContentHost;
        _mixerTabPanel = mixerTabPanel;
        _settingsTabPanel = settingsTabPanel;
    }

    public void LockOverlayHeight()
    {
        var measuredHeight = Math.Clamp(MeasureMixerLayoutHeight(), _window.MinHeight, _window.MaxHeight);
        if (_lockedOverlayHeight is null || measuredHeight > _lockedOverlayHeight)
        {
            _lockedOverlayHeight = measuredHeight;
        }

        ApplyLockedOverlaySize();
    }

    public void ReleaseOverlayHeight()
    {
        _lockedOverlayHeight = null;
        _lockedHeaderHostHeight = null;
        _window.SizeToContent = SizeToContent.Height;
        _window.MinHeight = 200;
        _window.MaxHeight = 700;
        _viewHeaderHost.MinHeight = 0;
        _viewContentHost.MinHeight = 0;
        _viewContentHost.MaxHeight = double.PositiveInfinity;
        _mixerTabPanel.MinHeight = 0;
        _mixerTabPanel.MaxHeight = double.PositiveInfinity;
        _settingsTabPanel.MinHeight = 0;
        _settingsTabPanel.MaxHeight = double.PositiveInfinity;
    }

    private double MeasureMixerLayoutHeight()
    {
        _window.UpdateLayout();

        var contentWidth = Math.Max(_window.ActualWidth, _window.Width) - 34;
        if (contentWidth < 200)
        {
            contentWidth = 370;
        }

        _mixerHeaderPanel.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));
        _channelsPanel.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));

        const double chromeHeight = 46;
        return chromeHeight + _mixerHeaderPanel.DesiredSize.Height + _channelsPanel.DesiredSize.Height;
    }

    private void ApplyLockedOverlaySize()
    {
        if (_lockedOverlayHeight is not double height)
        {
            return;
        }

        _window.SizeToContent = SizeToContent.Manual;
        _window.Height = height;
        _window.MinHeight = height;
        _window.MaxHeight = height;

        _window.UpdateLayout();
        var headerHeight = ResolveLockedHeaderHostHeight();
        _viewHeaderHost.MinHeight = headerHeight;

        var contentWidth = Math.Max(_window.ActualWidth, _window.Width) - 34;
        if (contentWidth < 200)
        {
            contentWidth = 370;
        }

        _channelsPanel.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));
        var contentAreaHeight = _channelsPanel.DesiredSize.Height;

        _viewContentHost.MinHeight = contentAreaHeight;
        _viewContentHost.MaxHeight = contentAreaHeight;
        _mixerTabPanel.MinHeight = 0;
        _mixerTabPanel.MaxHeight = double.PositiveInfinity;
        _settingsTabPanel.MinHeight = contentAreaHeight;
        _settingsTabPanel.MaxHeight = contentAreaHeight;
    }

    private double ResolveLockedHeaderHostHeight()
    {
        var headerHeight = _mixerHeaderPanel.ActualHeight;
        if (headerHeight < 1)
        {
            var contentWidth = Math.Max(_window.ActualWidth, _window.Width) - 34;
            if (contentWidth < 200)
            {
                contentWidth = 370;
            }

            _mixerHeaderPanel.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));
            headerHeight = _mixerHeaderPanel.DesiredSize.Height;
        }

        if (_lockedHeaderHostHeight is null || headerHeight > _lockedHeaderHostHeight)
        {
            _lockedHeaderHostHeight = headerHeight;
        }

        return _lockedHeaderHostHeight.Value;
    }
}
