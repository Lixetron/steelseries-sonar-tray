using System.Windows.Data;
using System.Windows.Markup;

namespace SonarQuickMixer.Localization;

/// <summary>
/// XAML token: <c>Text="{loc:Tr mixer.output}"</c>. Returns a one-way binding to the catalog
/// indexer, so all visible text refreshes in place when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            return string.Empty;
        }

        var binding = new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationManager.Current,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
