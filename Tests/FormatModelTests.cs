#if DEBUG
using Amoxtli.Editing;

namespace Amoxtli;

// Format editor model: parsing into flat paragraphs and canonical serialization (port document, section 4.2).
public sealed partial class MainWindow
{
    private static void RunFormatModelTests(Action<string, bool, string?> check)
    {
        void Check(string name, bool condition, string? detail = null) => check(name, condition, detail);
        string Show(string text) => text.Replace("\n", "⏎");

        void RoundTrip(string name, string markdown)
        {
            var parsed = FormatParser.Parse(markdown);
            string serialized = FormatSerializer.Serialize(parsed);
            var reparsed = FormatParser.Parse(serialized);
            string before = FormatRuns.Describe(parsed), after = FormatRuns.Describe(reparsed);
            Check($"Ida y vuelta: {name}", before == after, $"Markdown: {Show(serialized)} · Antes: {Show(before)} · Después: {Show(after)}");
            string again = FormatSerializer.Serialize(reparsed);
            Check($"Salida canónica estable: {name}", again == serialized, $"{Show(serialized)} · {Show(again)}");
        }

        RoundTrip("estilos", "Texto **negrita**, *cursiva*, <u>subrayado</u>, ~~tachado~~, `código`, ***ambas*** y [enlace](https://a.com) con **negrita [dentro](u)**.");
        RoundTrip("estilos pegados", "*a* **b** _c_ **d *e* f** `x`**y**");
        RoundTrip("listas anidadas", "- uno\n- dos\n  - anidado\n    1. num\n    2. dos\n- tres\n\n1. a\n2. b\n\n- [ ] tarea\n- [x] hecha");
        RoundTrip("lista que empieza en 5", "Texto\n\n5. cinco\n6. seis");
        RoundTrip("listas seguidas", "- a\n- b\n\n* c\n* d\n\n1. x\n\n1) y");
        RoundTrip("ítem con varios párrafos", "- primero\n\n  segundo del mismo ítem\n- otro");
        RoundTrip("citas", "> cita\n>\n> - lista en cita\n>   > anidada\n\n> otra cita");
        RoundTrip("código", "```swift\nlet a = 1\n\n  sangría\n```\n\n    indentado\n\n````md\n```\nadentro\n```\n````");
        RoundTrip("código en lista", "- ítem\n\n  ```py\n  print(1)\n  ```");
        RoundTrip("tablas", "| a | b |\n| :--- | ---: |\n| 1 | **2** |\n| x \\| y | |");
        RoundTrip("enlaces", "[a](https://x.com/a_b) [b](<https://x.com/con espacio>) [c](./doc.md#sec) [**d**](u)");
        RoundTrip("saltos forzados", "uno\\\ndos  \ntres\n\n**negrita\\\npartida**");
        RoundTrip("texto que parece sintaxis", "\\# no título\n\n1\\. no lista\n\n\\- no viñeta\n\n\\> no cita\n\nun \\*asterisco\\* y snake_case y \\_borde\\_ y \\`tilde\\` y \\[no enlace\\](x) y &amp; y 3 < 4 > 2 y a \\<b\\> c y \\~\\~no\\~\\~ y \\=\\=no\\=\\= y \\\\ barra");
        RoundTrip("títulos", "# T1\n\n## T2 ##\n\nSetext\n======\n\n### Termina con \\#\n\n#### `código` y **negrita**");
        RoundTrip("bloques crudos", "---\ntitle: x\n---\n\n# H\n\nTexto[^1] con ==marca== y H~2~O.\n\n<div>html</div>\n\n[ref]: https://r.com\n\nUn [enlace][ref] y <https://auto.com>.\n\n[^1]: Nota.");
        RoundTrip("imagen", "![alt](img.png)\n\nTexto con ![icono](i.png) en línea.");
        RoundTrip("separador", "a\n\n---\n\nb\n\n***");
        RoundTrip("vacío", "");
        RoundTrip("guía de bienvenida", Services.DocumentFile.NormalizeLineEndings(Services.Localization.WelcomeMarkdown()));
        RoundTrip("Welcome.md", Services.DocumentFile.NormalizeLineEndings(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Welcome.md"))));

        var styled = FormatParser.Parse("**a** y *b*");
        Check("Modelo: estilos en runs", styled is [{ Kind: BlockKind.Paragraph, Runs: [{ Text: "a", Style: InlineStyle.Bold }, { Text: " y " }, { Text: "b", Style: InlineStyle.Italic }] }],
            FormatRuns.Describe(styled));
        var list = FormatParser.Parse("- a\n  - b\n- c");
        Check("Modelo: listas como contenedores", list.Count == 3 && list[1].Containers.Count == 2 && list[0].Containers[0] == list[1].Containers[0]
            && list[2].Containers[0].ListId == list[0].Containers[0].ListId && list[2].Containers[0].Id != list[0].Containers[0].Id);
        var code = FormatParser.Parse("```js\na\nb\n```");
        Check("Modelo: código una línea por párrafo", code.Count == 2 && code.All(p => p is { Kind: BlockKind.Code, Info: "js" }) && code[0].BlockId == code[1].BlockId);
        var emptyRows = FormatParser.Parse("| a | b |\n| --- | --- |\n|  |  |\n| 1 | 2 |\n|  |  |");
        Check("Tablas conservan filas vacías", emptyRows.Where(p => p.Kind == BlockKind.TableCell).Select(p => p.Row).Distinct().Count() == 4, FormatRuns.Describe(emptyRows));
        Check("Escapes mínimos", FormatSerializer.Serialize(FormatParser.Parse("snake_case y C:\\Users y 2+2=4")) == "snake_case y C:\\Users y 2+2=4\n",
            FormatSerializer.Serialize(FormatParser.Parse("snake_case y C:\\Users y 2+2=4")));
        Check("Viñetas canónicas", FormatSerializer.Serialize(FormatParser.Parse("* a\n+ b")) is var bullets && bullets.StartsWith("- a"), bullets);
        Check("Espacios fuera de los marcadores", FormatSerializer.Serialize([new FormatParagraph { Runs = [new("a"), new(" b ", InlineStyle.Bold), new("c")] }]) == "a **b** c\n");
        Check("Cursiva junto a negrita sigue siendo legible", FormatParser.Parse(FormatSerializer.Serialize([new FormatParagraph { Runs = [new("a", InlineStyle.Italic), new("b", InlineStyle.Bold)] }])) is [{ Runs: [{ Style: InlineStyle.Italic }, { Style: InlineStyle.Bold }] }]);
    }
}
#endif
