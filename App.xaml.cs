using Microsoft.UI.Xaml;
using Amoxtli.Services;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Amoxtli;

public partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        InitializeComponent();
#if DEBUG
        // Debug builds leave the last unhandled exception beside the executable, for crashes in tests.
        UnhandledException += (_, args) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "last-crash.txt"), args.Exception.ToString());
#endif
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
#if DEBUG
        int smoke = Array.IndexOf(commandLine, "--smoke-test");
        int memory = Array.IndexOf(commandLine, "--memory-test");
        int readingPosition = Array.IndexOf(commandLine, "--reading-position-test");
        _window = smoke >= 0 || memory >= 0 || readingPosition >= 0 ? new MainWindow(new RecentItems()) : new MainWindow();
#else
        _window = new MainWindow();
#endif
        _window.Activate();
        await _window.Ready;
#if DEBUG
        if (smoke >= 0)
        {
            string report = smoke + 1 < commandLine.Length ? commandLine[smoke + 1] : Path.Combine(AppContext.BaseDirectory, "smoke-results.json");
            await _window.RunSmokeTestsAsync(report);
            _window.Close();
            return;
        }
        if (memory >= 0 && memory + 2 < commandLine.Length)
        {
            await _window.RunMemoryTestsAsync(commandLine[memory + 1], commandLine[memory + 2]);
            _window.Close();
            return;
        }
        if (readingPosition >= 0)
        {
            string report = readingPosition + 1 < commandLine.Length ? commandLine[readingPosition + 1]
                : Path.Combine(AppContext.BaseDirectory, "reading-position-results.json");
            var results = new List<object>();
            int passed = 0, failed = 0;
            void Check(string name, bool success, string? detail)
            {
                if (success) passed++; else failed++;
                results.Add(new { name, passed = success, detail });
            }
            try { await _window.RunReadingPositionTestsAsync(Check); }
            catch (Exception ex) { Check("Pruebas de posición de lectura", false, ex.ToString()); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new { passed, failed, results }, options));
            _window.Close();
            return;
        }
#endif
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Data is IFileActivatedEventArgs files)
            {
                var file = files.Files.OfType<StorageFile>().FirstOrDefault();
                if (file is not null) await _window.OpenFileAsync(file.Path);
            }
            else
            {
                var path = commandLine.Skip(1).FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
                if (path is not null)
                {
                    if (Directory.Exists(path)) await _window.OpenFolderAsync(path);
                    else await _window.OpenFileAsync(path);
                }
                else await _window.RestoreRecentAsync();
            }
        }
        catch (Exception ex) { await _window.ShowErrorAsync(Localization.Get("OpenFileError"), ex.Message); }
        await _window.OfferDefaultAppAsync();
    }
}
