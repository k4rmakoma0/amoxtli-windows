using Microsoft.UI.Xaml;
using Amoxtli.Services;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Amoxtli;

public partial class App : Application
{
    private MainWindow? _window;
    public App() => InitializeComponent();

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        await _window.Ready;
#if DEBUG
        var commandLine = Environment.GetCommandLineArgs();
        int smoke = Array.IndexOf(commandLine, "--smoke-test");
        if (smoke >= 0)
        {
            string report = smoke + 1 < commandLine.Length ? commandLine[smoke + 1] : Path.Combine(AppContext.BaseDirectory, "smoke-results.json");
            await _window.RunSmokeTestsAsync(report);
            _window.Close();
            return;
        }
        int memory = Array.IndexOf(commandLine, "--memory-test");
        if (memory >= 0 && memory + 2 < commandLine.Length)
        {
            await _window.RunMemoryTestsAsync(commandLine[memory + 1], commandLine[memory + 2]);
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
                var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
                if (path is not null)
                {
                    if (Directory.Exists(path)) await _window.OpenFolderAsync(path);
                    else await _window.OpenFileAsync(path);
                }
            }
        }
        catch (Exception ex) { await _window.ShowErrorAsync(Localization.Get("OpenFileError"), ex.Message); }
        await _window.OfferDefaultAppAsync();
    }
}
