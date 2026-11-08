using System.Text.Json;

namespace Amoxtli.Services;

public sealed class ReaderSettings
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amoxtli", "settings.json");
    public bool RemoteImages { get; set; } = true;
    public double FontSize { get; set; } = Typesetting.DefaultSize;
    public string Typeface { get; set; } = Typesetting.System;
    public string Theme { get; set; } = "Default";
    public string Language { get; set; } = "auto";
    public bool NavigationOpen { get; set; } = true;
    public bool PromptForDefaultApp { get; set; } = true;

    public static ReaderSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ReaderSettings>(File.ReadAllText(SettingsPath)) ?? new();
            settings.FontSize = Typesetting.Snap(settings.FontSize);
            settings.Typeface = Typesetting.NormalizeTypeface(settings.Typeface);
            return settings;
        }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
