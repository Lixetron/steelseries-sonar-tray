using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SonarQuickMixer.Controls;

public static class SmoothScrolling
{
    private const double Smoothness = 14.0;
    private const double StopThreshold = 0.5;
    private const double MaxFrameSeconds = 0.05;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrolling),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty AnimationStateProperty =
        DependencyProperty.RegisterAttached(
            "AnimationState",
            typeof(AnimationState),
            typeof(SmoothScrolling),
            new PropertyMetadata(null));

    private static readonly MouseWheelEventHandler MouseWheelHandler = OnPreviewMouseWheel;

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, MouseWheelHandler);

        if (e.NewValue is true)
        {
            scrollViewer.AddHandler(UIElement.PreviewMouseWheelEvent, MouseWheelHandler, handledEventsToo: false);
            scrollViewer.Unloaded -= OnScrollViewerUnloaded;
            scrollViewer.Unloaded += OnScrollViewerUnloaded;
        }
        else
        {
            StopAnimation(scrollViewer);
            scrollViewer.Unloaded -= OnScrollViewerUnloaded;
        }
    }

    private static void OnScrollViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            StopAnimation(scrollViewer);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (ShouldSkipSmoothScroll(e.OriginalSource as DependencyObject, scrollViewer))
        {
            return;
        }

        scrollViewer.UpdateLayout();
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        e.Handled = true;

        var state = GetOrCreateState(scrollViewer);
        if (!state.IsAnimating)
        {
            state.TargetOffset = scrollViewer.VerticalOffset;
        }

        var scrollDelta = -(e.Delta / 120.0) * SystemParameters.WheelScrollLines * 16.0;
        state.TargetOffset = Math.Clamp(
            state.TargetOffset + scrollDelta,
            0,
            scrollViewer.ScrollableHeight);

        StartAnimation(scrollViewer, state);
    }

    private static bool ShouldSkipSmoothScroll(DependencyObject? source, ScrollViewer scrollViewer)
    {
        for (var current = source; current != null && !ReferenceEquals(current, scrollViewer); current = VisualTreeHelper.GetParent(current))
        {
            if (current is Slider)
            {
                return true;
            }
        }

        return false;
    }

    private static AnimationState GetOrCreateState(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(AnimationStateProperty) is AnimationState state)
        {
            return state;
        }

        state = new AnimationState();
        scrollViewer.SetValue(AnimationStateProperty, state);
        return state;
    }

    private static void StartAnimation(ScrollViewer scrollViewer, AnimationState state)
    {
        if (state.IsAnimating)
        {
            return;
        }

        state.IsAnimating = true;
        state.LastFrame = DateTime.UtcNow;
        state.RenderingHandler = (_, _) => OnRendering(scrollViewer, state);
        CompositionTarget.Rendering += state.RenderingHandler;
    }

    private static void OnRendering(ScrollViewer scrollViewer, AnimationState state)
    {
        var now = DateTime.UtcNow;
        var deltaSeconds = (now - state.LastFrame).TotalSeconds;
        state.LastFrame = now;

        if (deltaSeconds > MaxFrameSeconds)
        {
            deltaSeconds = MaxFrameSeconds;
        }

        scrollViewer.UpdateLayout();
        var maxOffset = scrollViewer.ScrollableHeight;
        state.TargetOffset = Math.Clamp(state.TargetOffset, 0, maxOffset);

        var current = scrollViewer.VerticalOffset;
        var diff = state.TargetOffset - current;

        if (Math.Abs(diff) <= StopThreshold)
        {
            scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
            StopAnimation(scrollViewer);
            return;
        }

        var step = diff * (1 - Math.Exp(-Smoothness * deltaSeconds));
        scrollViewer.ScrollToVerticalOffset(current + step);
    }

    private static void StopAnimation(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(AnimationStateProperty) is not AnimationState state)
        {
            return;
        }

        if (state.RenderingHandler is not null)
        {
            CompositionTarget.Rendering -= state.RenderingHandler;
            state.RenderingHandler = null;
        }

        state.IsAnimating = false;
    }

    private sealed class AnimationState
    {
        public double TargetOffset;
        public bool IsAnimating;
        public DateTime LastFrame;
        public EventHandler? RenderingHandler;
    }
}
