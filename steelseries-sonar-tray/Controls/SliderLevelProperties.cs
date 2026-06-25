using System.Windows;
using System.Windows.Controls;

namespace SteelSeries.SonarTray.Controls;

public static class SliderLevelProperties
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.RegisterAttached(
            "Level",
            typeof(double),
            typeof(SliderLevelProperties),
            new PropertyMetadata(0d));

    public static double GetLevel(Slider slider) => (double)slider.GetValue(LevelProperty);

    public static void SetLevel(Slider slider, double value) => slider.SetValue(LevelProperty, value);
}
