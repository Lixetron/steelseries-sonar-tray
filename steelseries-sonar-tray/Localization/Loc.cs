namespace SonarQuickMixer.Localization;

/// <summary>
/// Static facade for code-behind and services. XAML uses the <c>{loc:Tr Key}</c> markup extension.
/// </summary>
public static class Loc
{
    public static LocalizationManager Manager => LocalizationManager.Current;

    public static string Get(string key) => Manager.Get(key);

    public static string Format(string key, params object?[] args) => Manager.Format(key, args);

    /// <summary>Captures a token for later rendering (status lines / view-model properties).</summary>
    public static LocalizedText Text(string key, params object?[] args) => new(key, args);

    public static event EventHandler? LanguageChanged
    {
        add => Manager.LanguageChanged += value;
        remove => Manager.LanguageChanged -= value;
    }
}
