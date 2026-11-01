using Windows.Storage;
using Windows.System;

namespace Amoxtli.Services;

public static class LinkService
{
    public static Uri? Resolve(string address, string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (Uri.TryCreate(address, UriKind.Absolute, out var absolute)) return absolute;
        if (documentPath is null) return null;
        // An implicit Windows file URI treats # and % as literal filename characters.
        // An explicit file URI preserves Markdown fragments and URL escaping.
        var baseUri = new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = Path.GetFullPath(documentPath) }.Uri;
        return Uri.TryCreate(baseUri, address.Replace('\\', '/'), out var relative) ? relative : null;
    }

    public static bool IsWeb(Uri uri) => uri.Scheme is "http" or "https";

    public static async Task OpenExternalAsync(Uri uri, bool chooseApplication)
    {
        // Arbitrary URI handlers and executables in a document are never invoked.
        if (!IsWeb(uri) && uri.Scheme != "mailto") throw new InvalidOperationException("Este tipo de enlace no se puede abrir desde el visor.");
        var opened = await Launcher.LaunchUriAsync(uri, new LauncherOptions { DisplayApplicationPicker = chooseApplication });
        if (!opened) throw new InvalidOperationException("Windows no pudo abrir el enlace. Comprueba que haya un navegador registrado.");
    }
}
