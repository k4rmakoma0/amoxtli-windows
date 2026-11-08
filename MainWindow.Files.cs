using Amoxtli.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Amoxtli;

// Files: folder autosave, and changes made to the open file by other apps (port document, sections 9 and 10).
public sealed partial class MainWindow
{
    internal enum ConflictChoice { KeepMine, UseDisk, Cancel }

    private DispatcherQueueTimer _autosaveTimer = null!;
    /// <summary>A folder the user opened; the folder shown beside a single opened file does not autosave.</summary>
    private bool _folderExplicit;
    /// <summary>Tests answer the "changed in another app" question instead of the dialog.</summary>
    private Func<ReadingDocument, bool, Task<ConflictChoice>>? ConflictResolver { get; set; }

    private void InitializeFiles()
    {
        _autosaveTimer = DispatcherQueue.CreateTimer();
        _autosaveTimer.Interval = TimeSpan.FromSeconds(1);
        _autosaveTimer.IsRepeating = false;
        _autosaveTimer.Tick += async (_, _) => await AutosaveAsync();
        Activated += Window_Activated;
    }

    /// <summary>Files inside a folder the user opened save on their own; single files use Save and ask before closing.</summary>
    private bool Autosaves(ReadingDocument? document) =>
        document is { Kind: DocumentKind.File, Path: { } path } && _folderExplicit && _folderPath is { } folder && IsInside(path, folder);

    private static bool IsInside(string path, string folder) =>
        System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>One second after the last edit.</summary>
    private void ScheduleAutosave()
    {
        if (!Autosaves(_document)) return;
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private async Task<bool> AutosaveAsync()
    {
        _autosaveTimer.Stop();
        if (_document is not { IsDirty: true } document || !Autosaves(document)) return true;
        return await SaveAsync(false, autosave: true);
    }

    private async void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_closed) return;
        // Leaving the app saves, like switching files; coming back picks up changes made elsewhere.
        if (args.WindowActivationState == WindowActivationState.Deactivated) { await AutosaveAsync(); return; }
        if (_document is { Kind: DocumentKind.File, Path: { } path } document && System.IO.File.Exists(path) && DocumentFile.Stamp(path) != document.Stamp)
            await ReloadCurrentAsync(true);
    }

    private void FileMissing()
    {
        if (_document is { Kind: DocumentKind.File, Path: { } path } document && !System.IO.File.Exists(path))
            Notify(Localization.Format("FileDeleted", DisplayName(document)), InfoBarSeverity.Warning);
    }

    /// <summary>
    /// The file changed in another app while this one has unsaved changes: keep mine (saving replaces the file)
    /// or use the version on disk. When saving, Cancel leaves both alone.
    /// </summary>
    private async Task<ConflictChoice> ResolveConflictAsync(ReadingDocument document, bool saving)
    {
        if (ConflictResolver is { } resolver) return await resolver(document, saving);
        if (_dialogOpen) return ConflictChoice.Cancel;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Localization.Format("ChangedOnDiskTitle", DisplayName(document)),
            Content = new TextBlock { Text = Localization.Get("ChangedOnDiskText"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = Localization.Get("KeepMine"),
            SecondaryButtonText = Localization.Get("UseDisk"),
            CloseButtonText = saving ? Localization.Get("Cancel") : "",
            DefaultButton = ContentDialogButton.Primary
        };
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => ConflictChoice.KeepMine,
                ContentDialogResult.Secondary => ConflictChoice.UseDisk,
                _ => saving ? ConflictChoice.Cancel : ConflictChoice.KeepMine
            };
        }
        finally { _dialogOpen = false; }
    }

    /// <summary>
    /// Before writing over the open file: false to stop. A file deleted elsewhere is not brought back by autosave;
    /// a file changed elsewhere asks which version wins.
    /// </summary>
    private async Task<bool> CheckDiskBeforeSaveAsync(ReadingDocument document, string path, bool autosave)
    {
        if (document.Kind != DocumentKind.File || !string.Equals(document.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.IO.File.Exists(path))
        {
            if (!autosave) return true;
            FileMissing();
            return false;
        }
        if (document.Stamp == default || DocumentFile.Stamp(path) == document.Stamp) return true;
        string? disk;
        try { disk = (await DocumentFile.ReadAsync(path)).Text; }
        catch (Exception) { disk = null; }
        if (disk == document.Saved) return true;
        switch (await ResolveConflictAsync(document, true))
        {
            case ConflictChoice.KeepMine: return true;
            case ConflictChoice.UseDisk:
                if (_document is { } current) UpdateDocument(current with { Source = current.Saved });
                await ReloadCurrentAsync(false);
                return false;
            default: return false;
        }
    }
}
