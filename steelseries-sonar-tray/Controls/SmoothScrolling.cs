using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfCursors = System.Windows.Input.Cursors;
using WpfPoint = System.Windows.Point;

namespace SonarQuickMixer.Controls;

/// <summary>
/// Smooth wheel scrolling for <see cref="ScrollViewer"/>, plus Shift+wheel horizontal scroll
/// and middle-mouse drag panning (grab-to-scroll).
/// </summary>
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
    private static readonly MouseButtonEventHandler MiddleDownHandler = OnPreviewMiddleDown;
    private static readonly System.Windows.Input.MouseEventHandler MouseMoveHandler = OnPreviewMouseMove;
    private static readonly MouseButtonEventHandler MiddleUpHandler = OnPreviewMiddleUp;
    private static readonly System.Windows.Input.MouseEventHandler LostCaptureHandler = OnLostMouseCapture;

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
        {
            return;
        }

        DetachHandlers(scrollViewer);

        if (e.NewValue is true)
        {
            scrollViewer.AddHandler(UIElement.PreviewMouseWheelEvent, MouseWheelHandler, handledEventsToo: false);
            scrollViewer.AddHandler(UIElement.PreviewMouseDownEvent, MiddleDownHandler, handledEventsToo: true);
            scrollViewer.AddHandler(UIElement.PreviewMouseMoveEvent, MouseMoveHandler, handledEventsToo: true);
            scrollViewer.AddHandler(UIElement.PreviewMouseUpEvent, MiddleUpHandler, handledEventsToo: true);
            scrollViewer.AddHandler(UIElement.LostMouseCaptureEvent, LostCaptureHandler, handledEventsToo: false);
            scrollViewer.Unloaded -= OnScrollViewerUnloaded;
            scrollViewer.Unloaded += OnScrollViewerUnloaded;
        }
        else
        {
            EndPan(scrollViewer);
            StopAnimation(scrollViewer);
            scrollViewer.Unloaded -= OnScrollViewerUnloaded;
        }
    }

    private static void DetachHandlers(ScrollViewer scrollViewer)
    {
        scrollViewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, MouseWheelHandler);
        scrollViewer.RemoveHandler(UIElement.PreviewMouseDownEvent, MiddleDownHandler);
        scrollViewer.RemoveHandler(UIElement.PreviewMouseMoveEvent, MouseMoveHandler);
        scrollViewer.RemoveHandler(UIElement.PreviewMouseUpEvent, MiddleUpHandler);
        scrollViewer.RemoveHandler(UIElement.LostMouseCaptureEvent, LostCaptureHandler);
    }

    private static void OnScrollViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            EndPan(scrollViewer);
            StopAnimation(scrollViewer);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        // Ctrl+wheel is reserved for blueprint/map zoom (handled by the host window).
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            return;
        }

        if (ShouldSkipSmoothScroll(e.OriginalSource as DependencyObject, scrollViewer))
        {
            return;
        }

        scrollViewer.UpdateLayout();

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var canVertical = scrollViewer.ScrollableHeight > 0;
        var canHorizontal = scrollViewer.ScrollableWidth > 0;

        // Shift+wheel → horizontal. If only one axis can scroll, use that axis.
        var scrollHorizontal = shift
            ? canHorizontal
            : !canVertical && canHorizontal;

        var scrollVertical = !shift && canVertical;

        if (!scrollHorizontal && !scrollVertical)
        {
            // Shift held but no horizontal room — don't steal the event for a no-op.
            if (shift && canVertical)
            {
                scrollVertical = true;
            }
            else
            {
                return;
            }
        }

        e.Handled = true;

        var state = GetOrCreateState(scrollViewer);
        if (state.IsPanning)
        {
            EndPan(scrollViewer);
        }

        if (!state.IsAnimating)
        {
            state.TargetVertical = scrollViewer.VerticalOffset;
            state.TargetHorizontal = scrollViewer.HorizontalOffset;
        }

        var scrollDelta = -(e.Delta / 120.0) * SystemParameters.WheelScrollLines * 16.0;

        if (scrollHorizontal)
        {
            state.TargetHorizontal = Math.Clamp(
                state.TargetHorizontal + scrollDelta,
                0,
                scrollViewer.ScrollableWidth);
        }

        if (scrollVertical)
        {
            state.TargetVertical = Math.Clamp(
                state.TargetVertical + scrollDelta,
                0,
                scrollViewer.ScrollableHeight);
        }

        StartAnimation(scrollViewer, state);
    }

    private static void OnPreviewMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer
            || e.ChangedButton != MouseButton.Middle
            || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        scrollViewer.UpdateLayout();
        if (scrollViewer.ScrollableWidth <= 0 && scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var state = GetOrCreateState(scrollViewer);
        StopAnimation(scrollViewer);

        state.IsPanning = true;
        state.PanStart = e.GetPosition(scrollViewer);
        state.PanStartVertical = scrollViewer.VerticalOffset;
        state.PanStartHorizontal = scrollViewer.HorizontalOffset;
        state.TargetVertical = state.PanStartVertical;
        state.TargetHorizontal = state.PanStartHorizontal;

        scrollViewer.CaptureMouse();
        scrollViewer.Cursor = WpfCursors.SizeAll;
        e.Handled = true;
    }

    private static void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var state = scrollViewer.GetValue(AnimationStateProperty) as AnimationState;
        if (state is not { IsPanning: true })
        {
            return;
        }

        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            EndPan(scrollViewer);
            return;
        }

        scrollViewer.UpdateLayout();
        var pos = e.GetPosition(scrollViewer);
        var dx = pos.X - state.PanStart.X;
        var dy = pos.Y - state.PanStart.Y;

        // Grab-and-drag: content follows the cursor (scroll opposite to mouse delta).
        var nextH = Math.Clamp(state.PanStartHorizontal - dx, 0, scrollViewer.ScrollableWidth);
        var nextV = Math.Clamp(state.PanStartVertical - dy, 0, scrollViewer.ScrollableHeight);
        scrollViewer.ScrollToHorizontalOffset(nextH);
        scrollViewer.ScrollToVerticalOffset(nextV);
        state.TargetHorizontal = nextH;
        state.TargetVertical = nextV;
        e.Handled = true;
    }

    private static void OnPreviewMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        if (scrollViewer.GetValue(AnimationStateProperty) is AnimationState { IsPanning: true })
        {
            EndPan(scrollViewer);
            e.Handled = true;
        }
    }

    private static void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            EndPan(scrollViewer);
        }
    }

    private static void EndPan(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(AnimationStateProperty) is not AnimationState state || !state.IsPanning)
        {
            if (scrollViewer.IsMouseCaptured)
            {
                scrollViewer.ReleaseMouseCapture();
            }

            return;
        }

        state.IsPanning = false;
        scrollViewer.ClearValue(FrameworkElement.CursorProperty);
        if (scrollViewer.IsMouseCaptured)
        {
            scrollViewer.ReleaseMouseCapture();
        }
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
        state.TargetVertical = Math.Clamp(state.TargetVertical, 0, scrollViewer.ScrollableHeight);
        state.TargetHorizontal = Math.Clamp(state.TargetHorizontal, 0, scrollViewer.ScrollableWidth);

        var verticalDone = StepAxis(
            scrollViewer.VerticalOffset,
            state.TargetVertical,
            deltaSeconds,
            out var nextVertical);
        var horizontalDone = StepAxis(
            scrollViewer.HorizontalOffset,
            state.TargetHorizontal,
            deltaSeconds,
            out var nextHorizontal);

        scrollViewer.ScrollToVerticalOffset(nextVertical);
        scrollViewer.ScrollToHorizontalOffset(nextHorizontal);

        if (verticalDone && horizontalDone)
        {
            StopAnimation(scrollViewer);
        }
    }

    private static bool StepAxis(double current, double target, double deltaSeconds, out double next)
    {
        var diff = target - current;
        if (Math.Abs(diff) <= StopThreshold)
        {
            next = target;
            return true;
        }

        next = current + diff * (1 - Math.Exp(-Smoothness * deltaSeconds));
        return false;
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
        public double TargetVertical;
        public double TargetHorizontal;
        public bool IsAnimating;
        public bool IsPanning;
        public WpfPoint PanStart;
        public double PanStartVertical;
        public double PanStartHorizontal;
        public DateTime LastFrame;
        public EventHandler? RenderingHandler;
    }
}
