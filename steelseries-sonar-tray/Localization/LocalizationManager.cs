using System.ComponentModel;
using System.Globalization;

namespace SonarQuickMixer.Localization;

/// <summary>
/// Runtime language catalog. Raise <see cref="INotifyPropertyChanged"/> with the WPF indexer
/// notification name so every <c>{loc:Tr Key}</c> binding re-reads its value on language change.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    /// <summary>Language used when the active pack lacks a key (and the culture is unsupported).</summary>
    public const string DefaultCode = "en";

    /// <summary>Preference value that follows the Windows display language.</summary>
    public const string AutoCode = "auto";

    private readonly Dictionary<string, LanguagePack> _packs;
    private readonly LanguagePack? _fallback;
    private LanguagePack? _active;
    private string _preference = AutoCode;

    public LocalizationManager(IEnumerable<LanguagePack> packs)
    {
        _packs = packs
            .Where(p => !string.IsNullOrWhiteSpace(p.Info.Code))
            .GroupBy(p => p.Info.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        _fallback = _packs.TryGetValue(DefaultCode, out var fallback) ? fallback : null;
        _active = _fallback ?? _packs.Values.FirstOrDefault();
    }

    /// <summary>Process-wide catalog used by XAML bindings and the <see cref="Loc"/> facade.</summary>
    public static LocalizationManager Current { get; private set; } = CreateDefault();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    /// <summary>All installed languages, default language first, then alphabetically.</summary>
    public IReadOnlyList<LanguageInfo> Languages => _packs.Values
        .OrderBy(p => string.Equals(p.Info.Code, DefaultCode, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(p => p.Info.NativeName, StringComparer.CurrentCultureIgnoreCase)
        .Select(p => p.Info)
        .ToList();

    /// <summary>Saved preference: <see cref="AutoCode"/> or a concrete language code.</summary>
    public string Preference => _preference;

    /// <summary>Resolved language code (never <see cref="AutoCode"/>).</summary>
    public string LanguageCode => _active?.Info.Code ?? DefaultCode;

    public LanguageInfo? ActiveLanguage => _active?.Info;

    /// <summary>Indexer used by WPF bindings: <c>{Binding [key], Source={x:Static ...}}</c>.</summary>
    public string this[string key] => Get(key);

    public static LocalizationManager CreateDefault() =>
        new(LanguagePackLoader.LoadAll());

    /// <summary>Replaces <see cref="Current"/> (startup only) and applies the saved preference.</summary>
    public static LocalizationManager Initialize(string? preference)
    {
        Current = CreateDefault();
        Current.SetLanguage(preference);
        return Current;
    }

    /// <summary>
    /// Applies <paramref name="preference"/> (a language code or <see cref="AutoCode"/>).
    /// Unknown codes fall back to the Windows UI culture, then to <see cref="DefaultCode"/>.
    /// </summary>
    public void SetLanguage(string? preference)
    {
        var requested = string.IsNullOrWhiteSpace(preference) ? AutoCode : preference.Trim();

        var resolvedCode = string.Equals(requested, AutoCode, StringComparison.OrdinalIgnoreCase)
            ? ResolveSystemLanguageCode()
            : requested;

        if (!_packs.TryGetValue(resolvedCode, out var pack))
        {
            pack = _fallback ?? _packs.Values.FirstOrDefault();
            // Stale/unknown code: follow Windows again instead of pinning English forever.
            if (!string.Equals(requested, AutoCode, StringComparison.OrdinalIgnoreCase))
            {
                requested = AutoCode;
            }
        }

        var nextPreference = _packs.ContainsKey(requested) ? requested : AutoCode;
        var preferenceChanged = !string.Equals(_preference, nextPreference, StringComparison.OrdinalIgnoreCase);
        _preference = nextPreference;

        var languageChanged = !string.Equals(_active?.Info.Code, pack?.Info.Code, StringComparison.OrdinalIgnoreCase);
        _active = pack;

        if (preferenceChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preference)));
        }

        if (!languageChanged)
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageCode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveLanguage)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Looks up a token in the active language, then English, then returns the token.</summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (_active is not null && _active.Strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (_fallback is not null && _fallback.Strings.TryGetValue(key, out var fallbackValue) && !string.IsNullOrEmpty(fallbackValue))
        {
            return fallbackValue;
        }

        return key;
    }

    public string Format(string key, params object?[] args)
    {
        var template = Get(key);
        if (args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // A bad placeholder in a translation must not crash the tray app.
            return template;
        }
    }

    public bool HasKey(string key) =>
        _active?.Strings.ContainsKey(key) == true || _fallback?.Strings.ContainsKey(key) == true;

    /// <summary>Maps the current Windows display language to the closest installed pack.</summary>
    public string ResolveSystemLanguageCode()
    {
        var uiCulture = CultureInfo.CurrentUICulture;
        var candidates = new[]
        {
            uiCulture.Name,
            uiCulture.TwoLetterISOLanguageName,
            uiCulture.Parent.Name
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var exact = _packs.Keys.FirstOrDefault(code =>
                string.Equals(code, candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var sameLanguage = _packs.Values.FirstOrDefault(p =>
                string.Equals(p.Info.Culture, candidate, StringComparison.OrdinalIgnoreCase)
                || p.Info.Code.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase));
            if (sameLanguage is not null)
            {
                return sameLanguage.Info.Code;
            }
        }

        return _fallback?.Info.Code ?? _packs.Keys.FirstOrDefault() ?? DefaultCode;
    }
}
