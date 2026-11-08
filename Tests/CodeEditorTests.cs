#if DEBUG
using Amoxtli.Controls;
using Amoxtli.Editing;
using Amoxtli.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amoxtli;

// Code coloring, the language chip and the Language menu in both editors (port document, section 6).
public sealed partial class MainWindow
{
    private async Task RunCodeEditorTestsAsync(Action<string, bool, string?> check)
    {
        void Check(string name, bool condition, string? detail = null) => check(name, condition, detail);
        async Task<T> Until<T>(Func<T?> probe) where T : class
        {
            for (int i = 0; i < 60; i++) { if (probe() is { } value) return value; await Task.Delay(50); }
            return probe()!;
        }

        const string sample = "Texto\n\n```swift\nlet a = \"x\" // c\n/* uno\ndos */\n```\n\nfin let\n";
        Navigate(new ReadingDocument(sample, null, "Código"), MarkdownService.Parse(sample));
        SetMode(EditorMode.Source);
        await Task.Delay(300);
        var palette = EditorPalette.For(_sourceEditor.Box.ActualTheme);
        Windows.UI.Color SourceColor(string text, int nth = 0)
        {
            string current = _sourceEditor.Text;
            int at = -1;
            for (int i = 0; i <= nth; i++) at = current.IndexOf(text, at + 1, StringComparison.Ordinal);
            return _sourceEditor.Box.Document.GetRange(at, at + 1).CharacterFormat.ForegroundColor;
        }
        Check("Fuente colorea palabras clave", SourceColor("let") == palette.Token(TokenKind.Keyword));
        Check("Fuente colorea cadenas y comentarios", SourceColor("\"x\"") == palette.Token(TokenKind.String) && SourceColor("// c") == palette.Token(TokenKind.Comment));
        Check("Comentario de varias líneas", SourceColor("dos") == palette.Token(TokenKind.Comment));
        Check("Fuera del bloque no se colorea como código", SourceColor("let", 1) != palette.Token(TokenKind.Keyword));
        int fence = sample.IndexOf("swift");
        _sourceEditor.Apply(new TextEdit(fence, 5, "", fence, 0));
        Check("Quitar el lenguaje quita el color", SourceColor("let") != palette.Token(TokenKind.Keyword));
        _sourceEditor.Apply(new TextEdit(fence, 0, "js", fence + 2, 0));
        Check("Escribir el lenguaje en el fence colorea el bloque", SourceColor("let") == palette.Token(TokenKind.Keyword));
        _sourceEditor.Undo(); _sourceEditor.Undo();
        Check("Editar una línea recolorea su bloque", _sourceEditor.Text == sample && SourceColor("dos") == palette.Token(TokenKind.Comment));
        int end = sample.IndexOf("fin let");
        _sourceEditor.Apply(new TextEdit(end, 0, "```swift\n", end + 9, 0));
        Check("Abrir un bloque colorea lo que sigue", SourceColor("let", 1) == palette.Token(TokenKind.Keyword));
        _sourceEditor.Undo();
        Check("Cerrar el bloque quita el color de lo que sigue", SourceColor("let", 1) != palette.Token(TokenKind.Keyword));

        SetMode(EditorMode.Format);
        var code = await Until(() => _formatEditor.Fields.FirstOrDefault(f => f.Multiline && f.IsRealized));
        await Task.Delay(100);
        Windows.UI.Color FormatColor(string text) { int at = code.ReadText().IndexOf(text, StringComparison.Ordinal); return code.Box.Document.GetRange(at, at + 1).CharacterFormat.ForegroundColor; }
        Check("Formato colorea el código", FormatColor("let") == palette.Token(TokenKind.Keyword) && FormatColor("dos") == palette.Token(TokenKind.Comment));
        code.Box.Focus(FocusState.Programmatic);
        code.Select(code.Length);
        code.Box.Document.Selection.TypeText("\rvar b = 2");
        await Task.Delay(50);
        Check("Formato recolorea al editar", FormatColor("var") == palette.Token(TokenKind.Keyword) && FormatColor("2") == palette.Token(TokenKind.Number));

        IEnumerable<FrameworkElement> Tree(DependencyObject root)
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement element) yield return element;
                foreach (var nested in Tree(child)) yield return nested;
            }
        }
        string ChipText(Button chip) => ((chip.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault())?.Text ?? "";
        var chips = Tree(_formatEditor).OfType<FormatEditor.LanguageButton>().ToList();
        Check("Chip con el nombre del lenguaje", chips.Count == 1 && ChipText(chips[0]) == "Swift");
        Check("El texto del código no es el chip", !code.ReadText().Contains("Swift"));
        int paragraph = _formatEditor.Paragraphs.ToList().FindIndex(p => p.Kind == BlockKind.Code);
        var items = _formatEditor.LanguageItems(paragraph);
        var names = items.OfType<RadioMenuFlyoutItem>().Select(i => i.Text).ToList();
        Check("Menú de lenguaje en orden", names.SequenceEqual([Localization.Get("NoLanguage"), "CSS", "HTML", "JavaScript", "JSON", "Python", "Shell", "SQL", "Swift", "TypeScript"])
            && items[1] is MenuFlyoutSeparator, string.Join(",", names));
        Check("Palomita en el lenguaje actual", items.OfType<RadioMenuFlyoutItem>().Single(i => i.IsChecked).Text == "Swift");
        Check("Menú contextual ofrece Lenguaje en código", _formatEditor.ActiveCodeParagraph is not null);

        _formatEditor.SetLanguage(paragraph, CodeLanguage.Json);
        Check("Elegir escribe el fence", _formatEditor.Markdown.Contains("```json\nlet a"), _formatEditor.Markdown);
        _formatEditor.Undo();
        Check("Elegir es un paso de deshacer", _formatEditor.Markdown.Contains("```swift\nlet a"));
        _formatEditor.SetLanguage(paragraph, CodeLanguage.None);
        Check("Ninguno deja el fence sin lenguaje", _formatEditor.Markdown.Contains("```\nlet a"));
        Check("Los demás bloques no cambian", _formatEditor.Markdown.StartsWith("Texto\n\n") && _formatEditor.Markdown.TrimEnd().EndsWith("fin let"));
        var none = await Until(() => Tree(_formatEditor).OfType<FormatEditor.LanguageButton>().FirstOrDefault());
        Check("Sin lenguaje: Elegir lenguaje", ChipText(none) == Localization.Get("ChooseLanguage"));

        const string unknown = "```rust\nfn main() {}\n```\n\ntexto\n";
        Navigate(new ReadingDocument(unknown, null, "Desconocido"), MarkdownService.Parse(unknown));
        var rust = await Until(() => Tree(_formatEditor).OfType<FormatEditor.LanguageButton>().FirstOrDefault());
        Check("Lenguaje desconocido se muestra tal cual", ChipText(rust) == "rust");
        var rustItems = _formatEditor.LanguageItems(0);
        Check("Lenguaje desconocido arriba, marcado y deshabilitado", rustItems[0] is ToggleMenuFlyoutItem { Text: "rust", IsChecked: true, IsEnabled: false } && rustItems[1] is MenuFlyoutSeparator
            && !rustItems.OfType<RadioMenuFlyoutItem>().Any(i => i.IsChecked));
        _formatEditor.Fields.Last().Box.Focus(FocusState.Programmatic);
        await Task.Delay(100);
        _formatEditor.OpenLanguageMenu(rust);
        await Task.Delay(300);
        Check("El chip encuentra su bloque al inicio del documento", _formatEditor.ActiveField?.Multiline == true);
        Check("Un bloque al inicio muestra su chip", Tree(_formatEditor).OfType<FormatEditor.LanguageButton>().First().ActualHeight > 0);
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)) popup.IsOpen = false;
        Navigate(new ReadingDocument("# Fin del código", null, "Fin"), MarkdownService.Parse("# Fin del código"));
    }
}
#endif
