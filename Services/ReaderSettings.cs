using System.Text.Json;

namespace Amoxtli.Services;

public sealed class ReaderSettings
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amoxtli", "settings.json");
    public bool RemoteImages { get; set; } = true;
    public double FontSize { get; set; } = 16;
    public string Theme { get; set; } = "Default";
    public string Language { get; set; } = "auto";
    public bool NavigationOpen { get; set; } = true;
    public bool PromptForDefaultApp { get; set; } = true;

    public static ReaderSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ReaderSettings>(File.ReadAllText(SettingsPath)) ?? new();
            settings.FontSize = double.IsFinite(settings.FontSize) ? Math.Clamp(settings.FontSize, 12, 28) : 16;
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
