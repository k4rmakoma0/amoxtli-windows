#if DEBUG
using Amoxtli.Controls;
using Amoxtli.Editing;
using Amoxtli.Services;
using Microsoft.UI.Xaml;
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
        Directory.CreateDirectory(folder);
        try
        {
            string one = Path.Combine(folder, "uno.md"), two = Path.Combine(folder, "dos.md");
            await File.WriteAllTextAsync(one, "# Uno\n");
            await File.WriteAllTextAsync(two, "# Dos\n");
            await File.WriteAllTextAsync(outside, "# Suelto\n");
            SetMode(EditorMode.Source);

            await OpenFileAsync(outside);
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
            ConflictResolver = null;
            _autosaveTimer.Stop();
            SetMode(EditorMode.Format);
            if (_document is { IsDirty: true } dirty) UpdateDocument(dirty with { Source = dirty.Saved });
            _folderExplicit = false;
            Navigate(new ReadingDocument("# Fin de archivos", null, "Fin"), MarkdownService.Parse("# Fin de archivos"));
            try { Directory.Delete(folder, true); File.Delete(outside); } catch (IOException) { }
        }
    }
}
#endif
