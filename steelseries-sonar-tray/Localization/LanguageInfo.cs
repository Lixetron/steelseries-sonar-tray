namespace SonarQuickMixer.Localization;

/// <summary>
/// Metadata for one translation pack. <see cref="NativeName"/> is what the language picker shows,
/// so translators do not have to know the English name of their own language.
/// </summary>
public sealed record LanguageInfo(string Code, string NativeName, string Culture);
