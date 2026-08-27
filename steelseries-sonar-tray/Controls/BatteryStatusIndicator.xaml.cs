using System.Windows;
using System.Windows.Media;

namespace SonarQuickMixer.Controls;

public partial class BatteryStatusIndicator : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IconKindProperty =
        DependencyProperty.Register(
            nameof(IconKind),
            typeof(BatteryIconKind),
            typeof(BatteryStatusIndicator),
            new PropertyMetadata(BatteryIconKind.Hidden, OnIconKindChanged));

    public static readonly DependencyProperty PercentTextProperty =
        DependencyProperty.Register(
            nameof(PercentText),
            typeof(string),
            typeof(BatteryStatusIndicator),
            new PropertyMetadata(string.Empty));

    public BatteryStatusIndicator()
    {
        InitializeComponent();
        ApplyIconKind(IconKind);
    }

    public BatteryIconKind IconKind
    {
        get => (BatteryIconKind)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string PercentText
    {
        get => (string)GetValue(PercentTextProperty);
        set => SetValue(PercentTextProperty, value);
    }

    private static void OnIconKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BatteryStatusIndicator indicator)
        {
            indicator.ApplyIconKind((BatteryIconKind)e.NewValue);
        }
    }

    private void ApplyIconKind(BatteryIconKind kind)
    {
        IconFull.Visibility = kind == BatteryIconKind.Full ? Visibility.Visible : Visibility.Collapsed;
        IconHalf.Visibility = kind == BatteryIconKind.Half ? Visibility.Visible : Visibility.Collapsed;
        IconLow.Visibility = kind == BatteryIconKind.Low ? Visibility.Visible : Visibility.Collapsed;
        IconEmpty.Visibility = kind == BatteryIconKind.Empty ? Visibility.Visible : Visibility.Collapsed;
        IconCharging.Visibility = kind == BatteryIconKind.Charging ? Visibility.Visible : Visibility.Collapsed;

        var brush = ResolveBrush(kind);
        if (Resources["BatteryTintBrush"] is SolidColorBrush tint)
        {
            // Freeze-safe: replace local brush instance used by StaticResource bindings.
            var next = brush.Clone();
            if (next.CanFreeze)
            {
                next.Freeze();
            }

            Resources["BatteryTintBrush"] = next;
        }

        PercentLabel.Foreground = brush;
    }

    private System.Windows.Media.Brush ResolveBrush(BatteryIconKind kind)
    {
        var key = kind switch
        {
            BatteryIconKind.Full => "BatteryFullBrush",
            BatteryIconKind.Half => "BatteryHalfBrush",
            BatteryIconKind.Low => "BatteryLowBrush",
            BatteryIconKind.Empty => "BatteryEmptyBrush",
            BatteryIconKind.Charging => "BatteryChargingBrush",
            _ => "TextSecondaryBrush"
        };

        return TryFindResource(key) as System.Windows.Media.Brush
               ?? new SolidColorBrush(Colors.Silver);
    }
}
