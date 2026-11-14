using System.IO;
using System.Text.Json;
using SonarQuickMixer.Localization;

namespace SonarQuickMixer.Tests.Localization;

public sealed class LanguageCatalogTests
{
    [Fact]
    public void EmbeddedCatalogs_LoadEnglishAndRussian()
    {
        var packs = LanguagePackLoader.LoadEmbedded().ToList();

        Assert.Contains(packs, p => p.Info.Code == "en");
        Assert.Contains(packs, p => p.Info.Code == "ru");
        Assert.Equal("English", packs.Single(p => p.Info.Code == "en").Info.NativeName);
        Assert.Equal("Русский", packs.Single(p => p.Info.Code == "ru").Info.NativeName);
    }

    [Fact]
    public void AllCatalogs_HaveTheSameKeysAndNoEmptyValues()
    {
        var packs = LanguagePackLoader.LoadEmbedded().ToList();
        var reference = packs.Single(p => p.Info.Code == LocalizationManager.DefaultCode);

        foreach (var pack in packs)
        {
            var missing = reference.Strings.Keys.Except(pack.Strings.Keys).ToList();
            var extra = pack.Strings.Keys.Except(reference.Strings.Keys).ToList();

            Assert.True(missing.Count == 0, $"{pack.Info.Code} is missing: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"{pack.Info.Code} has unknown keys: {string.Join(", ", extra)}");

            var empty = pack.Strings.Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key)
                .ToList();
            Assert.True(empty.Count == 0, $"{pack.Info.Code} has empty values: {string.Join(", ", empty)}");
        }
    }

    [Fact]
    public void RussianCatalog_KeepsEveryFormatPlaceholder()
    {
        var packs = LanguagePackLoader.LoadEmbedded().ToList();
        var english = packs.Single(p => p.Info.Code == "en");
        var russian = packs.Single(p => p.Info.Code == "ru");

        foreach (var (key, englishText) in english.Strings)
        {
            var englishIndexes = ExtractPlaceholderIndexes(englishText);
            var russianIndexes = ExtractPlaceholderIndexes(russian.Strings[key]);

            Assert.True(
                englishIndexes.SetEquals(russianIndexes),
                $"{key}: placeholders differ (en: {string.Join(",", englishIndexes)}, ru: {string.Join(",", russianIndexes)})");
        }
    }

    private static HashSet<int> ExtractPlaceholderIndexes(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)\}")
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToHashSet();
}

public sealed class LocalizationManagerTests
{
    private static LocalizationManager CreateManager(out LanguagePack english)
    {
        english = CreatePack("en", "English", new Dictionary<string, string>
        {
            ["greeting"] = "Hello",
            ["withArg"] = "Hello, {0}!",
            ["onlyEnglish"] = "English only"
        });

        var russian = CreatePack("ru", "Русский", new Dictionary<string, string>
        {
            ["greeting"] = "Привет",
            ["withArg"] = "Привет, {0}!"
        });

        return new LocalizationManager([english, russian]);
    }

    private static LanguagePack CreatePack(string code, string nativeName, Dictionary<string, string> strings) =>
        new(
            new LanguageInfo(code, nativeName, code),
            strings,
            $"test:{code}");

    [Fact]
    public void MissingKey_FallsBackToEnglish_ThenToToken()
    {
        var manager = CreateManager(out _);
        manager.SetLanguage("ru");

        Assert.Equal("Привет", manager.Get("greeting"));
        Assert.Equal("English only", manager.Get("onlyEnglish"));
        Assert.Equal("not.a.token", manager.Get("not.a.token"));
    }

    [Fact]
    public void SetLanguage_NotifiesBindingsAndLanguageChanged()
    {
        var manager = CreateManager(out _);
        var propertyNames = new List<string?>();
        var languageChanges = 0;
        manager.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);
        manager.LanguageChanged += (_, _) => languageChanges++;

        manager.SetLanguage("ru");

        Assert.Contains("Item[]", propertyNames);
        Assert.Equal(1, languageChanges);
        Assert.Equal("ru", manager.LanguageCode);
    }

    [Fact]
    public void UnknownLanguage_FallsBackToEnglish()
    {
        var manager = CreateManager(out _);
        manager.SetLanguage("zz");

        Assert.Equal("en", manager.LanguageCode);
        Assert.Equal(LocalizationManager.AutoCode, manager.Preference);
        Assert.Equal("Hello", manager.Get("greeting"));
    }

    [Fact]
    public void Format_UsesActiveLanguageAndSurvivesBadPlaceholder()
    {
        var manager = CreateManager(out _);
        manager.SetLanguage("ru");

        Assert.Equal("Привет, Dmitry!", manager.Format("withArg", "Dmitry"));

        var broken = new LocalizationManager([
            CreatePack("en", "English", new Dictionary<string, string> { ["bad"] = "Hi {1}" })
        ]);
        Assert.Equal("Hi {1}", broken.Format("bad", "only-one-arg"));
    }

    [Fact]
    public void ExternalPack_OverridesEmbeddedLanguage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sqm-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                code = "ru",
                nativeName = "Русский (тест)",
                @strings = new Dictionary<string, string>
                {
                    ["app.name"] = "Тестовое имя"
                }
            });
            File.WriteAllText(Path.Combine(directory, "ru.json"), json);

            var packs = LanguagePackLoader.LoadAll(directory);
            var russian = packs.Single(p => p.Info.Code == "ru");

            Assert.Equal("Русский (тест)", russian.Info.NativeName);
            Assert.Equal("Тестовое имя", russian.Strings["app.name"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Parse_RejectsMalformedPack()
    {
        Assert.Null(LanguagePackLoader.Parse("{}", "test"));
        Assert.Null(LanguagePackLoader.Parse("not json", "test"));
        Assert.Null(LanguagePackLoader.Parse("""{"code":"en"}""", "test"));
    }
}
