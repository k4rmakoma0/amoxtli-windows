#if DEBUG
using Amoxtli.Controls;
using Amoxtli.Editing;
using Amoxtli.Services;
using Microsoft.UI.Xaml;
using System.Text;

namespace Amoxtli;

// Split view: text kept in step and scroll paired block by block (port document, section 7).
public sealed partial class MainWindow
{
    private async Task RunSplitTestsAsync(Action<string, bool, string?> check)
    {
        void Check(string name, bool condition, string? detail = null) => check(name, condition, detail);
        static bool Near(double a, double b, double tolerance = 1) => Math.Abs(a - b) <= tolerance;

        // Scroll map
        var map = ScrollMap.Create([(100, 300), (200, 350)], 1000, 2000);
        Check("Mapa: interpola entre el inicio y el primer bloque", Near(map.Forward(50), 150));
        Check("Mapa: interpola entre bloques", Near(map.Forward(150), 325));
        Check("Mapa: interpola hasta el final", Near(map.Forward(600), 350 + 400.0 / 800 * 1650));
        Check("Mapa: los bloques coinciden exactamente", Near(map.Forward(200), 350) && Near(map.Backward(350), 200));
        Check("Mapa: ida y vuelta", Near(map.Backward(map.Forward(437)), 437));
        Check("Mapa: los extremos coinciden", Near(map.Forward(0), 0) && Near(map.Forward(1000), 2000) && Near(map.Forward(5000), 2000) && Near(map.Backward(-10), 0));
        var unreachable = ScrollMap.Create([(100, 300), (950, 1990)], 900, 2000);
        Check("Mapa: un bloque que ya no llega arriba se omite y manda el final", unreachable.Points.Count == 3 && unreachable.Points[^1] == (900, 2000));
        var backwards = ScrollMap.Create([(100, 300), (90, 400), (150, 250)], 1000, 1000);
        Check("Mapa: solo puntos crecientes en ambos lados", backwards.Points.Count == 3 && backwards.Points[1] == (100, 300));
        var mismatch = ScrollMap.Create([(100, 300)], 1000, 2000, sameBlocks: false);
        Check("Mapa: conteos distintos solo alinean los extremos", mismatch.Points.Count == 2 && Near(mismatch.Forward(500), 1000));
        Check("Mapa: vistas sin scroll", Near(ScrollMap.Create([(10, 10)], 0, 0).Forward(30), 0));

        // Where blocks start in the Markdown
        const string sample = "# A\n\npara\n\n- x\n- y\n\n```\nc\n```\n";
        var parsed = FormatParser.Parse(sample);
        string Line(string text, int offset) { int start = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1; int end = text.IndexOf('\n', offset); return text[start..(end < 0 ? text.Length : end)]; }
        // A code block starts at its fence.
        bool Points(string text, int offset, FormatParagraph p) => p.Kind == BlockKind.Code ? Line(text, offset).StartsWith("```") : Line(text, offset).Contains(p.Text);
        Check("Offsets del parser: cada párrafo apunta a su línea", parsed.All(p => Points(sample, p.SourceOffset, p)),
            string.Join(", ", parsed.Select(p => $"{p.Text}@{p.SourceOffset}")));
        var edited = parsed.Select(p => p.Kind == BlockKind.Paragraph && p.Text == "para" ? p with { Runs = [new InlineRun("párrafo más largo")] } : p).ToList();
        var offsets = new int[edited.Count];
        string serialized = FormatSerializer.Serialize(edited, offsets);
        Check("Offsets del serializador: cada párrafo apunta a su línea", edited.Select((p, i) => Points(serialized, offsets[i], p)).All(found => found),
            string.Join(", ", offsets));

        // Minimal difference keeps the selection where it was
        SetMode(EditorMode.Source);
        _sourceEditor.Load("abc def");
        _sourceEditor.Select(5, 0);
        _sourceEditor.Replace("abc XX def");
        Check("Diferencia mínima ajusta el cursor después del cambio", _sourceEditor.Selection == (8, 0) && _sourceEditor.Text == "abc XX def");
        _sourceEditor.Select(1, 0);
        _sourceEditor.Replace("abc XX def!");
        Check("Diferencia mínima no mueve el cursor antes del cambio", _sourceEditor.Selection == (1, 0));

        // Scroll in a long document
        var text = new StringBuilder("# Inicio\n\n");
        for (int i = 1; i <= 80; i++)
        {
            text.Append($"## Sección {i}\n\nTexto de la sección {i} con algo de contenido para leer.\n\n");
            if (i % 10 == 0) text.Append("| A | B |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n\n```\nlínea\nlínea\nlínea\n```\n\n");
        }
        string document = text.ToString();
        Navigate(new ReadingDocument(document, null, "Dividido"), MarkdownService.Parse(document));
        SetMode(EditorMode.Split);
        await Task.Delay(1500);
        var sourceScroll = _sourceEditor.Scroll;
        Check("Dividido sincroniza el scroll", _splitScroll.Enabled && sourceScroll is not null && _sourceEditor.Text == _formatEditor.Markdown);
        if (sourceScroll is null) return;
        int section40 = document.IndexOf("## Sección 40\n");
        _sourceEditor.CalibrateUnits();
        double before = _sourceEditor.TopOf(section40);
        // Line positions must be in the scroll's units: the text ends near the end of the content, the middle near its middle.
        double ratio = before / _sourceEditor.TopOf(document.Length);
        Check("Posiciones de Fuente en las unidades del scroll", ratio is > 0.35 and < 0.65 && Math.Abs(_sourceEditor.TopOf(document.Length) / sourceScroll.ExtentHeight - 1) < 0.08,
            $"{before} / {_sourceEditor.TopOf(document.Length)} · alto {sourceScroll.ExtentHeight}");
        sourceScroll.ChangeView(null, 500, null, true);
        await Task.Delay(200);
        Check("La posición de una línea no depende del scroll", Near(_sourceEditor.TopOf(section40), before, 2), $"{before} · {_sourceEditor.TopOf(section40)}");

        var tops = _formatEditor.BlockTops().ToList();
        var target = tops.First(block => block.Offset == section40);
        _splitScroll.Invalidate();
        _formatEditor.Scroll.ChangeView(null, target.Top, null, true);
        await Task.Delay(200);
        _splitScroll.Follow(SplitScroll.Side.Format);
        await Task.Delay(200);
        Check("Formato manda: Fuente muestra la misma sección arriba", Near(sourceScroll.VerticalOffset, _sourceEditor.TopOf(section40), 4),
            $"Fuente {sourceScroll.VerticalOffset} · línea {_sourceEditor.TopOf(section40)}");

        int section70 = document.IndexOf("## Sección 70\n");
        sourceScroll.ChangeView(null, _sourceEditor.TopOf(section70), null, true);
        await Task.Delay(200);
        _splitScroll.Follow(SplitScroll.Side.Source);
        await Task.Delay(200);
        var block70 = _formatEditor.BlockTops().First(block => block.Offset == section70);
        Check("Fuente manda: Formato muestra la misma sección arriba", Near(_formatEditor.Scroll.VerticalOffset, block70.Top, 4),
            $"Formato {_formatEditor.Scroll.VerticalOffset} · bloque {block70.Top}");
        var pairs = _formatEditor.BlockTops().Select(block => (Source: _sourceEditor.TopOf(block.Offset), Format: block.Top, block.Offset)).ToList();
        var skipped = pairs.Where((pair, i) => i > 0 && (pair.Source <= pairs[i - 1].Source || pair.Format <= pairs[i - 1].Format)).Take(6);
        Check("Los bloques se emparejan uno a uno", _splitScroll.Map.Points.Count > 100,
            $"{_splitScroll.Map.Points.Count} puntos de {pairs.Count}; fin {sourceScroll.ScrollableHeight}/{_formatEditor.Scroll.ScrollableHeight}; " + string.Join(" | ", skipped.Select(p => $"{p.Offset}:{p.Source:0}/{p.Format:0}")));

        // Catching up when an editor gains the focus
        UpdateDocument(_document! with { Source = "# Cambiado afuera\n" });
        _formatEditor.Fields[0].Box.Focus(FocusState.Programmatic);
        await Task.Delay(300);
        Check("Formato se pone al día al recibir el foco", _formatEditor.Markdown == "# Cambiado afuera\n");
        UpdateDocument(_document! with { Source = "# Otra vez\n" });
        _sourceEditor.Box.Focus(FocusState.Programmatic);
        await Task.Delay(100);
        Check("Fuente se pone al día al recibir el foco", _sourceEditor.Text == "# Otra vez\n");

        SetMode(EditorMode.Format);
        Check("Fuera de Dividido no se sincroniza", !_splitScroll.Enabled);
        Navigate(new ReadingDocument("# Fin de Dividido", null, "Fin"), MarkdownService.Parse("# Fin de Dividido"));
    }
}
#endif
