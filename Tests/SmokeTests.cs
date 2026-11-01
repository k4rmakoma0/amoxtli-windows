#if DEBUG
using Amoxtli.Services;
using Amoxtli.Controls;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Amoxtli;

// Runs in a real WinUI dispatcher with --smoke-test <report.json>.
// Tests avoid the user's clipboard, browser, file associations and preferences.
public sealed partial class MainWindow
{
    internal async Task RunSmokeTestsAsync(string reportPath)
    {
        var results = new List<object>();
        int passed = 0, failed = 0;
        void Check(string name, bool condition, string? detail = null)
        {
            if (condition) passed++; else failed++;
            results.Add(new { name, passed = condition, detail = condition ? null : detail });
        }
        try
        {
            var oldLanguageSettings = JsonSerializer.Deserialize<ReaderSettings>("{\"Theme\":\"Dark\"}");
            Check("Preferencias anteriores detectan el idioma de Windows", oldLanguageSettings?.Language == "auto");
            Check("Idioma seleccionado se conserva en preferencias", JsonSerializer.Deserialize<ReaderSettings>(JsonSerializer.Serialize(new ReaderSettings { Language = "ja" }))?.Language == "ja");
            Check("Resolución de idiomas regionales", Localization.ResolveLanguage("es-MX") == "es" && Localization.ResolveLanguage("pt-BR") == "pt" && Localization.ResolveLanguage("zh-CN") == "zh-Hans");
            Check("Detección automática respeta el orden de Windows", Localization.ResolveLanguage("auto", ["nl-NL", "fr-CA", "en-US"]) == "fr");
            Check("Idioma no compatible vuelve a inglés", Localization.ResolveLanguage("invalid-language") == "en" && Localization.ResolveLanguage("auto", ["nl-NL"]) == "en");
            var baseline = Localization.GetCatalog("en");
            foreach (var option in Localization.Languages)
            {
                var catalog = Localization.GetCatalog(option.Code);
                Check($"Catálogo completo: {option.Code}", catalog.Count == baseline.Count && baseline.Keys.All(key => catalog.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)));
                bool valid = true;
                foreach (var (key, value) in catalog)
                {
                    var expected = System.Text.RegularExpressions.Regex.Matches(baseline[key], @"\{\d+(?::[^}]+)?\}").Select(m => m.Value).Order().ToArray();
                    var actual = System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+(?::[^}]+)?\}").Select(m => m.Value).Order().ToArray();
                    valid &= expected.SequenceEqual(actual);
                    try { _ = string.Format(System.Globalization.CultureInfo.GetCultureInfo(option.Code), value, 12, 3); }
                    catch (FormatException) { valid = false; }
                }
                Check($"Formatos de traducción válidos: {option.Code}", valid);
            }
            _settings.Language = "es";
            ApplyLanguage();
            var previewSettings = new ReaderSettings { Language = "es", Theme = "Light", RemoteImages = true, FontSize = 17, PromptForDefaultApp = true };
            var preferences = new ReadingPreferencesDialog(previewSettings);
            preferences.ThemePicker.SelectedIndex = 2;
            preferences.RemoteImages.IsOn = false;
            preferences.TextSize.Value = 23;
            preferences.DefaultAppPrompt.IsChecked = false;
            string appLanguage = Localization.CurrentLanguage;
            string appMenu = Root.Children.OfType<MenuBar>().First().Items[0].Title;
            foreach (var option in Localization.Languages)
            {
                preferences.LanguagePicker.SelectedItem = ((IEnumerable<LanguageOption>)preferences.LanguagePicker.ItemsSource).First(item => item.Code == option.Code);
                string DialogText(string key) => Localization.Get(key, option.Code);
                Check($"Vista previa de configuración inmediata: {option.Code}", preferences.Title?.ToString() == DialogText("Preferences")
                    && preferences.PrimaryButtonText == DialogText("Save") && preferences.CloseButtonText == DialogText("Cancel")
                    && preferences.LanguagePicker.Header?.ToString() == DialogText("Language") && preferences.ThemePicker.Header?.ToString() == DialogText("Theme")
                    && preferences.RemoteImages.Header?.ToString() == DialogText("RemoteImages") && preferences.RemoteImages.OnContent?.ToString() == DialogText("On")
                    && preferences.RemoteImages.OffContent?.ToString() == DialogText("Off") && preferences.TextSize.Header?.ToString() == DialogText("FontSize")
                    && (preferences.DefaultAppPrompt.Content as TextBlock)?.Text == DialogText("DefaultAppPrompt"));
                Check($"Vista previa conserva opciones pendientes: {option.Code}", preferences.ThemePicker.SelectedIndex == 2 && !preferences.RemoteImages.IsOn
                    && preferences.TextSize.Value == 23 && preferences.DefaultAppPrompt.IsChecked == false
                    && (preferences.LanguagePicker.SelectedItem as LanguageOption)?.Code == option.Code);
                Check($"Vista previa no modifica app ni preferencias: {option.Code}", Localization.CurrentLanguage == appLanguage
                    && Root.Children.OfType<MenuBar>().First().Items[0].Title == appMenu
                    && previewSettings is { Language: "es", Theme: "Light", RemoteImages: true, FontSize: 17, PromptForDefaultApp: true });
                Check($"Dirección de vista previa correcta: {option.Code}", preferences.FlowDirection == (option.Code == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight));
            }
            preferences.LanguagePicker.SelectedItem = ((IEnumerable<LanguageOption>)preferences.LanguagePicker.ItemsSource).First(item => item.Code == "auto");
            string automaticLanguage = Localization.ResolveLanguage("auto");
            Check("Vista previa de idioma automático mantiene selección", (preferences.LanguagePicker.SelectedItem as LanguageOption)?.Code == "auto"
                && preferences.Title?.ToString() == Localization.Get("Preferences", automaticLanguage)
                && ((IEnumerable<LanguageOption>)preferences.LanguagePicker.ItemsSource).First().NativeName == Localization.Get("SystemLanguage", automaticLanguage));
            var reopenedPreferences = new ReadingPreferencesDialog(previewSettings);
            Check("Descartar vista previa conserva preferencias originales", (reopenedPreferences.LanguagePicker.SelectedItem as LanguageOption)?.Code == "es"
                && reopenedPreferences.ThemePicker.SelectedIndex == 1 && reopenedPreferences.RemoteImages.IsOn && reopenedPreferences.TextSize.Value == 17
                && reopenedPreferences.DefaultAppPrompt.IsChecked == true && Localization.CurrentLanguage == appLanguage);
            preferences.LanguagePicker.SelectedItem = ((IEnumerable<LanguageOption>)preferences.LanguagePicker.ItemsSource).First(item => item.Code == "en");
            preferences.ApplyTo(previewSettings);
            Check("Guardar aplica idioma y opciones de la vista previa", previewSettings is { Language: "en", Theme: "Dark", RemoteImages: false, FontSize: 23, PromptForDefaultApp: false });
            var previousSettings = JsonSerializer.Deserialize<ReaderSettings>("{\"NavigationOpen\":false}");
            Check("Preferencias anteriores habilitan sugerencia de app predeterminada", previousSettings is { NavigationOpen: false, PromptForDefaultApp: true });
            var rememberedSettings = JsonSerializer.Deserialize<ReaderSettings>(JsonSerializer.Serialize(new ReaderSettings { PromptForDefaultApp = false }));
            Check("No volver a preguntar se conserva en preferencias", rememberedSettings is { PromptForDefaultApp: false });
            Check("Configuración sin identidad abre página general", DefaultAppService.GetSettingsUri(null).AbsoluteUri == "ms-settings:defaultapps");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                const string appId = "Contoso.Reader_test!App";
                var settingsUri = DefaultAppService.GetSettingsUri(appId);
                Check("Configuración de app conserva identidad escapada", settingsUri.Scheme == "ms-settings" && Uri.UnescapeDataString(settingsUri.Query) == $"?registeredAUMID={appId}");
            }
            const string sample = """
                ---
                title: Prueba
                ---
                # Introducción

                ## Repetido

                ## Repetido

                **Negrita** y *cursiva*, ~~tachado~~, ++subrayado++, ==marca==, H~2~O, x^2^, `inline` y &amp;.

                [Enlace](https://example.com) y [ancla](#introducción).

                - [x] Hecho
                - [ ] Pendiente
                  - Anidado

                > Cita **con formato**.

                | Nombre | Valor |
                | :--- | ---: |
                | ejemplo | **42** |

                ```csharp
                var prueba = "código";
                ```

                Término

                :   Una definición.

                Texto con nota[^uno].

                ![Remota de prueba](https://example.invalid/test.png)

                <script>alert('no ejecutar')</script>

                [^uno]: Nota al pie.
                """;
            var parsed = MarkdownService.Parse(sample);
            var headings = MarkdownService.GetHeadings(parsed);
            Check("Encabezados con acentos", headings.Count == 3 && headings[0].Title == "Introducción");
            Check("Identificadores únicos en encabezados repetidos", headings.Select(h => h.Id).Distinct().Count() == 3);
            Check("Análisis de tablas", parsed.Descendants<Table>().Count() == 1);
            Check("Análisis de tareas", parsed.Descendants<TaskList>().Count() == 2);
            Check("Análisis de notas al pie", parsed.Descendants<FootnoteGroup>().Count() == 1);
            Check("Análisis de definiciones", parsed.Descendants<DefinitionList>().Count() == 1);
            Check("Extensiones de archivo", MarkdownService.IsMarkdown("LEEME.MD") && MarkdownService.IsMarkdown("a.markdown") && !MarkdownService.IsMarkdown("a.exe"));
            string basePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Welcome.md");
            var relative = LinkService.Resolve("sub/otro%20archivo.md#seccion", basePath);
            Check("Enlaces relativos con espacios y ancla", relative is { IsFile: true } && relative.LocalPath.EndsWith(Path.Combine("sub", "otro archivo.md")) && relative.Fragment == "#seccion");
            Check("Sin carpeta base para Markdown pegado", LinkService.Resolve("foto.png", null) is null);
            Check("Identificación de enlaces web", LinkService.IsWeb(new Uri("https://example.com")) && !LinkService.IsWeb(new Uri("javascript:alert(1)")));
            bool blocked = false;
            try { await LinkService.OpenExternalAsync(new Uri("javascript:alert(1)"), false); }
            catch (InvalidOperationException) { blocked = true; }
            Check("Bloqueo de protocolos ejecutables", blocked);

            _settings.RemoteImages = false;
            Navigate(new ReadingDocument(sample, null, "Prueba nativa"), parsed);
            DocumentPanel.UpdateLayout();
            Check("Renderizador nativo creado", _renderer is not null && DocumentPanel.Children.Count > 10);
            Check("Índice nativo", OutlineList.Items.Count == 3);
            Check("Anclas de encabezados", _renderer!.Anchors.ContainsKey(headings[0].Id));
            Check("Anclas de notas al pie", _renderer.Anchors.ContainsKey("fn:uno") && _renderer.Anchors.ContainsKey("fnref:uno"), string.Join(", ", _renderer.Anchors.Keys));

            IEnumerable<FrameworkElement> Descendants(FrameworkElement root)
            {
                yield return root;
                IEnumerable<FrameworkElement> children = root switch
                {
                    Panel panel => panel.Children.OfType<FrameworkElement>(),
                    Border border when border.Child is FrameworkElement child => [child],
                    ScrollViewer scroll when scroll.Content is FrameworkElement child => [child],
                    _ => []
                };
                foreach (var child in children)
                    foreach (var descendant in Descendants(child)) yield return descendant;
            }
            var elements = Descendants(DocumentPanel).ToList();
            var richBlocks = elements.OfType<RichTextBlock>().ToList();
            Check("Párrafos nativos seleccionables", richBlocks.Count > 10 && richBlocks.All(t => t.IsTextSelectionEnabled));
            var linkedBlocks = richBlocks.Where(t => t.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Hyperlink>().Any())).ToList();
            Check("Menús de enlaces nativos", linkedBlocks.Count > 0 && linkedBlocks.All(t => t.ContextFlyout is MenuFlyout));
            bool HasBrowserChoice(IEnumerable<MenuFlyoutItemBase> items) => items.Any(i => i is MenuFlyoutItem item && item.Text == "Abrir con…" || i is MenuFlyoutSubItem sub && HasBrowserChoice(sub.Items));
            Check("Abrir con en menú de enlace web", linkedBlocks.Any(t => t.ContextFlyout is MenuFlyout menu && HasBrowserChoice(menu.Items)));
            Check("Casillas de tareas de solo lectura", elements.OfType<CheckBox>().Count() == 2 && elements.OfType<CheckBox>().All(c => !c.IsHitTestVisible));
            Check("Código copiable", elements.OfType<Button>().Count(b => Equals(b.Content, "Copiar")) >= 2);
            var tableGrid = elements.OfType<Grid>().FirstOrDefault(g => g.ColumnDefinitions.Count == 2 && g.RowDefinitions.Count == 2 && g.Children.OfType<Border>().Count() == 4);
            Check("Celdas de tabla en columnas distintas", tableGrid is not null && tableGrid.Children.OfType<Border>().Count(b => Grid.GetColumn(b) == 1) == 2,
                JsonSerializer.Serialize(elements.OfType<Grid>().Where(g => g.Children.OfType<Border>().Any()).Select(g => new { columns = g.ColumnDefinitions.Count, rows = g.RowDefinitions.Count, cells = g.Children.OfType<Border>().Select(b => new { column = Grid.GetColumn(b), span = Grid.GetColumnSpan(b) }) })));
            Check("HTML preservado como texto", elements.OfType<TextBlock>().Any(t => t.Text.Contains("<script>")));
            Check("Búsqueda en documento", _renderer.Search("definición").Count == 1);
            Check("Búsqueda sin distinción de mayúsculas", _renderer.Search("NEGRITA").Count == 1);
            Check("Búsqueda sin coincidencias", _renderer.Search("zzzzzz-no-existe").Count == 0);
            _renderer.Search("");
            Check("Búsqueda limpia resaltados", richBlocks.All(t => t.TextHighlighters.Count == 0));

            SetSource(true);
            Check("Vista fuente", SourceScroll.Visibility == Visibility.Visible && SourceText.Text == sample);
            SetSource(false);
            JumpToAnchor(headings[0].Id);
            Check("Navegación por anclas", ReaderScroll.Visibility == Visibility.Visible);

            int languageHistory = _history.Count;
            SearchBox.Text = "definición";
            foreach (var option in Localization.Languages)
            {
                _settings.Language = option.Code;
                ApplyLanguage();
                DocumentPanel.UpdateLayout();
                Check($"Cambio inmediato de interfaz: {option.Code}", Root.Children.OfType<MenuBar>().First().Items[0].Title == Localization.Get("MenuFile")
                    && NavigationPivot.Items.OfType<PivotItem>().First().Header?.ToString() == Localization.Get("Files")
                    && SearchBox.PlaceholderText == Localization.Get("Find") && SidebarMenu.Text == Localization.Get("Navigation"));
                Check($"Documento e historial conservados: {option.Code}", _document?.Source == sample && SourceText.Text == sample && _history.Count == languageHistory && _matches.Count == 1 && SearchBox.Text == "definición");
                Check($"Dirección de interfaz correcta: {option.Code}", Root.FlowDirection == (option.Code == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight)
                    && DocumentPanel.FlowDirection == FlowDirection.LeftToRight && SourceText.FlowDirection == FlowDirection.LeftToRight);
                var languageElements = Descendants(DocumentPanel).ToList();
                Check($"Controles del lector traducidos: {option.Code}", languageElements.OfType<Button>().Any(button => Equals(button.Content, Localization.Get("Copy")))
                    && _renderer!.Anchors.ContainsKey(headings[0].Id));
                Check($"Guía del historial traducida: {option.Code}", _history.First(entry => entry.Document.Kind == DocumentKind.Welcome).Document.Source.StartsWith("# " + Localization.Get("WelcomeTitle")));
            }
            SearchBox.Text = "";
            _settings.Language = "es";
            ApplyLanguage();

            int previous = _historyIndex;
            Navigate(new ReadingDocument("# Segundo", null, "Segundo"), MarkdownService.Parse("# Segundo"));
            GoHistory(-1);
            Check("Historial hacia atrás", _historyIndex == previous && _document?.Source == sample);
            GoHistory(1);
            Check("Historial hacia adelante", _document?.Name == "Segundo");

            var missing = Path.Combine(AppContext.BaseDirectory, "no-existe.md");
            await OpenFileAsync(missing);
            Check("Error de archivo sin cerrar app", _document?.Name == "Segundo" && Notice.IsOpen);

            await OpenFolderAsync(Path.Combine(AppContext.BaseDirectory, "Assets"), false);
            Check("Carpeta y filtro Markdown", FileTree.RootNodes.Count >= 1 && FileTree.RootNodes.All(n => n.Content is Models.FileTreeItem item && (item.IsDirectory || MarkdownService.IsMarkdown(item.Path))));
            await OpenFileAsync(basePath);
            Check("Lectura real de archivo UTF-8", _document?.Path == basePath && _document.Source.Contains("Bienvenido a Amoxtli"));
            Check("Registro del vigilante de cambios", _watcher?.EnableRaisingEvents == true);
            int folderItems = FileTree.RootNodes.Count;
            _settings.Language = "en";
            ApplyLanguage();
            Check("Cambio de idioma conserva archivo y carpeta", _document?.Path == basePath && DocumentName.Text == "Welcome.md" && DocumentPath.Text == basePath
                && FolderName.Text == "Assets" && FileTree.RootNodes.Count == folderItems && _watcher?.EnableRaisingEvents == true);
            _settings.Language = "es";
            ApplyLanguage();

            Image? InlineImage() => DocumentPanel.Children.OfType<RichTextBlock>()
                .SelectMany(t => t.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<InlineUIContainer>())
                .Select(i => i.Child).OfType<StackPanel>().SelectMany(p => p.Children.OfType<Image>()).FirstOrDefault();
            async Task<bool> WaitForImageAsync()
            {
                for (int attempt = 0; attempt < 60; attempt++)
                {
                    if (InlineImage() is { Source: not null, Visibility: Visibility.Visible }) return true;
                    await Task.Delay(50);
                }
                return false;
            }
            string localSource = "![Imagen local](Square44x44Logo.scale-200.png)";
            Navigate(new ReadingDocument(localSource, basePath, "Imagen local"), MarkdownService.Parse(localSource));
            Check("Carga real de imagen local", await WaitForImageAsync());

            string disabledSource = "![Imagen remota](https://example.invalid/image.png)";
            Navigate(new ReadingDocument(disabledSource, null, "Imagen desactivada"), MarkdownService.Parse(disabledSource));
            Check("Imágenes remotas desactivadas", InlineImage() is { Source: null, Visibility: Visibility.Collapsed });

            // Exercise HTTP loading against a single-use loopback server, without contacting third parties.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            byte[] picture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-200.png"));
            var response = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = client.GetStream();
                _ = await stream.ReadAsync(new byte[8192], deadline.Token);
                byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {picture.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, deadline.Token); await stream.WriteAsync(picture, deadline.Token);
            });
            _settings.RemoteImages = true;
            string remoteSource = $"![Imagen HTTP](http://127.0.0.1:{port}/image.png)";
            Navigate(new ReadingDocument(remoteSource, null, "Imagen HTTP"), MarkdownService.Parse(remoteSource));
            Check("Carga real de imagen por HTTP", await WaitForImageAsync());
            await response;
        }
        catch (Exception ex) { failed++; results.Add(new { name = "Excepción inesperada", passed = false, detail = ex.ToString() }); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
#endif
