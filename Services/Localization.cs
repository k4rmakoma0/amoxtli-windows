using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Amoxtli.Services;

public sealed record LanguageOption(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

/// <summary>Embedded catalogs work in both packaged and unpackaged builds.</summary>
public static class Localization
{
    public static IReadOnlyList<LanguageOption> Languages { get; } = new LanguageOption[]
    {
        new("en", "English"), new("es", "Español"), new("pt", "Português"),
        new("fr", "Français"), new("de", "Deutsch"), new("it", "Italiano"),
        new("ru", "Русский"), new("zh-Hans", "简体中文"), new("ja", "日本語"),
        new("ko", "한국어"), new("ar", "العربية"), new("hi", "हिन्दी")
    };
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Catalogs = new(StringComparer.OrdinalIgnoreCase);
    public static string CurrentLanguage { get; private set; } = "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");
    public static bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    public static string ResolveLanguage(string? preference, IEnumerable<string>? systemLanguages = null)
    {
        if (!string.IsNullOrWhiteSpace(preference) && preference != "auto")
            return MatchLanguage(preference) ?? "en";
        systemLanguages ??= Windows.System.UserProfile.GlobalizationPreferences.Languages;
        return systemLanguages.Select(MatchLanguage).FirstOrDefault(code => code is not null) ?? "en";
    }

    private static string? MatchLanguage(string tag)
    {
        var exact = Languages.FirstOrDefault(item => item.Code.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Code;
        try
        {
            string language = CultureInfo.GetCultureInfo(tag).TwoLetterISOLanguageName;
            return Languages.FirstOrDefault(item => item.Code.Split('-')[0] == language)?.Code;
        }
        catch (CultureNotFoundException) { return null; }
    }

    public static void SetLanguage(string? preference)
    {
        string language = ResolveLanguage(preference);
        var culture = CultureInfo.GetCultureInfo(language);
        // Load eagerly so a broken catalog cannot leave half of the interface translated.
        _ = GetCatalog(language);
        CurrentLanguage = language;
        Culture = culture;
        LocalizedText.Refresh();
    }

    internal static IReadOnlyDictionary<string, string> GetCatalog(string language)
    {
        if (Catalogs.TryGetValue(language, out var catalog)) return catalog;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Amoxtli.Localization.{language}.json")
            ?? throw new InvalidOperationException($"Missing language catalog: {language}");
        catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Invalid language catalog: {language}");
        Catalogs.Add(language, catalog);
        return catalog;
    }

    public static string Get(string key) => Get(key, CurrentLanguage);
    // Read a catalog without changing the active app language, for dialog previews.
    internal static string Get(string key, string language) => GetCatalog(language).TryGetValue(key, out var value)
        ? value : GetCatalog("en").TryGetValue(key, out var fallback) ? fallback : key;
    public static string Format(string key, params object[] args) => string.Format(Culture, Get(key), args);
    public static string Percent(double value) => value.ToString("P0", Culture);
    /// <summary>"Ctrl+Shift+X" with the modifier names of the interface language, such as "Strg+Umschalt+X".</summary>
    public static string Shortcut(string keys) => string.Join("+", keys.Split('+').Select(key => key switch
    {
        "Ctrl" => Get("KeyCtrl"),
        "Shift" => Get("KeyShift"),
        _ => key
    }));

    public static string WelcomeMarkdown() => $"""
        # {Get("WelcomeTitle")}

        {Get("WelcomeIntro")}

        {Get("WelcomeOpen")}

        {Get("WelcomeNavigation")}

        > {Get("WelcomeEditing")}

        ## {Get("MenuHelp")}

        | {Get("Action")} | {Get("Shortcut")} |
        | :--- | :--- |
        | {Get("OpenFile")} | {Shortcut("Ctrl+O")} |
        | {Get("OpenFolder")} | {Shortcut("Ctrl+Shift+O")} |
        | {Get("Save")} | {Shortcut("Ctrl+S")} |
        | {Get("Find")} | {Shortcut("Ctrl+F")} |
        | {Get("CopyMarkdown")} | {Shortcut("Ctrl+Shift+C")} |
        | {Get("Navigation")} | {Shortcut("Ctrl+Shift+L")} |
        | {Get("ViewSource")} | {Shortcut("Ctrl+Shift+1")} |
        | {Get("ViewFormat")} | {Shortcut("Ctrl+Shift+2")} |
        | {Get("ViewSplit")} | {Shortcut("Ctrl+Shift+3")} |
        | {Get("Reload")} | F5 |

        ## Markdown

        {Get("WelcomeFormats")}

        ```csharp
        Console.WriteLine("Amoxtli");
        ```

        {Get("WelcomeLinks")}

        ## {Get("Preferences")}

        {Get("WelcomeSettings")}

        {Get("WelcomeLimitations")}
        """;
}
