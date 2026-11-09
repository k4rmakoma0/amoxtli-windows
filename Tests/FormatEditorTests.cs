#if DEBUG
using Amoxtli.Controls;
using Amoxtli.Editing;
using Amoxtli.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Amoxtli;

// Format editor: structural operations on the model, then the block editor in this window.
public sealed partial class MainWindow
{
    private async Task RunFormatEditorTestsAsync(Action<string, bool, string?> check)
    {
        void Check(string name, bool condition, string? detail = null) => check(name, condition, detail);
        static string Md(IReadOnlyList<FormatParagraph> paragraphs) => FormatSerializer.Serialize(paragraphs);
        static string Show(string text) => text.Replace("\n", "⏎");
        void Expect(string name, string actual, string expected) => Check(name, actual == expected, $"Obtenido: {Show(actual)} · Esperado: {Show(expected)}");
        static List<FormatParagraph> P(string markdown) => FormatOperations.Ensure(FormatParser.Parse(markdown));
        // After a structural edit the editor rebuilds; wait until the focused field is laid out.
        async Task<RichField> FocusedFieldAsync()
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                if (_formatEditor.ActiveField is { IsRealized: true } field && field.Box.FocusState != FocusState.Unfocused) return field;
                await Task.Delay(50);
            }
            return _formatEditor.Fields.Last();
        }

        // Return
        var split = FormatOperations.Split(P("- uno"), 0, 3);
        Expect("Return en lista crea otro ítem", Md(split.Paragraphs), "- uno\n-\n");
        Check("Return en lista: el cursor va al ítem nuevo", split is { Focus: 1, Offset: 0 } && split.Paragraphs[1].Containers[0].ListId == split.Paragraphs[0].Containers[0].ListId
            && split.Paragraphs[1].Containers[0].Id != split.Paragraphs[0].Containers[0].Id);
        Expect("Return en ítem vacío sale de la lista", Md(FormatOperations.Split(split.Paragraphs, 1, 0).Paragraphs), "- uno\n");
        var nested = P("- a\n  - b\n  -");
        Expect("Return en ítem vacío anidado sube un nivel", Md(FormatOperations.Split(nested, 2, 0).Paragraphs.Select(p => p.Runs.Count == 0 ? p with { Runs = [new("c")] } : p).ToList()), "- a\n  - b\n- c\n");
        Expect("Return al final de un título sigue texto normal", Md(FormatOperations.Split(P("# Título"), 0, 6).Paragraphs.Select((p, i) => i == 1 ? p with { Runs = [new("texto")] } : p).ToList()), "# Título\n\ntexto\n");
        Expect("Return dentro de un título lo parte", Md(FormatOperations.Split(P("# AB"), 0, 1).Paragraphs), "# A\n\n# B\n");
        Expect("Return en tarea crea tarea pendiente", Md(FormatOperations.Split(P("- [x] hecha"), 0, 5).Paragraphs.Select((p, i) => i == 1 ? p with { Runs = [new("otra")] } : p).ToList()), "- [x] hecha\n- [ ] otra\n");

        // Backspace and Delete
        Expect("Borrar al inicio quita el título", Md(FormatOperations.Backspace(P("# T"), 0)!.Paragraphs), "T\n");
        Expect("Borrar al inicio quita la viñeta", Md(FormatOperations.Backspace(P("a\n\n- b"), 1)!.Paragraphs), "a\n\nb\n");
        Expect("Borrar al inicio quita la cita", Md(FormatOperations.Backspace(P("> c"), 0)!.Paragraphs), "c\n");
        var joined = FormatOperations.Backspace(P("uno\n\ndos"), 1)!;
        Check("Borrar al inicio une con el anterior", Md(joined.Paragraphs) == "unodos\n" && joined is { Focus: 0, Offset: 3 }, Md(joined.Paragraphs));
        Expect("Borrar al inicio quita una línea divisoria anterior", Md(FormatOperations.Backspace(P("a\n\n---\n\nb"), 2)!.Paragraphs), "a\n\nb\n");
        Expect("Suprimir al final une el siguiente", Md(FormatOperations.DeleteForward(P("uno\n\ndos"), 0)!.Paragraphs), "unodos\n");

        // Tab
        var indented = FormatOperations.Indent(P("- a\n- b"), 1, 0)!;
        Expect("Tab anida el ítem", Md(indented.Paragraphs), "- a\n  - b\n");
        Expect("Mayús+Tab lo desanida", Md(FormatOperations.Outdent(indented.Paragraphs, 1, 0)!.Paragraphs), "- a\n- b\n");
        Check("Tab sin ítem anterior no anida", FormatOperations.Indent(P("- a"), 0, 0) is null);
        Expect("Mayús+Tab en primer nivel sale de la lista", Md(FormatOperations.Outdent(P("- a"), 0, 0)!.Paragraphs), "a\n");
        Expect("Tab se une a la lista anidada existente", Md(FormatOperations.Indent(P("- a\n  - b\n- c"), 2, 0)!.Paragraphs), "- a\n  - b\n  - c\n");

        // Typed "# " and emptied elements
        Check("# tecleado hace un título", FormatOperations.TypedHeading(FormatParagraph.Plain("## Hola")) is { Kind: BlockKind.Heading, Level: 2, Text: "Hola" });
        Check("# sin espacio no es título", FormatOperations.TypedHeading(P("\\#Hola")[0]) is null && FormatOperations.TypedHeading(FormatParagraph.Plain("####### x")) is null);
        Check("\\# tecleado queda literal", FormatOperations.TypedEscapedHash(FormatParagraph.Plain("\\# x")) is { LiteralHash: true, Text: "# x" } literal
            && FormatOperations.TypedHeading(literal) is null && Md([literal]) == "\\# x\n");
        Check("# conserva estilos", FormatOperations.TypedHeading(new FormatParagraph { Runs = [new("# "), new("b", InlineStyle.Bold)] }) is { Runs: [{ Text: "b", Style: InlineStyle.Bold }] });
        Check("Título vaciado vuelve a texto", FormatOperations.Emptied(new FormatParagraph { Kind = BlockKind.Heading, Level = 1 }) is { Kind: BlockKind.Paragraph, Containers.Count: 0 });
        Check("Ítem vaciado vuelve a texto", FormatOperations.Emptied(P("- a")[0] with { Runs = [] }) is { Containers.Count: 0 });
        Check("Celda vaciada sigue siendo celda", FormatOperations.Emptied(new FormatParagraph { Kind = BlockKind.TableCell }) is null);

        // Tools on the model
        Expect("Viñetas se unen a la lista anterior", Md(FormatOperations.ToggleList(P("- a\n\nb"), 1, 0, false).Paragraphs), "- a\n- b\n");
        Expect("Numerada cambia toda la lista", Md(FormatOperations.ToggleList(P("- a\n- b"), 1, 0, true).Paragraphs), "1. a\n2. b\n");
        Expect("Viñetas quita la lista", Md(FormatOperations.ToggleList(P("- a"), 0, 0, false).Paragraphs), "a\n");
        Expect("Cita aplica y se une", Md(FormatOperations.ToggleQuote(P("> a\n\nb"), 1, 0).Paragraphs), "> a\n>\n> b\n");
        Expect("Bloque de código desde texto", Md(FormatOperations.ToggleCode(P("let a"), 0, 0).Paragraphs), "```\nlet a\n```\n");
        Expect("Bloque de código a texto", Md(FormatOperations.ToggleCode(P("```\na\nb\n```"), 1, 0).Paragraphs), "a\n\nb\n");
        Expect("Insertar tabla", Md(FormatOperations.InsertTable(P("a"), 0, 2, 2).Paragraphs), "a\n\n|  |  |\n| --- | --- |\n|  |  |\n");
        Check("Tabla al final lleva un párrafo después", FormatOperations.Ensure(FormatParser.Parse("| a |\n| --- |")) is [.., { Kind: BlockKind.TableCell }, { Kind: BlockKind.Paragraph }]);
        Expect("Insertar línea divisoria", Md(FormatOperations.InsertRule(P("a"), 0).Paragraphs), "a\n\n---\n");

        // The editor in this window
        _settings.Language = "es";
        ApplyLanguage();
        const string sample = "# Título\n\nUn párrafo con **negrita** y [enlace](https://a.com).\n\n- uno\n- [ ] tarea\n\n> Cita\n\n```js\nlet a = 1;\n```\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\nTexto[^1].\n\n[^1]: Nota al pie.\n";
        Navigate(new ReadingDocument(sample, null, "Formato"), MarkdownService.Parse(sample));
        SetMode(EditorMode.Format);
        Check("Formato muestra el editor", FormatEditing && FormatHost.Visibility == Visibility.Visible && ReaderScroll.Visibility == Visibility.Collapsed);
        await Task.Delay(400);
        var fields = _formatEditor.Fields;
        Check("Un campo por párrafo, bloque de código y celda", fields.Count == 1 + 1 + 2 + 1 + 1 + 4 + 1 + 1, fields.Count.ToString());
        Check("Abrir no cambia el texto", _formatEditor.Markdown == sample && _document?.IsDirty == false);
        var paragraph = fields[1];
        Check("Campos leen los mismos estilos", FormatRuns.Describe([new FormatParagraph { Runs = paragraph.ReadRuns() }])
            == FormatRuns.Describe([new FormatParagraph { Runs = FormatParser.Parse(sample)[1].Runs }]), Show(FormatRuns.Describe([new FormatParagraph { Runs = paragraph.ReadRuns() }])));

        paragraph.Box.Focus(FocusState.Programmatic);
        paragraph.Select(paragraph.Length);
        paragraph.Box.Document.Selection.TypeText(" Más");
        await Task.Delay(50);
        Check("Escribir actualiza el Markdown", _formatEditor.Markdown.Contains("[enlace](https://a.com). Más") && _document?.Source == _formatEditor.Markdown && _document.IsDirty);
        Check("Las notas al pie se conservan al editar", _formatEditor.Markdown.EndsWith("[^1]: Nota al pie.\n"));
        _formatEditor.Undo();
        Check("Deshacer la escritura", _formatEditor.Markdown == sample, Show(_formatEditor.Markdown));
        _formatEditor.Redo();
        Check("Rehacer la escritura", _formatEditor.Markdown.Contains(". Más"));
        _formatEditor.Undo();
        await Task.Delay(100);

        paragraph = _formatEditor.Fields[1];
        paragraph.Box.Focus(FocusState.Programmatic);
        paragraph.Select(0, 2);
        await Task.Delay(50);
        Check("Atajo de negrita en Formato", TryShortcut(VirtualKey.B, VirtualKeyModifiers.Control) && _formatEditor.Markdown.Contains("**Un** párrafo"), Show(_formatEditor.Markdown));
        UpdateToolStates();
        Check("Estado activo en Formato", FormatBar.PrimaryCommands.OfType<AppBarToggleButton>().First(b => b.Label == Localization.Get("Bold")).IsChecked == true);
        _formatEditor.Undo();
        await Task.Delay(100);

        paragraph = _formatEditor.Fields[1];
        paragraph.Box.Focus(FocusState.Programmatic);
        paragraph.Select(2);
        _formatEditor.Return(paragraph);
        await Task.Delay(200);
        Check("Return parte el párrafo", _formatEditor.Markdown.Contains("Un\n\npárrafo"), Show(_formatEditor.Markdown));
        var second = _formatEditor.Fields[2];
        second.Box.Focus(FocusState.Programmatic);
        second.Select(0);
        Check("Borrar al inicio vuelve a unir", _formatEditor.BackspaceAtStart(second) && _formatEditor.Markdown == sample, Show(_formatEditor.Markdown));
        await Task.Delay(200);

        var item = _formatEditor.Fields.First(f => f.ReadText() == "uno");
        item.Box.Focus(FocusState.Programmatic);
        item.Select(3);
        _formatEditor.Return(item);
        var empty = await FocusedFieldAsync();
        empty.Box.Document.Selection.TypeText("dos");
        await Task.Delay(50);
        Check("Return en lista y escribir", _formatEditor.Markdown.Contains("- uno\n- dos\n- [ ] tarea"), Show(_formatEditor.Markdown));
        Check("Tab anida en el editor", _formatEditor.Nest(_formatEditor.Fields.First(f => f.ReadText() == "dos"), false) && _formatEditor.Markdown.Contains("- uno\n  - dos"), Show(_formatEditor.Markdown));
        _formatEditor.Undo(); _formatEditor.Undo(); _formatEditor.Undo();
        await Task.Delay(200);
        Check("Deshacer pasos de estructura", _formatEditor.Markdown == sample, Show(_formatEditor.Markdown));

        var blank = _formatEditor.Fields.Last();
        blank.Box.Focus(FocusState.Programmatic);
        blank.Select(blank.Length);
        _formatEditor.Return(blank);
        var last = await FocusedFieldAsync();
        last.Box.Document.Selection.TypeText("## ");
        await Task.Delay(50);
        last = await FocusedFieldAsync();
        last.Box.Document.Selection.TypeText("Nuevo");
        await Task.Delay(50);
        Check("# tecleado en el editor hace un título", _formatEditor.Markdown.TrimEnd().EndsWith("## Nuevo"), Show(_formatEditor.Markdown));
        _formatEditor.Load(sample, null);
        await Task.Delay(200);

        var cell = _formatEditor.Fields.First(f => f.ReadText() == "1");
        cell.Box.Focus(FocusState.Programmatic);
        cell.Select(1);
        cell.Box.Document.Selection.TypeText("0");
        await Task.Delay(50);
        Check("Editar una celda", _formatEditor.Markdown.Contains("| 10 | 2 |"), Show(_formatEditor.Markdown));
        var code = _formatEditor.Fields.First(f => f.Multiline);
        code.Box.Focus(FocusState.Programmatic);
        code.Select(code.Length);
        code.Box.Document.Selection.TypeText("\rlet b = 2;");
        await Task.Delay(50);
        Check("Editar código agrega líneas", _formatEditor.Markdown.Contains("```js\nlet a = 1;\nlet b = 2;\n```"), Show(_formatEditor.Markdown));
        Check("Campos después del código siguen en su lugar", _formatEditor.Fields.First(f => f.ReadText() == "10") is { } after && _formatEditor.Paragraphs.Any(p => p.Kind == BlockKind.TableCell && p.Text == "10"));

        Check("Buscar en Formato", SearchDocument("nota").Count == 0 && SearchDocument("Cita").Count == 1);
        SetMode(EditorMode.Split);
        await Task.Delay(300);
        Check("Dividido muestra Fuente y Formato", SourceHost.Visibility == Visibility.Visible && FormatHost.Visibility == Visibility.Visible && _sourceEditor.Text == _formatEditor.Markdown);
        var splitField = _formatEditor.Fields[0];
        splitField.Box.Focus(FocusState.Programmatic);
        splitField.Select(splitField.Length);
        splitField.Box.Document.Selection.TypeText(" dividido");
        await Task.Delay(50);
        Check("Formato actualiza Fuente con la diferencia mínima", _sourceEditor.Text.StartsWith("# Título dividido\n") && !_sourceEditor.CanUndo);
        _sourceEditor.Box.Focus(FocusState.Programmatic);
        _sourceEditor.Apply(new TextEdit(0, 0, "Inicio\n\n", 8, 0));
        await Task.Delay(500);
        Check("Fuente actualiza Formato", _formatEditor.Markdown.StartsWith("Inicio\n\n# Título dividido") && _formatEditor.Fields[0].ReadText() == "Inicio", Show(_formatEditor.Markdown[..Math.Min(40, _formatEditor.Markdown.Length)]));
        SetMode(EditorMode.Format);

        // Columns fit their text instead of collapsing to the minimum width.
        const string shortcuts = "| Acción | Atajo |\n| --- | --- |\n| Buscar en el documento | Ctrl+F |\n| Guardar | Ctrl+S |\n";
        Navigate(new ReadingDocument(shortcuts, null, "Atajos"), MarkdownService.Parse(shortcuts));
        await Task.Delay(400);
        var wide = _formatEditor.Fields.FirstOrDefault(f => f.ReadText() == "Buscar en el documento");
        var shortcut = _formatEditor.Fields.FirstOrDefault(f => f.ReadText() == "Ctrl+F");
        if (wide is null || shortcut is null)
            throw new InvalidOperationException("Celdas: " + string.Join(" | ", _formatEditor.Fields.Select(f => $"'{f.ReadText()}' {f.IsRealized} {f.Box.ActualWidth}")));
        var wideCell = (Border)VisualTreeHelper.GetParent(wide.Box);
        var table = (Grid)VisualTreeHelper.GetParent(wideCell);
        double natural0 = table.ColumnDefinitions[0].MaxWidth, natural1 = table.ColumnDefinitions[1].MaxWidth;
        double room = ((FrameworkElement)VisualTreeHelper.GetParent(table)).ActualWidth;
        // With room, a column gets its natural width; without, the columns shrink together to fit.
        double expected0 = natural0 + natural1 <= room ? natural0 : room - Math.Max(72, Math.Floor(natural1 * room / (natural0 + natural1)));
        Check("La columna cabe su texto", natural0 >= FormatEditor.CellWidth("Buscar en el documento", wide.Box, wideCell.Padding) - .5
            && Math.Abs(table.ColumnDefinitions[0].ActualWidth - expected0) <= 2 && table.ActualWidth <= room + .5,
            $"natural {natural0}, esperado {expected0}, real {table.ColumnDefinitions[0].ActualWidth}, tabla {table.ActualWidth}, espacio {room}");
        Check("Una columna corta no se estira", natural1 < natural0 && table.ColumnDefinitions[1].ActualWidth <= natural1 + .5, $"{natural1} {table.ColumnDefinitions[1].ActualWidth}");
        await Task.Delay(500);
        double CellHeight(string text) => ((Border)VisualTreeHelper.GetParent(_formatEditor.Fields.First(f => f.ReadText() == text).Box)).ActualHeight;
        bool squeezed = table.ColumnDefinitions[0].ActualWidth < natural0 - 1;
        Check("Las filas de una línea miden lo mismo", Math.Abs(CellHeight("Guardar") - CellHeight("Acción")) <= 1
            && (squeezed ? CellHeight("Buscar en el documento") > CellHeight("Guardar") + 5 : Math.Abs(CellHeight("Buscar en el documento") - CellHeight("Guardar")) <= 1),
            string.Join(", ", _formatEditor.Fields.Select(f => VisualTreeHelper.GetParent(f.Box)).OfType<Border>().Select(b => b.ActualHeight)) + $" angosta {squeezed}");
        // Narrow enough to wrap the cells, then wide enough for one line each: rows must shrink back.
        _formatEditor.Width = 320;
        await Task.Delay(400);
        _formatEditor.Width = 1400;
        await Task.Delay(600);
        var cellHeights = _formatEditor.Fields.Select(f => VisualTreeHelper.GetParent(f.Box)).OfType<Border>().Select(b => b.ActualHeight).ToList();
        Check("Al ensanchar, las filas vuelven a una línea", cellHeights.Count == 6 && cellHeights.Max() - cellHeights.Min() <= 1, string.Join(", ", cellHeights));
        _formatEditor.Width = double.NaN;
        await Task.Delay(300);
        shortcut.Box.Focus(FocusState.Programmatic);
        shortcut.Select(shortcut.Length);
        shortcut.Box.Document.Selection.TypeText(" o Mayús+F3 desde cualquier lugar");
        await Task.Delay(400);
        shortcut = _formatEditor.Fields.First(f => f.ReadText().StartsWith("Ctrl+F"));
        table = (Grid)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(shortcut.Box));
        Check("La columna crece al escribir", table.ColumnDefinitions[1].MaxWidth > natural1 + 100, $"{natural1} → {table.ColumnDefinitions[1].MaxWidth}");
        if (_document is { IsDirty: true } edited) UpdateDocument(edited with { Source = edited.Saved });

        // Each paragraph reads in the direction of its first letter.
        Check("Dirección por la primera letra", TextDirection.FirstStrong("مرحبًا Amoxtli") == true && TextDirection.FirstStrong("Amoxtli مرحبًا") == false
            && TextDirection.FirstStrong("123 — ٤٥") is null && TextDirection.FirstStrong("**Ctrl+O** افتح") == false && TextDirection.FirstStrong("שלום") == true);
        Check("Las marcas de Markdown no deciden la dirección", TextDirection.IsRightToLeftLine("- [x] مهمة") && TextDirection.IsRightToLeftLine("> 1. مرحبًا")
            && TextDirection.IsRightToLeftLine("# مرحبًا") && !TextDirection.IsRightToLeftLine("- [x] task") && TextDirection.IsRightToLeftLine("| الإجراء | Ctrl |"));
        const string bidi = "# مرحبًا بك في Amoxtli\n\nEnglish text.\n\n```js\nlet a = 1;\n```\n\n- [x] مهمة\n\n| الإجراء | Ctrl |\n| --- | --- |\n| حفظ | Ctrl+S |\n";
        Navigate(new ReadingDocument(bidi, null, "Bidi"), MarkdownService.Parse(bidi));
        await Task.Delay(400);
        bool RightToLeftField(string text) => _formatEditor.Fields.First(f => f.ReadText() == text) is { RightToLeft: true } field
            && field.Box.Document.GetRange(0, 0).ParagraphFormat.RightToLeft == Microsoft.UI.Text.FormatEffect.On;
        Check("Formato: título árabe de derecha a izquierda", RightToLeftField("مرحبًا بك في Amoxtli"));
        Check("Formato: párrafo inglés de izquierda a derecha", !RightToLeftField("English text."));
        string Language(string text) => _formatEditor.Fields.First(f => f.ReadText() == text).Box.Document.GetRange(0, 1).CharacterFormat.LanguageTag;
        Check("Formato: el árabe lleva su idioma para ubicar espacios y puntos", Language("مرحبًا بك في Amoxtli").StartsWith("ar") && !Language("English text.").StartsWith("ar")
            && TextDirection.LanguageTag("שלום") == "he" && TextDirection.LanguageTag("- مرحبا") == "ar", Language("مرحبًا بك في Amoxtli") + " / " + Language("English text."));
        Check("Formato: el código queda de izquierda a derecha", !_formatEditor.Fields.First(f => f.Multiline).RightToLeft);
        Check("Formato: lista y tabla árabes de derecha a izquierda", RightToLeftField("مهمة") && RightToLeftField("Ctrl+S"));
        var english = _formatEditor.Fields.First(f => f.ReadText() == "English text.");
        english.Box.Focus(FocusState.Programmatic);
        english.Select(0);
        english.Box.Document.Selection.TypeText("نص ");
        await Task.Delay(100);
        english = _formatEditor.Fields.First(f => f.ReadText() == "نص English text.");
        Check("Formato: la dirección cambia al escribir", english.RightToLeft && english.Box.Document.GetRange(0, 0).ParagraphFormat.RightToLeft == Microsoft.UI.Text.FormatEffect.On);
        if (_document is { IsDirty: true } turned) UpdateDocument(turned with { Source = turned.Saved });
        Navigate(new ReadingDocument(bidi, null, "Bidi"), MarkdownService.Parse(bidi));
        SetMode(EditorMode.Source);
        await Task.Delay(400);
        bool LineRightToLeft(string line)
        {
            int at = _sourceEditor.Text.IndexOf(line);
            return at >= 0 && _sourceEditor.Box.Document.GetRange(at, at).ParagraphFormat.RightToLeft == Microsoft.UI.Text.FormatEffect.On;
        }
        Check("Fuente: cada línea toma su dirección", LineRightToLeft("# مرحبًا") && !LineRightToLeft("English text.") && !LineRightToLeft("let a = 1;")
            && LineRightToLeft("- [x] مهمة") && LineRightToLeft("| حفظ") && LineRightToLeft("| --- | --- |"));
        int arabicLine = _sourceEditor.Text.IndexOf("مرحبًا"), englishLine = _sourceEditor.Text.IndexOf("English");
        Check("Fuente: el árabe lleva su idioma", _sourceEditor.Box.Document.GetRange(arabicLine, arabicLine + 1).CharacterFormat.LanguageTag.StartsWith("ar")
            && !_sourceEditor.Box.Document.GetRange(englishLine, englishLine + 1).CharacterFormat.LanguageTag.StartsWith("ar"),
            _sourceEditor.Box.Document.GetRange(arabicLine, arabicLine + 1).CharacterFormat.LanguageTag);
        SetMode(EditorMode.Format);

        Navigate(new ReadingDocument("# Fin del Formato", null, "Fin"), MarkdownService.Parse("# Fin del Formato"));
    }
}
#endif
