using System.Collections.Generic;

namespace SonarQuickMixer.Localization;

/// <summary>One parsed language file: metadata plus flat key/value strings.</summary>
public sealed class LanguagePack
{
    public LanguagePack(
        LanguageInfo info,
        IReadOnlyDictionary<string, string> strings,
        string source)
    {
        Info = info;
        Strings = strings;
        Source = source;
    }

    public LanguageInfo Info { get; }

    public IReadOnlyDictionary<string, string> Strings { get; }

    /// <summary>Embedded resource name or absolute file path — useful in diagnostics/tests.</summary>
    public string Source { get; }
}
