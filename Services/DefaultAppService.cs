using System.Runtime.InteropServices;
using System.Text;
using Windows.System;

namespace Amoxtli.Services;

internal static class DefaultAppService
{
    private const int ErrorInsufficientBuffer = 122;
    private const uint AssocStrAppId = 21;

    public static string? GetCurrentAppId()
    {
        uint length = 0;
        if (GetCurrentApplicationUserModelId(ref length, null) != ErrorInsufficientBuffer || length == 0) return null;
        var appId = new StringBuilder(checked((int)length));
        return GetCurrentApplicationUserModelId(ref length, appId) == 0 ? appId.ToString() : null;
    }

    public static bool IsDefaultForMarkdown(string appId)
    {
        // ASSOCSTR_APPID queries the user's effective choice, including packaged apps.
        uint length = 0;
        if (AssocQueryString(0, AssocStrAppId, ".md", null, null, ref length) != 1 || length == 0) return false;
        var defaultAppId = new StringBuilder(checked((int)length));
        return AssocQueryString(0, AssocStrAppId, ".md", null, defaultAppId, ref length) == 0
            && string.Equals(defaultAppId.ToString(), appId, StringComparison.OrdinalIgnoreCase);
    }

    public static Uri GetSettingsUri(string? appId) => appId is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
        ? new Uri($"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(appId)}")
        : new Uri("ms-settings:defaultapps");

    public static async Task OpenSettingsAsync()
    {
        // https://learn.microsoft.com/windows/apps/develop/launch/launch-default-apps-settings
        var uri = GetSettingsUri(GetCurrentAppId());
        if (await Launcher.LaunchUriAsync(uri)) return;
        if (uri.Query.Length > 0 && await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"))) return;
        throw new InvalidOperationException(Localization.Get("SettingsError"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentApplicationUserModelId(ref uint length, [Out] StringBuilder? appId);

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryString(uint flags, uint str, string association, string? extra, [Out] StringBuilder? output, ref uint length);
}
