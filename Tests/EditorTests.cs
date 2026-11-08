#if DEBUG
using Amoxtli.Controls;
using Amoxtli.Editing;
using Amoxtli.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using System.Text;
using Windows.System;

namespace Amoxtli;

// Source editor: pure editing logic first, then the editor inside this window.
// Text uses ‸ for the caret and «» for a selection.
public sealed partial class MainWindow
{
    private async Task RunEditorTestsAsync(Action<string, bool, string?> check)
    {
        void Check(string name, bool condition, string? detail = null) => check(name, condition, detail);
        static (string Text, int Start, int Length) Mark(string marked)
        {
            int start = marked.IndexOf('«');
            if (start >= 0)
            {
                int end = marked.IndexOf('»');
                return (marked.Remove(end, 1).Remove(start, 1), start, end - start - 1);
            }
            int caret = marked.IndexOf('‸');
            return (marked.Remove(caret, 1), caret, 0);
        }
        static string Show(string text, TextEdit? edit)
        {
            if (edit is null) return "(sin cambio)";
            string result = edit.Apply(text);
            return edit.SelectionLength == 0 ? result.Insert(edit.SelectionStart, "‸")
                : result.Insert(edit.SelectionStart + edit.SelectionLength, "»").Insert(edit.SelectionStart, "«");
        }
        static string Tool(string marked, MarkdownTool tool) { var (t, s, l) = Mark(marked); return Show(t, SourceFormatter.Toggle(t, s, l, tool)); }
        static string Heading(string marked, int level) { var (t, s, l) = Mark(marked); return Show(t, SourceFormatter.SetHeading(t, s, l, level)); }
        static string Return(string marked) { var (t, s, l) = Mark(marked); var e = SourceFormatter.ContinueOnReturn(t, s, l); return e is null ? "normal" : Show(t, e); }
        void Expect(string name, string actual, string expected) => Check(name, actual == expected, $"Obtenido: {actual.Replace("\n", "⏎")} · Esperado: {expected.Replace("\n", "⏎")}");

        // Minimal difference
        var insert = TextDiff.Compute("abc", "abXc", 3);
        Check("Diferencia mínima: inserción", insert is { Start: 2, Length: 0, Text: "X" });
        Check("Diferencia mínima: borrado", TextDiff.Compute("abc", "ac", 1) is { Start: 1, Length: 1, Text: "" });
        Check("Diferencia mínima: reemplazo", TextDiff.Compute("hola mundo", "hola gente", 10) is { Start: 5, Length: 5, Text: "gente" });
        Check("Diferencia mínima: no parte pares UTF-16", TextDiff.Compute("a😀", "a😃", 3) is { Start: 1, Length: 2, Text: "😃" });
        Check("Diferencia mínima: escribir la misma letra", TextDiff.Compute("aa", "aaa", 2).Apply("aa") == "aaa");

        // Inline scanner
        var nested = InlineScanner.Scan("***a** b*");
        Check("Cursiva junto a negrita", nested.Any(s => s is { Kind: InlineKind.Italic, Start: 0, End: 9 }) && nested.Any(s => s is { Kind: InlineKind.Bold, Start: 1, End: 6 }),
            string.Join(", ", nested));
        Check("Nada es Markdown dentro de código", InlineScanner.Scan("`**no**`") is [{ Kind: InlineKind.Code }]);
        Check("Espacio junto al marcador no es negrita", !InlineScanner.Scan("** a**").Any(s => s.Kind == InlineKind.Bold));
        Check("Guion bajo dentro de palabra no es cursiva", InlineScanner.Scan("a_b_c").Count == 0);
        Check("Enlaces con texto y dirección", InlineScanner.Scan("[t](u)") is [{ Kind: InlineKind.Link, Start: 0, End: 6, Open: 1, Close: 4 }]);
        Check("Subrayado y tachado", InlineScanner.Scan("<u>a</u> ~~b~~").Select(s => s.Kind).SequenceEqual([InlineKind.Underline, InlineKind.Strikethrough]));

        // Highlighter
        var highlighter = new SourceHighlighter();
        highlighter.Reset("# Título\n- uno\n> cita\ntexto");
        var headingSpans = highlighter.GetSpans(0);
        Check("Resaltado de título y marcas", headingSpans.Any(s => s is { Kind: HighlightKind.Heading, Level: 1 }) && headingSpans.Any(s => s is { Kind: HighlightKind.Syntax, Start: 0, Length: 1 }));
        Check("Resaltado de marcador de lista", highlighter.GetSpans(1).Any(s => s is { Kind: HighlightKind.ListMarker, Start: 9, Length: 1 }));
        Check("Resaltado de cita", highlighter.GetSpans(2).Any(s => s.Kind == HighlightKind.Quote));
        string fenced = "```\n" + highlighter.Text;
        var range = highlighter.Update(fenced, new TextEdit(0, 0, "```\n", 4, 0));
        Check("Abrir un bloque reescanea lo que sigue", range == (0, 4) && Enumerable.Range(0, 5).All(highlighter.IsCode), range.ToString());
        string closed = fenced.Replace("- uno", "```");
        range = highlighter.Update(closed, TextDiff.Compute(fenced, closed));
        Check("Cerrar un bloque quita el código de lo que sigue", range.Last == 4 && highlighter.IsCode(2) && !highlighter.IsCode(3) && !highlighter.IsCode(4), range.ToString());
        string typed = closed + "s";
        range = highlighter.Update(typed, TextDiff.Compute(closed, typed));
        Check("Escribir reescanea solo su línea", range == (4, 4), range.ToString());

        // Formatting tools
        Expect("Negrita aplica", Tool("«hola»", MarkdownTool.Bold), "**«hola»**");
        Expect("Negrita quita", Tool("**«hola»**", MarkdownTool.Bold), "«hola»");
        Expect("Negrita quita con el cursor dentro", Tool("**ho‸la**", MarkdownTool.Bold), "ho‸la");
        Expect("Negrita sin selección", Tool("a‸b", MarkdownTool.Bold), "a**‸**b");
        Expect("Espacios fuera de los marcadores", Tool("« hola »", MarkdownTool.Bold), "« **hola** »");
        Expect("Negrita en varias líneas", Tool("«uno\ndos»", MarkdownTool.Bold), "**«uno**\n**dos»**");
        Expect("Cursiva", Tool("«x»", MarkdownTool.Italic), "*«x»*");
        Expect("Subrayado", Tool("«x»", MarkdownTool.Underline), "<u>«x»</u>");
        Expect("Tachado", Tool("«x»", MarkdownTool.Strikethrough), "~~«x»~~");
        Expect("Código", Tool("«x»", MarkdownTool.Code), "`«x»`");
        Expect("Offsets UTF-16", Tool("a «ñ😀» b", MarkdownTool.Bold), "a **«ñ😀»** b");
        Expect("Título", Heading("Hola‸", 2), "## Hola‸");
        Expect("Cambiar nivel de título", Heading("# a‸", 3), "### a‸");
        Expect("Estilo actual no cambia", Heading("## Hola‸", 2), "(sin cambio)");
        Expect("Texto quita el título", Heading("## Hola‸", 0), "Hola‸");
        Expect("Viñetas en línea vacía", Tool("‸", MarkdownTool.BulletList), "- ‸");
        Expect("Numerada en línea vacía", Tool("‸", MarkdownTool.NumberedList), "1. ‸");
        Expect("Cita en línea vacía", Tool("‸", MarkdownTool.Quote), "> ‸");
        Expect("Viñetas aplica", Tool("uno‸", MarkdownTool.BulletList), "- uno‸");
        Expect("Viñetas quita", Tool("- uno‸", MarkdownTool.BulletList), "uno‸");
        Expect("Viñetas omite líneas vacías", Tool("«uno\n\ndos»", MarkdownTool.BulletList), "- «uno\n\n- dos»");
        Expect("Numerada reemplaza viñetas", Tool("«- a\n- b»", MarkdownTool.NumberedList), "1. «a\n2. b»");
        Expect("Cita quita", Tool("> cita‸", MarkdownTool.Quote), "cita‸");
        Expect("Cita en varios párrafos", Tool("«a\n\nb»", MarkdownTool.Quote), "> «a\n>\n> b»");
        Expect("Bloque de código en línea vacía", Tool("‸", MarkdownTool.CodeBlock), "```\n‸\n```");
        Expect("Bloque de código envuelve la línea", Tool("x‸", MarkdownTool.CodeBlock), "```\nx‸\n```");
        Expect("Bloque de código quita", Tool("a\n```\nx‸\n```\nb", MarkdownTool.CodeBlock), "a\nx‸\nb");
        Expect("Enlace quita", Tool("[ho‸la](u)", MarkdownTool.Link), "ho‸la");
        var (linkText, linkStart, linkLength) = Mark("ver «sitio»");
        Expect("Enlace inserta", Show(linkText, SourceFormatter.InsertLink(linkText, linkStart, linkLength, "sitio", "https://a.com")), "ver [sitio](https://a.com)‸");
        Check("Dirección vacía no se inserta", SourceFormatter.NormalizeAddress("  ") is null);
        Check("Dirección con esquema o ancla queda igual", SourceFormatter.NormalizeAddress("https://a.com") == "https://a.com" && SourceFormatter.NormalizeAddress("#sec") == "#sec"
            && SourceFormatter.NormalizeAddress("/x") == "/x" && SourceFormatter.NormalizeAddress("./a.md") == "./a.md" && SourceFormatter.NormalizeAddress("mailto:a@b.c") == "mailto:a@b.c");
        Check("Dirección de correo", SourceFormatter.NormalizeAddress("ana@ejemplo.com") == "mailto:ana@ejemplo.com");
        Check("Dirección web sin esquema", SourceFormatter.NormalizeAddress("ejemplo.com/a b") == "https://ejemplo.com/a%20b");
        var table = SourceFormatter.InsertTable("", 0, 0, 2, 2);
        Expect("Tabla", Show("", table), "| ‸  |   |\n| --- | --- |\n|   |   |");
        var bigTable = SourceFormatter.InsertTable("", 0, 0, 50, 500).Apply("");
        Check("Límites de tabla", bigTable.Split('\n').Length == 101 && bigTable.Split('\n')[0].Count(c => c == '|') == 21);
        Expect("Línea divisoria separada del texto", Show("a\nb", SourceFormatter.InsertThematicBreak("a\nb", 1, 0)), "a\n\n---‸\n\nb");
        Expect("Línea divisoria en línea vacía", Show("a\n", SourceFormatter.InsertThematicBreak("a\n", 2, 0)), "a\n\n---‸");

        // Active state
        FormatState State(string marked) { var (t, s, l) = Mark(marked); return SourceFormatter.GetState(t, s, l); }
        Check("Estado: negrita en el cursor", State("**ho‸la**").Has(MarkdownTool.Bold) && !State("**hola** ‸x").Has(MarkdownTool.Bold));
        Check("Estado: título", State("## T‸").HeadingLevel == 2 && State("T‸").HeadingLevel == 0);
        Check("Estado: cita y viñetas", State("> - a‸").Has(MarkdownTool.Quote) && State("> - a‸").Has(MarkdownTool.BulletList) && !State("> - a‸").Has(MarkdownTool.NumberedList));
        Check("Estado: bloque de código", State("```\na‸\n```").Has(MarkdownTool.CodeBlock) && !State("a‸").Has(MarkdownTool.CodeBlock));
        Check("Estado: toda la selección", State("«**a** **b**»") is var mixed && !mixed.Has(MarkdownTool.Bold) && State("«**a**\n**b**»").Has(MarkdownTool.Bold));

        // Return in lists and quotes (port document, section 3.2)
        foreach (var (before, after) in new[]
        {
            ("- uno‸", "- uno\n- ‸"), ("* uno‸", "* uno\n* ‸"), ("1. uno‸", "1. uno\n2. ‸"), ("9) nueve‸", "9) nueve\n10) ‸"),
            ("  - anidado‸", "  - anidado\n  - ‸"), ("> - en cita‸", "> - en cita\n> - ‸"), ("> cita‸", "> cita\n> ‸"),
            ("- uno‸ dos", "- uno\n- ‸ dos"), ("- uno\n- ‸", "- uno\n‸"), ("> - ‸", "> ‸"), ("> ‸", "‸"), ("- [x] hecho‸", "- [x] hecho\n- [ ] ‸")
        })
            Expect($"Return: {before.Replace("\n", "⏎")}", Return(before), after);
        foreach (var plain in new[] { "texto‸", "# Título‸", "-sin espacio‸", "1.5 no es lista‸", "-‸ uno", "```\n- uno‸\n```", "«- uno\n- dos»" })
            Expect($"Return normal: {plain.Replace("\n", "⏎")}", Return(plain), "normal");

        // Undo history
        var history = new EditHistory();
        var clock = DateTime.UtcNow;
        string typing = "";
        foreach (char c in "hola mundo")
        {
            var edit = new TextEdit(typing.Length, 0, c.ToString(), typing.Length + 1, 0);
            history.Record(typing, edit, true, clock);
            typing = edit.Apply(typing);
        }
        var undoWord = history.Undo();
        Check("Deshacer agrupa la escritura por palabra", undoWord is not null && undoWord.Apply(typing) == "hola ");
        Check("Deshacer la palabra anterior", history.Undo() is { } first && first.Apply("hola ") == "" && !history.CanUndo);
        Check("Rehacer", history.Redo() is { } redone && redone.Apply("") == "hola " && history.CanRedo);
        var tool = SourceFormatter.Toggle("hola", 0, 4, MarkdownTool.Bold)!;
        history.Clear();
        history.Record("hol", new TextEdit(3, 0, "a", 4, 0), true, clock);
        history.Record("hola", tool, false, clock);
        Check("Cada herramienta es un paso de deshacer", history.Undo()!.Apply(tool.Apply("hola")) == "hola" && history.Undo()!.Apply("hola") == "hol");

        // Files
        Check("UTF-8 con BOM y CRLF", DocumentFile.Decode([0xEF, 0xBB, 0xBF, (byte)'a', 13, 10]) is ("a\n", { ByteOrderMark: true, CrLf: true }));
        Check("Guardar conserva BOM y CRLF", DocumentFile.Encode("a\nb", DocumentFile.Decode([0xEF, 0xBB, 0xBF, (byte)'a', 13, 10]).Format).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 13, 10, (byte)'b' }));
        bool invalid = false;
        try { DocumentFile.Decode([(byte)'#', 0xC3, 0x28]); } catch (InvalidDataException) { invalid = true; }
        Check("UTF-8 inválido se rechaza", invalid);

        // The editor in this window
        _settings.Language = "es";
        ApplyLanguage();
        const string sample = "# Título\n\n- uno\n\nTexto **negrita** y `código` <u>subrayado</u>.\n";
        Navigate(new ReadingDocument(sample, null, "Editor"), MarkdownService.Parse(sample));
        SetMode(EditorMode.Split);
        Check("Dividido muestra ambos editores", SourceHost.Visibility == Visibility.Visible && ReaderScroll.Visibility == Visibility.Visible && SplitDivider.Visibility == Visibility.Visible);
        SetMode(EditorMode.Source);
        Check("Modo fuente carga el documento", _sourceEditor.Text == sample && ModeSourceButton.IsChecked == true && ViewSourceMenu.IsChecked && ReaderScroll.Visibility == Visibility.Collapsed);
        Check("El control devuelve el mismo texto", _sourceEditor.ReadBox() == sample, _sourceEditor.ReadBox().Replace("\n", "⏎"));
        foreach (var variant in new[] { "", "a", "a\n", "a\n\n", "\n" })
        {
            _sourceEditor.Load(variant);
            Check($"Saltos finales intactos: {variant.Replace("\n", "⏎")}", _sourceEditor.ReadBox() == variant, _sourceEditor.ReadBox().Replace("\n", "⏎"));
        }
        // Typing through the text box itself goes through change detection, not Apply.
        _sourceEditor.Load("uno\n");
        _sourceEditor.Select(3, 0);
        _sourceEditor.Box.Document.Selection.TypeText("s");
        await Task.Delay(50);
        Check("Escribir conserva el salto final", _sourceEditor.Text == "unos\n" && _sourceEditor.ReadBox() == "unos\n" && _sourceEditor.CanUndo, _sourceEditor.Text.Replace("\n", "⏎"));
        _sourceEditor.Select(4, 0);
        _sourceEditor.Box.Document.Selection.TypeText("\r");
        await Task.Delay(50);
        Check("Escribir un salto al final", _sourceEditor.Text == "unos\n\n" && _document?.Source == "unos\n\n", _sourceEditor.Text.Replace("\n", "⏎"));
        Navigate(new ReadingDocument(sample, null, "Editor"), MarkdownService.Parse(sample));
        ITextCharacterFormat Format(string text, int offset = 0) => _sourceEditor.Box.Document.GetRange(sample.IndexOf(text) + offset, sample.IndexOf(text) + offset + 1).CharacterFormat;
        Check("Resaltado: negrita", Format("negrita").Bold == FormatEffect.On && Format("Texto").Bold == FormatEffect.Off);
        Check("Resaltado: título más grande", Format("Título").Size > (float)_settings.FontSize && Format("Título").Bold == FormatEffect.On);
        Check("Resaltado: código monoespaciado", Format("código").Name == SourceEditor.MonospaceFont);
        await Task.Delay(300);
        Check("El resaltado sobrevive a la carga del control", _sourceEditor.Box.IsLoaded && Format("código").Name == SourceEditor.MonospaceFont, Format("código").Name);
        Check("Subrayado sin subrayar las marcas", Format("<u>").Underline == UnderlineType.None && Format("subrayado").Underline == UnderlineType.Single);
        Check("Resaltado: marcas en gris", !Format("# ").ForegroundColor.Equals(Format("Título").ForegroundColor) && !Format("**").ForegroundColor.Equals(Format("negrita").ForegroundColor));
        Check("Herramientas habilitadas con editor", FormatBar.IsEnabled);

        int word = sample.IndexOf("Texto");
        _sourceEditor.Select(word, 5);
        Check("Atajo de negrita", TryShortcut(VirtualKey.B, VirtualKeyModifiers.Control) && _sourceEditor.Text.Contains("**Texto** **negrita**"));
        Check("Editar actualiza el documento", _document?.Source == _sourceEditor.Text && _document.IsDirty && DocumentName.Text.StartsWith("• "));
        UpdateToolStates();
        var boldButton = FormatBar.PrimaryCommands.OfType<AppBarToggleButton>().First(b => b.Label == Localization.Get("Bold"));
        Check("Estado activo en la barra", boldButton.IsChecked == true);
        Check("Estado activo en el menú", FormatMenu.Items.OfType<ToggleMenuFlyoutItem>().First(i => i.Text == Localization.Get("Bold")).IsChecked);
        _sourceEditor.Undo();
        Check("Deshacer la herramienta", _sourceEditor.Text == sample && _document?.IsDirty == false && !DocumentName.Text.StartsWith("• "));
        _sourceEditor.Redo();
        Check("Rehacer la herramienta", _sourceEditor.Text.Contains("**Texto**"));
        _sourceEditor.Undo();
        Check("Atajo de título", _sourceEditor.Selection.Start >= 0 && TryShortcut(VirtualKey.Number2, VirtualKeyModifiers.Control) && _sourceEditor.Text.Contains("## Texto"));
        _sourceEditor.Undo();
        _sourceEditor.Select(sample.IndexOf("uno") + 3, 0);
        _sourceEditor.InsertReturn();
        Check("Return continúa la lista en el editor", _sourceEditor.Text.Contains("- uno\n- \n") && _sourceEditor.Selection.Start == sample.IndexOf("uno") + 6);
        _sourceEditor.InsertReturn();
        Check("Return en ítem vacío termina la lista", _sourceEditor.Text.Contains("- uno\n\n\nTexto"));
        _sourceEditor.Undo(); _sourceEditor.Undo();
        Check("Return se deshace como una tecla", _sourceEditor.Text == sample);
        _sourceEditor.Apply(new TextEdit(0, 0, "```\n", 4, 0));
        Check("Abrir un bloque colorea lo que sigue", _sourceEditor.Box.Document.GetRange(sample.IndexOf("negrita") + 4, sample.IndexOf("negrita") + 5).CharacterFormat.Name == SourceEditor.MonospaceFont);
        _sourceEditor.Undo();
        Check("Cerrar el bloque quita el color", Format("negrita").Name != SourceEditor.MonospaceFont && Format("negrita").Bold == FormatEffect.On);

        SetMode(EditorMode.Split);
        _sourceEditor.Apply(new TextEdit(sample.Length, 0, "Nuevo párrafo dividido.\n", sample.Length + 24, 0));
        Check("Dividido conserva el editor y su deshacer", _sourceEditor.CanUndo && _sourceEditor.Text.EndsWith("Nuevo párrafo dividido.\n"));
        await Task.Delay(600);
        Check("Formato se actualiza tras editar", _renderer?.Search("párrafo dividido").Count == 1);
        _sourceEditor.Undo();
        SetMode(EditorMode.Format);
        Check("Formato deshabilita las herramientas", !FormatBar.PrimaryCommands.OfType<AppBarToggleButton>().Any(b => b.IsEnabled) && FormatMenu.Items.All(i => !i.IsEnabled));

        var tableDialog = new InsertTableDialog();
        tableDialog.Columns.Value = 25;
        Check("Tabla rechaza más de 20 columnas", !tableDialog.IsValid && !tableDialog.IsPrimaryButtonEnabled);
        tableDialog.Columns.Value = 4; tableDialog.Rows.Value = 0;
        Check("Tabla rechaza 0 filas", !tableDialog.IsValid);
        tableDialog.Rows.Value = 100;
        Check("Tabla acepta tamaños válidos", tableDialog.IsValid && tableDialog.IsPrimaryButtonEnabled);

        _settings.Language = "de";
        ApplyLanguage();
        var germanBold = FormatMenu.Items.OfType<ToggleMenuFlyoutItem>().First();
        Check("Menú Formato traducido con atajos locales", germanBold.Text == "Fett" && germanBold.KeyboardAcceleratorTextOverride == "Strg+B"
            && ToolTipService.GetToolTip(FormatBar.PrimaryCommands.OfType<AppBarToggleButton>().First())?.ToString() == "Fett (Strg+B)");
        _settings.Language = "es";
        ApplyLanguage();

        // Typography (port document, section 8). Applied directly so the tests never write preferences.
        Check("Tamaño se ajusta al paso más cercano", Typesetting.Snap(23) is 22 or 24 && Typesetting.Snap(13) == 13 && Typesetting.Snap(100) == 32 && Typesetting.Snap(double.NaN) == Typesetting.DefaultSize);
        Check("Pasos de tamaño con límites", Typesetting.Step(16, 1) == 17 && Typesetting.Step(32, 1) == 32 && Typesetting.Step(10, -1) == 10 && Typesetting.Step(20, -1) == 18);
        Check("Tipo de letra desconocido vuelve a Sistema", Typesetting.NormalizeTypeface("Rounded") == Typesetting.System && Typesetting.NormalizeTypeface(null) == Typesetting.System);
        Check("Atajos de tamaño", new[] { ((VirtualKey)187, VirtualKeyModifiers.Control), ((VirtualKey)187, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift), (VirtualKey.Add, VirtualKeyModifiers.Control),
            ((VirtualKey)189, VirtualKeyModifiers.Control), (VirtualKey.Number0, VirtualKeyModifiers.Control) }.All(_shortcuts.ContainsKey));
        Navigate(new ReadingDocument(sample, null, "Editor"), MarkdownService.Parse(sample));
        Check("Aa disponible en Formato sin las herramientas", _typographyButton.IsEnabled && !FormatBar.PrimaryCommands.OfType<AppBarToggleButton>().Any(b => b.IsEnabled));
        _settings.Typeface = Typesetting.Serif; _settings.FontSize = 20;
        ApplyTypography();
        var body = DocumentPanel.Children.OfType<RichTextBlock>().First(t => t.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Run>().Any(r => r.Text.StartsWith("Texto"))));
        var title = DocumentPanel.Children.OfType<RichTextBlock>().First();
        Check("Formato usa el tipo de letra y el tamaño", body.FontFamily.Source == "Georgia" && body.FontSize == 20 && title.FontSize > 20 && ZoomText.Text == Localization.Percent(20 / Typesetting.DefaultSize));
        Check("Aa marca el tipo de letra", _typefaceItems.Single(item => item.Item.IsChecked).Typeface == Typesetting.Serif);
        SetMode(EditorMode.Source);
        Check("Fuente usa el tipo de letra y el tamaño", _sourceEditor.Box.FontFamily.Source == "Georgia" && _sourceEditor.Box.FontSize == 20
            && _sourceEditor.Box.Document.GetRange(2, 3).CharacterFormat.Size > 20);
        _settings.Typeface = Typesetting.Monospace;
        ApplyTypography();
        Check("Fuente monoespaciada en todo el editor", _sourceEditor.Box.FontFamily.Source == Typesetting.FontFamily(Typesetting.Monospace));
        SetMode(EditorMode.Format);
        _settings.Typeface = Typesetting.System; _settings.FontSize = Typesetting.DefaultSize;
        ApplyTypography();

        string folder = Path.Combine(Path.GetTempPath(), "amoxtli-pruebas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "crlf.md");
            byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# A\r\n\r\nTexto\r\n")];
            await File.WriteAllBytesAsync(path, original);
            var written = File.GetLastWriteTimeUtc(path);
            await OpenFileAsync(path);
            Check("Abrir archivo con BOM y CRLF", _document is { Source: "# A\n\nTexto\n", Format: { ByteOrderMark: true, CrLf: true }, IsDirty: false });
            SetMode(EditorMode.Source);
            Check("Abrir o leer no reescribe el archivo", File.ReadAllBytes(path).SequenceEqual(original) && File.GetLastWriteTimeUtc(path) == written);
            _sourceEditor.Apply(new TextEdit(_sourceEditor.Text.Length, 0, "Más\n", _sourceEditor.Text.Length + 4, 0));
            byte[] saved = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# A\r\n\r\nTexto\r\nMás\r\n")];
            Check("Guardar", await SaveAsync(false) && File.ReadAllBytes(path).SequenceEqual(saved)
                && _document is { IsDirty: false } && !DocumentName.Text.StartsWith("• ") && !Directory.EnumerateFiles(folder, "*.tmp").Any());

            string bad = Path.Combine(folder, "malo.md");
            await File.WriteAllBytesAsync(bad, [(byte)'#', (byte)' ', 0xC3, 0x28]);
            Notice.IsOpen = false;
            await OpenFileAsync(bad);
            Check("UTF-8 inválido no se muestra ni se escribe", _document?.Path == path && Notice.IsOpen && Notice.Message.Contains(Localization.Get("InvalidEncoding"))
                && File.ReadAllBytes(bad).SequenceEqual(new byte[] { (byte)'#', (byte)' ', 0xC3, 0x28 }));

            _sourceEditor.Apply(new TextEdit(0, 0, "x", 1, 0));
            await File.WriteAllTextAsync(path, "otro");
            int asked = 0;
            ConflictResolver = (_, _) => { asked++; return Task.FromResult(ConflictChoice.KeepMine); };
            await ReloadCurrentAsync(true);
            ConflictResolver = null;
            Check("Cambio externo no reemplaza ediciones sin guardar", asked == 1 && _document is { IsDirty: true } && _sourceEditor.Text.StartsWith("x# A"));
            UpdateDocument(_document! with { Source = _document!.Saved });
            LoadEditor();
            await ReloadCurrentAsync(true);
            Check("Cambio externo recarga sin ediciones", _document is { Source: "otro", IsDirty: false } && _sourceEditor.Text == "otro");

            File.SetAttributes(path, FileAttributes.ReadOnly);
            _sourceEditor.Apply(new TextEdit(4, 0, " más", 8, 0));
            Check("Error de escritura explica y no guarda", !await SaveAsync(false) && Notice.Severity == InfoBarSeverity.Error && _document is { IsDirty: true }
                && File.ReadAllText(path) == "otro" && !Directory.EnumerateFiles(folder, "*.tmp").Any());
            File.SetAttributes(path, FileAttributes.Normal);

            string copy = Path.Combine(folder, "copia.md");
            Check("Guardar como", await SaveAsync(true, copy) && File.ReadAllText(copy) == "otro más" && _document is { Name: "copia.md", IsDirty: false } && _document.Path == copy);
        }
        finally
        {
            SetMode(EditorMode.Format);
            if (_document is { IsDirty: true } dirty) UpdateDocument(dirty with { Source = dirty.Saved });
            Navigate(new ReadingDocument("# Fin del editor", null, "Fin"), MarkdownService.Parse("# Fin del editor"));
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
#endif
