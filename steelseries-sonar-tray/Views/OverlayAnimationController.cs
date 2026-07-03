using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SonarQuickMixer.Views;

public sealed class OverlayAnimationController
{
    private const int ShowAnimationMs = 240;
    private const int HideAnimationMs = 180;
    private const int ViewTransitionMs = 220;
    private const double SlideDistanceDip = 24;

    private readonly Window _window;
    private readonly FrameworkElement _overlayRoot;

    public OverlayAnimationController(Window window, FrameworkElement overlayRoot)
    {
        _window = window;
        _overlayRoot = overlayRoot;
    }

    public int ShowAnimationDurationMs => ShowAnimationMs;
    public int HideAnimationDurationMs => HideAnimationMs;
    public int ViewTransitionDurationMs => ViewTransitionMs;
    public double SlideDistance => SlideDistanceDip;

    public void ClearSlideAnimations()
    {
        _window.BeginAnimation(Window.TopProperty, null);
        _overlayRoot.BeginAnimation(UIElement.OpacityProperty, null);
    }

    public void SetHiddenPose(double anchorLeft, double anchorTop)
    {
        ClearSlideAnimations();
        _window.Left = anchorLeft;
        _window.Top = anchorTop + SlideDistanceDip;
        _overlayRoot.Opacity = 0;
    }

    public Task AnimateSlideAsync(double toTop, double toOpacity, int durationMs, IEasingFunction easing)
    {
        ClearSlideAnimations();
        _overlayRoot.CacheMode = new BitmapCache(1.0);

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var fromTop = _window.Top;
        var fromOpacity = _overlayRoot.Opacity;

        var topAnimation = new DoubleAnimation
        {
            From = fromTop,
            To = toTop,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        Storyboard.SetTarget(topAnimation, _window);
        Storyboard.SetTargetProperty(topAnimation, new PropertyPath(Window.TopProperty));

        var opacityAnimation = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        Storyboard.SetTarget(opacityAnimation, _overlayRoot);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.Stop,
            Children = { topAnimation, opacityAnimation }
        };

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storyboard.Completed += (_, _) =>
        {
            _window.Top = toTop;
            _overlayRoot.Opacity = toOpacity;
            _overlayRoot.CacheMode = null;
            completion.TrySetResult();
        };
        storyboard.Begin();
        return completion.Task;
    }

    public double GetViewSlideDistance(FrameworkElement viewContentHost, double windowActualWidth)
    {
        viewContentHost.UpdateLayout();
        var width = viewContentHost.ActualWidth;
        if (width < 1)
        {
            width = Math.Max(windowActualWidth - 34, 320);
        }

        return width;
    }

    public void ResetViewZOrder(params FrameworkElement[] elements)
    {
        foreach (var element in elements)
        {
            System.Windows.Controls.Panel.SetZIndex(element, 0);
        }
    }

    public void SetViewSlideState(FrameworkElement element, double x)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        EnsureViewTransform(element).BeginAnimation(TranslateTransform.XProperty, null);
        element.Opacity = 1;
        EnsureViewTransform(element).X = x;
    }

    public void ResetViewSlideState(FrameworkElement element) => SetViewSlideState(element, 0);

    public Task AnimateViewSlideAsync(
        FrameworkElement element,
        double toX,
        int durationMs,
        IEasingFunction easing)
    {
        var transform = EnsureViewTransform(element);
        transform.BeginAnimation(TranslateTransform.XProperty, null);

        var animation = new DoubleAnimation
        {
            From = transform.X,
            To = toX,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        animation.Completed += OnCompleted;

        void OnCompleted(object? sender, EventArgs e)
        {
            animation.Completed -= OnCompleted;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = toX;
            completion.TrySetResult();
        }

        transform.BeginAnimation(TranslateTransform.XProperty, animation);
        return completion.Task;
    }

    private static TranslateTransform EnsureViewTransform(FrameworkElement element)
    {
        element.RenderTransformOrigin = new System.Windows.Point(0, 0);

        if (element.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }
}
