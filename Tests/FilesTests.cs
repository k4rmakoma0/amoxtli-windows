#if DEBUG
using Amoxtli.Controls;
using Amoxtli.Editing;
using Amoxtli.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text;

namespace Amoxtli;

// Folder autosave, changes made elsewhere, large documents, statistics and surfaces (port document, sections 9–13).
public sealed partial class MainWindow
{
    private async Task RunFilesTestsAsync(Action<string, bool, string?> check)
    {
        void Check(string name, bool condition, string? detail = null) => check(name, condition, detail);
        string folder = Path.Combine(Path.GetTempPath(), "amoxtli-carpeta-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "amoxtli-suelto-" + Guid.NewGuid().ToString("N") + ".md");
        bool restoreOnStartup = _settings.RestoreRecentOnStartup;
        Directory.CreateDirectory(folder);
        try
        {
            string one = Path.Combine(folder, "uno.md"), two = Path.Combine(folder, "dos.md");
            string recentFile = Path.Combine(folder, "recent.json");
            var persistentRecents = new RecentItems(recentFile);
            for (int index = 1; index <= 10; index++)
                persistentRecents.Remember(new RecentItem(Path.Combine(folder, $"{index}.md"), false));
            var secondInstance = new RecentItems(recentFile);
            secondInstance.Remember(new RecentItem(Path.Combine(folder, "6.md"), false));
            var savedRecents = persistentRecents.Read();
            Check("Los recientes persisten entre instancias, con ocho entradas sin duplicados", savedRecents.Count == 8
                && savedRecents[0].Path == Path.Combine(folder, "6.md") && savedRecents[1].Path == Path.Combine(folder, "10.md")
                && savedRecents.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 8);
            var olderRecent = new RecentItem(Path.Combine(folder, "10.md"), false);
            var bookmark = new ReadingPosition(ReadingSurface.Format, 180, 900, 42, 10);
            secondInstance.SavePosition(olderRecent, bookmark);
            var withBookmark = new RecentItems(recentFile).Read();
            Check("La posición persiste sin cambiar el orden de recientes", withBookmark.Count > 1 && withBookmark[1].Position == bookmark
                && withBookmark.Select(item => item.Path).SequenceEqual(savedRecents.Select(item => item.Path)));
            secondInstance.Clear();
            persistentRecents.SavePosition(olderRecent, bookmark);
            Check("Limpiar recientes persiste entre instancias", persistentRecents.Read().Count == 0 && new RecentItems(recentFile).Read().Count == 0);
            var reopenedRecent = new RecentItem(one, false);
            persistentRecents.Remember(reopenedRecent);
            Check("La lista persistente vuelve a registrar archivos después de limpiarla", secondInstance.Read().SequenceEqual([reopenedRecent]));

            var folderRecent = new RecentItem(folder, true, one);
            persistentRecents.Remember(folderRecent);
            persistentRecents.SavePosition(folderRecent, bookmark);
            secondInstance.Remember(new RecentItem(folder, true));
            Check("Reabrir una carpeta conserva la posición de su último archivo", secondInstance.Read().FirstOrDefault() == (folderRecent with { Position = bookmark }));
            var changedFolderRecent = folderRecent with { FilePath = two };
            secondInstance.Remember(changedFolderRecent);
            persistentRecents.SavePosition(folderRecent, bookmark);
            Check("Una carpeta no hereda la posición de otro archivo", secondInstance.Read().FirstOrDefault() == changedFolderRecent);
            await File.WriteAllTextAsync(one, "# Uno\n");
            await File.WriteAllTextAsync(two, "# Dos\n");
            await File.WriteAllTextAsync(outside, "# Suelto\n");
            SetMode(EditorMode.Source);

            await OpenFileAsync(outside);
            Check("El archivo suelto aparece primero en recientes", _recents.Read().FirstOrDefault() == new RecentItem(outside, false));
            _sourceEditor.Apply(new TextEdit(_sourceEditor.Text.Length, 0, "más\n", _sourceEditor.Text.Length + 4, 0));
            await Task.Delay(1500);
            Check("Un archivo suelto no se guarda solo", File.ReadAllText(outside) == "# Suelto\n" && _document is { IsDirty: true });
            UpdateDocument(_document! with { Source = _document!.Saved });

            await OpenFolderAsync(folder, false);
            await OpenFileAsync(one);
            _sourceEditor.Apply(new TextEdit(_sourceEditor.Text.Length, 0, "editado\n", _sourceEditor.Text.Length + 8, 0));
            await Task.Delay(400);
            Check("La carpeta espera un segundo antes de guardar", File.ReadAllText(one) == "# Uno\n");
            await Task.Delay(1200);
            Check("Autoguardado en la carpeta", File.ReadAllText(one) == "# Uno\neditado\n" && _document is { IsDirty: false } && !DocumentName.Text.StartsWith("• "));
            _sourceEditor.Apply(new TextEdit(_sourceEditor.Text.Length, 0, "otra\n", _sourceEditor.Text.Length + 5, 0));
            await OpenFileAsync(two);
            Check("Cambiar de archivo guarda el anterior", File.ReadAllText(one) == "# Uno\neditado\notra\n" && _document?.Path == two);
            Check("La carpeta reciente recuerda el último archivo que se abrió dentro", _recents.Read().FirstOrDefault() == new RecentItem(folder, true, two)
                && !_recents.Read().Any(item => item.Path == one || item.Path == two));
            var recentLabels = RecentMenu.Items.OfType<MenuFlyoutItem>().Take(2).Select(item => item.Text).ToArray();
            Check("El menú Recientes muestra la carpeta y el archivo suelto en orden", recentLabels.Length == 2
                && recentLabels[0].Contains(Path.GetFileName(folder), StringComparison.Ordinal)
                && recentLabels[1].Contains(Path.GetFileName(outside), StringComparison.Ordinal));

            // Changed in another app with unsaved changes here.
            // The watcher would reload on its own and answer first; these checks drive the reload themselves.
            if (_watcher is not null) _watcher.EnableRaisingEvents = false;
            var choices = new Queue<ConflictChoice>();
            int asked = 0;
            ConflictResolver = (_, _) => { asked++; return Task.FromResult(choices.Dequeue()); };
            _sourceEditor.Apply(new TextEdit(0, 0, "mío ", 4, 0));
            _autosaveTimer.Stop();
            await File.WriteAllTextAsync(two, "# De afuera\n");
            File.SetLastWriteTimeUtc(two, DateTime.UtcNow.AddMinutes(1));
            choices.Enqueue(ConflictChoice.Cancel);
            Check("No guarda encima de un archivo que cambió", !await SaveAsync(false, autosave: true) && asked == 1 && File.ReadAllText(two) == "# De afuera\n" && _document is { IsDirty: true });
            choices.Enqueue(ConflictChoice.KeepMine);
            Check("Conservar la mía reemplaza el archivo", await SaveAsync(false) && File.ReadAllText(two) == "mío # Dos\n" && asked == 2);
            _sourceEditor.Apply(new TextEdit(0, 0, "x", 1, 0));
            _autosaveTimer.Stop();
            await File.WriteAllTextAsync(two, "# Otra vez de afuera\n");
            File.SetLastWriteTimeUtc(two, DateTime.UtcNow.AddMinutes(2));
            choices.Enqueue(ConflictChoice.UseDisk);
            await ReloadCurrentAsync(true);
            Check("Usar la del disco recarga", asked == 3 && _document is { Source: "# Otra vez de afuera\n", IsDirty: false } && _sourceEditor.Text == "# Otra vez de afuera\n");
            await File.WriteAllTextAsync(two, "# Sin cambios aquí\n");
            File.SetLastWriteTimeUtc(two, DateTime.UtcNow.AddMinutes(3));
            await ReloadCurrentAsync(true);
            Check("Sin cambios locales se recarga sin preguntar", asked == 3 && _document?.Source == "# Sin cambios aquí\n");
            ConflictResolver = null;

            File.Delete(two);
            Notice.IsOpen = false;
            await ReloadCurrentAsync(true);
            Check("Archivo borrado: aviso y el texto sigue", Notice.IsOpen && Notice.Message == Localization.Format("FileDeleted", "dos.md") && _sourceEditor.Text == "# Sin cambios aquí\n");
            _sourceEditor.Apply(new TextEdit(0, 0, "y", 1, 0));
            Check("El autoguardado no revive un archivo borrado", !await AutosaveAsync() && !File.Exists(two));
            UpdateDocument(_document! with { Source = _document!.Saved });

            _settings.RestoreRecentOnStartup = true;
            _recents.Remember(new RecentItem(folder, true, one));
            _recents.Remember(new RecentItem(two, false));
            await RestoreRecentAsync();
            Check("La restauración omite el archivo borrado y abre la carpeta con su último archivo", _folderPath == folder
                && _document?.Path == one && _sourceEditor.Text == "# Uno\neditado\notra\n" && Autosaves(_document));

            _recents.Remember(new RecentItem(one, false));
            await RestoreRecentAsync();
            Check("La restauración de un archivo suelto conserva el guardado manual", _document?.Path == one
                && _recents.Read().FirstOrDefault() == new RecentItem(one, false) && !Autosaves(_document));

            string anotherFolder = Path.Combine(folder, "otra-carpeta");
            Directory.CreateDirectory(anotherFolder);
            string anotherFile = Path.Combine(anotherFolder, "inicio.md");
            await File.WriteAllTextAsync(anotherFile, "# Otra carpeta\n");
            await OpenFolderAsync(folder, false);
            _sourceEditor.Apply(new TextEdit(_sourceEditor.Text.Length, 0, "guardado al cambiar\n", 0, 0));
            _recents.Remember(new RecentItem(anotherFolder, true, anotherFile));
            await RestoreRecentAsync();
            Check("Abrir una carpeta reciente completa el autoguardado de la carpeta anterior", _folderPath == anotherFolder
                && _document?.Path == anotherFile && File.ReadAllText(one).EndsWith("guardado al cambiar\n", StringComparison.Ordinal));

            _settings.RestoreRecentOnStartup = false;
            _recents.Remember(new RecentItem(outside, false));
            var pendingRecents = _recents.Read();
            await RestoreRecentAsync();
            Check("Desactivar la restauración conserva el documento y la lista", _document?.Path == anotherFile
                && _folderPath == anotherFolder && _recents.Read().SequenceEqual(pendingRecents));

            var clearRecents = RecentMenu.Items.OfType<MenuFlyoutItem>().SingleOrDefault(item => item.Text == Localization.Get("ClearRecentItems"));
            if (clearRecents is { IsEnabled: true }) ClearRecents_Click(clearRecents, new RoutedEventArgs());
            Check("Limpiar recientes conserva abierto el documento", _recents.Read().Count == 0 && _document?.Path == anotherFile
                && _folderPath == anotherFolder && File.ReadAllText(anotherFile) == "# Otra carpeta\n"
                && RecentMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == Localization.Get("ClearRecentItems") && !item.IsEnabled));
            await OpenFileAsync(outside);
            Check("Abrir un archivo registra recientes con la restauración desactivada", _document?.Path == outside
                && _recents.Read().SequenceEqual([new RecentItem(outside, false)])
                && RecentMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == Localization.Get("ClearRecentItems") && item.IsEnabled));

            await RunReadingPositionTestsAsync(check);

            // Statistics and surfaces
            const string counted = "# Uno dos\n\ntres `cuatro`\n\n```\ncinco seis\n```\n\n![siete](x.png)\n\n| ocho | nueve |\n| --- | --- |\n| diez | once |\n";
            Check("Palabras sin código ni imágenes", MarkdownService.CountWords(MarkdownService.Parse(counted)) == 7, MarkdownService.CountWords(MarkdownService.Parse(counted)).ToString());
            Navigate(new ReadingDocument(counted, null, "Palabras"), MarkdownService.Parse(counted));
            Check("Estadísticas en su propia franja", StatsText.Text == Localization.Format("DocumentStatusShort", 7) && StatusText.Text == "");
            string many = string.Join(" ", Enumerable.Repeat("palabra", 450));
            Navigate(new ReadingDocument(many, null, "Muchas"), MarkdownService.Parse(many));
            Check("Tiempo de lectura a 200 palabras por minuto", StatsText.Text == Localization.Format("DocumentStatus", 450, 2));
            Check("Superficies con opacidad", EditorPalette.CodeSurface(ElementTheme.Light) is { A: 19, R: 0 } && EditorPalette.CodeSurface(ElementTheme.Dark) is { A: 33, R: 255 }
                && EditorPalette.TableLineSurface(ElementTheme.Dark) is { A: 66 } && EditorPalette.TableHeaderSurface(ElementTheme.Light) is { A: 13 });

            // Large documents: bytes in UTF-8, not characters
            string large = new string('ñ', 600_000);
            SetMode(EditorMode.Format);
            Navigate(new ReadingDocument(large, null, "Grande"), MarkdownService.Parse("ñ"));
            Check("Un documento de más de 1 MB abre en Fuente", _mode == EditorMode.Source && _sourceEditor.Text.Length == large.Length);
            SetMode(EditorMode.Format);
            Check("Formato muestra Documento demasiado grande", TooLargePanel.Visibility == Visibility.Visible && FormatHost.Visibility == Visibility.Collapsed
                && ReaderScroll.Visibility == Visibility.Collapsed && TooLargeText.Text.Contains(Localization.Format("SizeMegabytes", 1.2)) && _formatEditor.Fields.Count == 0);
            TooLargeRead_Click(TooLargeRead, new RoutedEventArgs());
            Check("Leer con formato usa el lector", ReaderScroll.Visibility == Visibility.Visible && TooLargePanel.Visibility == Visibility.Collapsed);
            TooLargeSource_Click(TooLargeSource, new RoutedEventArgs());
            Check("Mostrar fuente", _mode == EditorMode.Source);
            Check("El límite cuenta bytes UTF-8", !TooLarge(new ReadingDocument(new string('a', 900_000), null, "Casi")) && TooLarge(new ReadingDocument(new string('ñ', 600_000), null, "Grande")));
        }
        finally
        {
            _settings.RestoreRecentOnStartup = restoreOnStartup;
            ConflictResolver = null;
            _autosaveTimer.Stop();
            SetMode(EditorMode.Format);
            if (_document is { IsDirty: true } dirty) UpdateDocument(dirty with { Source = dirty.Saved });
            _folderExplicit = false;
            Navigate(new ReadingDocument("# Fin de archivos", null, "Fin"), MarkdownService.Parse("# Fin de archivos"));
            try { Directory.Delete(folder, true); File.Delete(outside); } catch (IOException) { }
        }
    }

    internal async Task RunReadingPositionTestsAsync(Action<string, bool, string?> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "amoxtli-position-" + Guid.NewGuid().ToString("N"));
        string inside = Path.Combine(folder, "carpeta");
        Directory.CreateDirectory(inside);
        bool restoreOnStartup = _settings.RestoreRecentOnStartup;
        _settings.RestoreRecentOnStartup = true;
        const string pause = "# Pausa de lectura";
        void LeaveFile() => Navigate(new ReadingDocument(pause, null, "Pausa de lectura"), MarkdownService.Parse(pause));
        string ScrollDetails() => $"Fuente {_sourceEditor.Scroll?.VerticalOffset}/{_sourceEditor.Scroll?.ScrollableHeight}, "
            + $"Formato {_formatEditor.Scroll.VerticalOffset}/{_formatEditor.Scroll.ScrollableHeight}.";

        MenuFlyoutItem? MenuEntry(string path) => RecentMenu.Items.OfType<MenuFlyoutItem>()
            .FirstOrDefault(item => item.Tag is RecentItem entry && entry.Path == path);

        async Task SelectRecentAsync(MenuFlyoutItem item, Func<bool> isAtExpectedPosition)
        {
            RecentItem_Click(item, new RoutedEventArgs());
            for (int attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(50);
                if (isAtExpectedPosition()) break;
            }
            await Task.Delay(200);
        }

        try
        {
            string readingSample = string.Join("\n\n", Enumerable.Range(1, 36).Select(index => $"## Sección {index}\n\nPárrafo {index}."));
            string newestFile = Path.Combine(folder, "otro-reciente.md");
            await File.WriteAllTextAsync(newestFile, readingSample);
            foreach (var (name, isFolder, mode, surface, label) in new[]
            {
                ("formato-suelto.md", false, EditorMode.Format, ReadingSurface.Format, "archivo suelto en Formato"),
                ("formato-en-carpeta.md", true, EditorMode.Format, ReadingSurface.Format, "archivo de una carpeta en Formato"),
                ("fuente-suelto.md", false, EditorMode.Source, ReadingSurface.Source, "archivo suelto en Fuente"),
                ("dividido-en-carpeta.md", true, EditorMode.Split, ReadingSurface.Split, "archivo de una carpeta en Dividido")
            })
            {
                string file = Path.Combine(isFolder ? inside : folder, name);
                var recent = isFolder ? new RecentItem(inside, true, file) : new RecentItem(file, false);
                await File.WriteAllTextAsync(file, readingSample);
                SetMode(mode);
                if (isFolder) await OpenFolderAsync(inside);
                await OpenFileAsync(file, standalone: !isFolder);
                await Task.Delay(800);
                Root.UpdateLayout();
                if (mode != EditorMode.Format) _sourceEditor.CalibrateUnits();
                var scroll = mode == EditorMode.Source ? _sourceEditor.Scroll : _formatEditor.Scroll;
                if (scroll is null)
                {
                    check($"Encontrar desplazamiento de un {label}", false, ScrollDetails());
                    continue;
                }

                double target = Math.Min(750, scroll.ScrollableHeight * 0.6);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (mode == EditorMode.Split) _splitScroll.RestoreFormatOffset(target);
                    else scroll.ChangeView(null, target, null, true);
                    await Task.Delay(250);
                    if (Math.Abs(scroll.VerticalOffset - target) <= 6) break;
                }
                double offset = scroll.VerticalOffset;
                SaveRecentPosition();
                var saved = _recents.Read().FirstOrDefault();
                var position = saved?.Position;
                double? savedTop = position?.TextOffset is { } savedAnchor ? TopAt(surface, savedAnchor) : null;
                check($"Guardar posición de lectura de un {label}", offset > 100 && saved is not null && saved.Path == recent.Path
                    && saved.IsFolder == recent.IsFolder && saved.FilePath == recent.FilePath
                    && position is not null && position.Surface == surface && Math.Abs(position.VerticalOffset - offset) <= 2,
                    $"Destino {target}, medida {offset}, guardada {position?.VerticalOffset}/{position?.ScrollableHeight}, "
                    + $"ancla {position?.TextOffset} en {savedTop}, desplazamiento desde el ancla {position?.OffsetInText}. {ScrollDetails()}");

                LeaveFile();
                await Task.Delay(250);
                Root.UpdateLayout();
                bool leftReadingPosition = scroll.VerticalOffset <= 2;
                SetMode(EditorMode.Format);
                await RestoreRecentAsync();
                await Task.Delay(800);
                Root.UpdateLayout();
                var restoredScroll = mode == EditorMode.Source ? _sourceEditor.Scroll : _formatEditor.Scroll;
                double? restoredOffset = restoredScroll?.VerticalOffset;
                double? restoredTop = position?.TextOffset is { } restoredAnchor ? TopAt(surface, restoredAnchor) : null;
                var visibleAnchor = restoredOffset is { } visibleOffset
                    ? mode == EditorMode.Source ? _sourceEditor.LineAt(visibleOffset) : FormatBlockAt(visibleOffset)
                    : null;
                double expected = offset;
                if (mode == EditorMode.Split && position?.TextOffset is { } anchor && restoredScroll is not null
                    && OffsetAt(surface, anchor, position.OffsetInText) is { } anchoredOffset)
                    expected = Math.Clamp(anchoredOffset, 0, restoredScroll.ScrollableHeight);

                check($"Recuperar la posición de lectura de un {label}", leftReadingPosition && _document?.Path == file
                    && _mode == mode && Autosaves(_document) == isFolder && restoredOffset is { } actual && Math.Abs(actual - expected) <= 6
                    && (position?.TextOffset is null || visibleAnchor?.Offset == position.TextOffset),
                    $"Posición original {offset}/{position?.ScrollableHeight}, esperada {expected}, restaurada {restoredOffset}/{restoredScroll?.ScrollableHeight}. "
                    + $"Ancla guardada {position?.TextOffset} en {savedTop}, al reabrir en {restoredTop}; visible {visibleAnchor?.Offset} en {visibleAnchor?.Top}. "
                    + $"Modo {_mode}, salió de la posición anterior {leftReadingPosition}. {ScrollDetails()}");

                if (mode != EditorMode.Format) continue;

                _settings.RestoreRecentOnStartup = false;
                LeaveFile();
                await Task.Delay(250);
                await OpenFileAsync(newestFile, standalone: true);
                await Task.Delay(250);
                bool differentNewest = _recents.Read().FirstOrDefault()?.Path == newestFile;
                var menuItem = MenuEntry(recent.Path);
                var manualScroll = _formatEditor.Scroll;
                bool ResumedFromMenu() => _document?.Path == file && _mode == mode
                    && Math.Abs(manualScroll.VerticalOffset - offset) <= 6;
                if (menuItem is not null) await SelectRecentAsync(menuItem, ResumedFromMenu);
                check($"Abrir desde Recientes recupera la posición de un {label}", differentNewest
                    && menuItem?.Tag is RecentItem { Position: not null } && offset > 100
                    && ResumedFromMenu() && Autosaves(_document) == isFolder,
                    $"Distinto reciente más nuevo: {differentNewest}. Posición esperada {offset}, actual {manualScroll.VerticalOffset}. {ScrollDetails()}");

                if (!isFolder && ResumedFromMenu() && MenuEntry(recent.Path)?.Tag is RecentItem displayedRecent)
                {
                    manualScroll.ChangeView(null, Math.Min(1200, manualScroll.ScrollableHeight), null, true);
                    await Task.Delay(250);
                    double currentOffset = manualScroll.VerticalOffset;
                    bool opened = await OpenRecentItemAsync(displayedRecent, automatic: false);
                    await Task.Delay(250);
                    check("Abrir el archivo actual desde Recientes conserva su posición visible", opened
                        && _document?.Path == file && currentOffset > offset + 100
                        && Math.Abs(manualScroll.VerticalOffset - currentOffset) <= 6,
                        $"Posición guardada {offset}, visible antes {currentOffset}, después {manualScroll.VerticalOffset}.");

                    var unmarked = MenuEntry(newestFile);
                    bool OpenedAtTop() => _document?.Path == newestFile && manualScroll.VerticalOffset <= 2;
                    if (unmarked is not null) await SelectRecentAsync(unmarked, OpenedAtTop);
                    check("Un reciente sin posición abre al principio del archivo", unmarked?.Tag is RecentItem { Position: null }
                        && OpenedAtTop(), $"Posición del archivo sin marca {manualScroll.VerticalOffset}.");
                }
                _settings.RestoreRecentOnStartup = true;
            }
        }
        finally
        {
            _settings.RestoreRecentOnStartup = restoreOnStartup;
            SetMode(EditorMode.Format);
            LeaveFile();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
#endif
