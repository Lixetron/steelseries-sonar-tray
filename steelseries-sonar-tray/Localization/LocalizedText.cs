namespace SonarQuickMixer.Localization;

/// <summary>
/// A token plus format arguments that resolves to text when read. Store it in view models
/// (status lines, hints) so a language switch re-renders text that was already displayed.
/// </summary>
public readonly struct LocalizedText
{
    private readonly string _key;
    private readonly object?[]? _args;

    public LocalizedText(string key, params object?[] args)
    {
        _key = key;
        _args = args;
    }

    public string Key => _key;

    public override string ToString() =>
        string.IsNullOrEmpty(_key)
            ? string.Empty
            : Loc.Format(_key, _args ?? []);

    public static implicit operator LocalizedText(string key) => new(key);
}
