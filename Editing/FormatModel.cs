using System.Text;

namespace Amoxtli.Editing;

public enum BlockKind { Paragraph, Heading, Code, ThematicBreak, Image, TableCell, Raw }

public enum TableAlignment { None, Left, Center, Right }

[Flags]
public enum InlineStyle
{
    None = 0, Bold = 1, Italic = 2, Underline = 4, Strikethrough = 8, Code = 16, Link = 32,
    /// <summary>Markdown the editor does not model (footnote references, inline HTML, ==marks==): kept verbatim.</summary>
    Raw = 64
}

/// <summary>A quote, or a list item. Paragraphs with the same item id belong to one item.</summary>
public sealed record Container(bool IsQuote, int Id, int ListId = 0, bool Ordered = false, int Start = 1, bool? Task = null)
{
    public static Container Quote(int id) => new(true, id);
    public static Container Item(int listId, int itemId, bool ordered, int start = 1, bool? task = null) => new(false, itemId, listId, ordered, start, task);
}

/// <summary>Styled text. A forced line break is "\n" inside <see cref="Text"/>. Raw runs hold source text.</summary>
public sealed record InlineRun(string Text, InlineStyle Style = InlineStyle.None, string? Url = null);

/// <summary>
/// One paragraph of the flat document. It knows what it is (<see cref="Kind"/>) and what it is
/// inside (<see cref="Containers"/>, outermost first). Code blocks are one paragraph per line
/// sharing <see cref="BlockId"/>; table cells share it per table.
/// </summary>
public sealed record FormatParagraph
{
    public BlockKind Kind { get; init; }
    public int Level { get; init; }
    public int BlockId { get; init; }
    /// <summary>The fence info string for code, such as "py title=\"a\"".</summary>
    public string Info { get; init; } = "";
    public int Row { get; init; }
    public int Column { get; init; }
    public IReadOnlyList<TableAlignment> Alignments { get; init; } = [];
    public IReadOnlyList<Container> Containers { get; init; } = [];
    public IReadOnlyList<InlineRun> Runs { get; init; } = [];
    /// <summary>Verbatim Markdown for raw blocks and images.</summary>
    public string Source { get; init; } = "";
    /// <summary>Where the paragraph's block starts in the Markdown it was read from, or -1. Pairs the two editors when scrolling.</summary>
    public int SourceOffset { get; init; } = -1;
    /// <summary>A leading "#" that is text, not a heading mark: typing a space after it changes nothing.</summary>
    public bool LiteralHash { get; init; }

    public string Text => string.Concat(Runs.Select(run => run.Text));
    public bool IsEditable => Kind is not (BlockKind.ThematicBreak or BlockKind.Image or BlockKind.Raw);

    public static FormatParagraph Plain(string text, IReadOnlyList<Container>? containers = null) =>
        new() { Runs = text.Length == 0 ? [] : [new InlineRun(text)], Containers = containers ?? [] };
}

public static class FormatRuns
{
    /// <summary>Joins neighbors with the same style and drops empty runs.</summary>
    public static List<InlineRun> Normalize(IEnumerable<InlineRun> runs)
    {
        var result = new List<InlineRun>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (result.Count > 0 && result[^1].Style == run.Style && result[^1].Url == run.Url && run.Style != InlineStyle.Raw)
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            else result.Add(run);
        }
        return result;
    }

    /// <summary>
    /// A stable description for comparing documents: container ids become their order of appearance,
    /// so parse(serialize(x)) can be compared with parse(x).
    /// </summary>
    public static string Describe(IReadOnlyList<FormatParagraph> paragraphs)
    {
        var ids = new Dictionary<(string, int), int>();
        int Id(string kind, int id) => ids.TryGetValue((kind, id), out int value) ? value : ids[(kind, id)] = ids.Count(pair => pair.Key.Item1 == kind);
        var text = new StringBuilder();
        foreach (var p in paragraphs)
        {
            text.Append(p.Kind);
            if (p.Kind == BlockKind.Heading) text.Append(p.Level);
            if (p.Kind == BlockKind.Code) text.Append($"#{Id("code", p.BlockId)}[{p.Info}]");
            if (p.Kind == BlockKind.TableCell) text.Append($"#{Id("table", p.BlockId)}({p.Row},{p.Column};{string.Join("", p.Alignments.Select(a => a.ToString()[0]))})");
            foreach (var c in p.Containers)
                text.Append(c.IsQuote ? $" >q{Id("quote", c.Id)}" : $" >l{Id("list", c.ListId)}i{Id("item", c.Id)}{(c.Ordered ? $"o{c.Start}" : "u")}{(c.Task is { } t ? (t ? "[x]" : "[ ]") : "")}");
            text.Append(" |");
            if (p.Kind is BlockKind.Raw or BlockKind.Image) text.Append(p.Source.Replace("\n", "⏎"));
            foreach (var run in Normalize(p.Runs)) text.Append($"{{{run.Style}{(run.Url is null ? "" : "@" + run.Url)}:{run.Text.Replace("\n", "⏎")}}}");
            text.Append('\n');
        }
        return text.ToString();
    }
}
