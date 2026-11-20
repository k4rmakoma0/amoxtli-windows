using Amoxtli.Models;
using Amoxtli.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amoxtli;

public sealed partial class MainWindow
{
    private readonly RecentItems _recents;

    private RecentItem RecentDocument(string path) =>
        _folderExplicit && _folderPath is { } folder && IsInside(path, folder)
            ? new RecentItem(folder, true, path)
            : new RecentItem(path, false);

    private void RememberDocument(string path) => RememberRecent(RecentDocument(path));

    private void RememberFolder(string path)
    {
        string? file = _document is { Kind: DocumentKind.File, Path: { } current } && IsInside(current, path) ? current : null;
        RememberRecent(new RecentItem(path, true, file));
    }

    private void RememberRecent(RecentItem recent) => UpdateRecents(() => _recents.Remember(recent));

    private void UpdateRecents(Action update)
    {
        try { update(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(Localization.Format("SaveSettingsError", ex.Message), InfoBarSeverity.Warning);
        }
        RefreshRecentMenu();
    }

    private void RefreshRecentMenu()
    {
        RecentMenu.Items.Clear();
        var entries = _recents.Read().Select(recent =>
            (Recent: recent, Name: Path.GetFileName(recent.Path) is { Length: > 0 } name ? name : recent.Path)).ToList();
        if (entries.Count == 0)
        {
            var empty = new MenuFlyoutItem { IsEnabled = false };
            LocalizedText.SetText(empty, "NoRecentItems");
            RecentMenu.Items.Add(empty);
        }

        var duplicateNames = entries.GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (recent, name) in entries)
        {
            var item = new MenuFlyoutItem
            {
                Text = duplicateNames.Contains(name) ? recent.Path : name,
                Icon = new FontIcon { Glyph = new FileTreeItem(recent.Path, recent.IsFolder).Glyph },
                Tag = recent
            };
            ToolTipService.SetToolTip(item, recent.Path);
            item.Click += RecentItem_Click;
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { IsEnabled = entries.Count > 0 };
        LocalizedText.SetText(clear, "ClearRecentItems");
        clear.Click += ClearRecents_Click;
        RecentMenu.Items.Add(clear);
    }

    private void ClearRecents_Click(object sender, RoutedEventArgs e) => UpdateRecents(_recents.Clear);

    private async void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: RecentItem recent }) return;
        int revision = ++_openRevision;
        bool exists = await Task.Run(() => recent.IsFolder ? Directory.Exists(recent.Path) : File.Exists(recent.Path));
        if (_closed || revision != _openRevision) return;
        if (!exists)
        {
            Notify(Localization.Format("RecentItemUnavailable", recent.Path), InfoBarSeverity.Warning);
            return;
        }
        await OpenRecentItemAsync(recent, false);
    }

    private async Task<bool> OpenRecentItemAsync(RecentItem recent, bool automatic)
    {
        string? targetFile = recent.IsFolder ? recent.FilePath : recent.Path;
        // The current viewport may be newer than the bookmark captured by the menu item.
        var position = _document is { Kind: DocumentKind.File, Path: { } current }
            && string.Equals(current, targetFile, StringComparison.OrdinalIgnoreCase)
            ? CaptureReadingPosition() ?? recent.Position
            : recent.Position;

        if (!recent.IsFolder)
        {
            int openRevision = _openRevision + 1;
            if (!await OpenFileAsync(recent.Path, standalone: true, reportError: !automatic)) return false;
            if (openRevision == _openRevision) await RestoreReadingPositionAsync(position);
            return true;
        }

        int revision = ++_openRevision;
        int folderRevision = ++_folderRevision;
        bool IsCurrent() => !_closed && revision == _openRevision && folderRevision == _folderRevision;
        try
        {
            var entries = await Task.Run(() => ReadDirectory(recent.Path));
            if (!IsCurrent()) return false;

            (ReadingDocument Document, Markdig.Syntax.MarkdownDocument Parsed)? selected = null;
            bool keepCurrentFile = targetFile is not null && IsCurrentDirtyFile(targetFile);
            if (targetFile is { } file && MarkdownService.IsMarkdown(file) && !keepCurrentFile)
            {
                try { selected = await ReadDocumentAsync(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    // The folder is still available even if its last file cannot be opened.
                }
                if (!IsCurrent()) return false;
            }

            // Save or confirm leaving while the old folder's autosave rules still apply.
            if (selected is not null && (!await ConfirmLeaveAsync() || !IsCurrent())) return false;
            if (selected is { } loaded) Navigate(loaded.Document, loaded.Parsed);
            else SaveRecentPosition();
            ShowFolder(recent.Path, entries, selectTab: true, chosen: true);
            RememberFolder(recent.Path);
            if (selected is not null || keepCurrentFile) await RestoreReadingPositionAsync(position);
            return true;
        }
        catch (Exception ex)
        {
            if (!automatic && IsCurrent()) await ShowErrorAsync(Localization.Get("OpenFolderError"), ex.Message);
            return false;
        }
    }

    internal async Task RestoreRecentAsync()
    {
        if (!_settings.RestoreRecentOnStartup) return;
        foreach (var recent in _recents.Read())
        {
            int revision = _openRevision + 1;
            if (await OpenRecentItemAsync(recent, true) || _closed || revision != _openRevision) return;
        }
    }
}
