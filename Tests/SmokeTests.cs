#if DEBUG
using Amoxtli.Services;
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
