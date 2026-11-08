using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;

namespace Amoxtli.Editing;

/// <summary>
/// Markdown to the flat paragraph model. Anything the model cannot hold is kept verbatim: an
/// unsupported block makes its whole top-level block raw, an unsupported inline becomes a raw run.
/// </summary>
public static class FormatParser
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseAutoLinks().UsePipeTables().UseGridTables().UseTaskLists()
        .UseEmphasisExtras().UseFootnotes().UseDefinitionLists()
        .UseAbbreviations().UseCustomContainers().UseYamlFrontMatter()
        .Build();

    private sealed class UnsupportedException : Exception;

    public static List<FormatParagraph> Parse(string source)
    {
        var document = Markdown.Parse(source, Pipeline);
        var parser = new Parser(source);
        var blocks = new List<(int Start, int End, List<FormatParagraph> Paragraphs)>();
        foreach (var block in document)
        {
            // Groups of footnotes or link definitions are synthetic: keep each definition where it was written.
            // Footnotes are also registered as link definitions; the footnote itself carries the text.
            if (block is FootnoteGroup or LinkReferenceDefinitionGroup)
            {
                foreach (var child in ((ContainerBlock)block).Where(child => child is not FootnoteLinkReferenceDefinition))
                    blocks.Add((child.Span.Start, End(child), [parser.Raw(child)]));
                continue;
            }
            var paragraphs = new List<FormatParagraph>();
            try { parser.Convert(block, [], paragraphs); }
            catch (UnsupportedException) { paragraphs = [parser.Raw(block)]; }
            blocks.Add((block.Span.Start, End(block), paragraphs));
        }
        var result = new List<FormatParagraph>();
        int covered = -1;
        foreach (var (start, end, paragraphs) in blocks.OrderBy(item => item.Start))
        {
            if (start <= covered) continue;
            covered = Math.Max(covered, end);
            result.AddRange(paragraphs.Where(p => p.Kind != BlockKind.Raw || p.Source.Length > 0));
        }
        return result;
    }

    /// <summary>The last source position of a block and everything inside it.</summary>
    internal static int End(Block block) => block is ContainerBlock container
        ? container.Descendants().Select(child => child.Span.End).Append(block.Span.End).Max()
        : block.Span.End;

    private sealed class Parser(string source)
    {
        private int _ids;

        public FormatParagraph Raw(Block block)
        {
            int start = Math.Clamp(block.Span.Start, 0, source.Length);
            start = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
            int end = Math.Clamp(End(block) + 1, start, source.Length);
            return new FormatParagraph { Kind = BlockKind.Raw, BlockId = ++_ids, Source = source[start..end].TrimEnd('\n', '\r'), SourceOffset = start };
        }

        public void Convert(Block block, IReadOnlyList<Container> containers, List<FormatParagraph> output)
        {
            int first = output.Count;
            Add(block, containers, output);
            // Nested blocks set their own offsets first; what is left starts where this block does.
            for (int i = first; i < output.Count; i++)
                if (output[i].SourceOffset < 0) output[i] = output[i] with { SourceOffset = Math.Max(0, block.Span.Start) };
        }

        private void Add(Block block, IReadOnlyList<Container> containers, List<FormatParagraph> output)
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    if (paragraph.Inline?.FirstChild is LinkInline { IsImage: true } image && image.NextSibling is null)
                    {
                        output.Add(new FormatParagraph { Kind = BlockKind.Image, BlockId = ++_ids, Containers = containers, Source = Slice(image) });
                        break;
                    }
                    output.Add(new FormatParagraph { Containers = containers, Runs = Runs(paragraph.Inline) });
                    break;
                case HeadingBlock heading:
                    output.Add(new FormatParagraph { Kind = BlockKind.Heading, Level = heading.Level, Containers = containers, Runs = Runs(heading.Inline).Select(r => r with { Text = r.Text.Replace('\n', ' ') }).ToList() });
                    break;
                case FencedCodeBlock fenced when fenced.GetType() == typeof(FencedCodeBlock):
                    string info = string.IsNullOrEmpty(fenced.Arguments) ? fenced.Info ?? "" : $"{fenced.Info} {fenced.Arguments}";
                    AddCode(fenced, info.Trim(), containers, output);
                    break;
                case CodeBlock code when code.GetType() == typeof(CodeBlock):
                    AddCode(code, "", containers, output);
                    break;
                case ThematicBreakBlock:
                    output.Add(new FormatParagraph { Kind = BlockKind.ThematicBreak, BlockId = ++_ids, Containers = containers });
                    break;
                case QuoteBlock quote:
                    Container[] inQuote = [.. containers, Container.Quote(++_ids)];
                    if (quote.Count == 0) output.Add(FormatParagraph.Plain("", inQuote));
                    foreach (var child in quote) Convert(child, inQuote, output);
                    break;
                case ListBlock list:
                    int listId = ++_ids;
                    int start = list.IsOrdered && int.TryParse(list.OrderedStart, out int number) ? number : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        bool? task = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList box } ? box.Checked : null;
                        Container[] inItem = [.. containers, Container.Item(listId, ++_ids, list.IsOrdered, start, task)];
                        if (item.Count == 0) output.Add(FormatParagraph.Plain("", inItem));
                        foreach (var child in item) Convert(child, inItem, output);
                    }
                    break;
                case MdTable table:
                    AddTable(table, containers, output);
                    break;
                default:
                    throw new UnsupportedException();
            }
        }

        private void AddCode(LeafBlock code, string info, IReadOnlyList<Container> containers, List<FormatParagraph> output)
        {
            int id = ++_ids;
            var lines = code.Lines;
            int count = lines.Count;
            if (count == 0) output.Add(new FormatParagraph { Kind = BlockKind.Code, BlockId = id, Info = info, Containers = containers });
            for (int i = 0; i < count; i++)
            {
                string text = lines.Lines[i].Slice.ToString();
                output.Add(new FormatParagraph { Kind = BlockKind.Code, BlockId = id, Info = info, Containers = containers, Runs = text.Length == 0 ? [] : [new InlineRun(text)] });
            }
        }

        private void AddTable(MdTable table, IReadOnlyList<Container> containers, List<FormatParagraph> output)
        {
            int id = ++_ids;
            var rows = table.OfType<TableRow>().ToList();
            // The header row decides the columns; a trailing pipe can add an empty column definition.
            int columns = Math.Max(1, rows[0].Count);
            var alignments = Enumerable.Range(0, columns).Select(i => i < table.ColumnDefinitions.Count ? table.ColumnDefinitions[i].Alignment switch
            {
                TableColumnAlign.Left => TableAlignment.Left,
                TableColumnAlign.Center => TableAlignment.Center,
                TableColumnAlign.Right => TableAlignment.Right,
                _ => TableAlignment.None
            } : TableAlignment.None).ToArray();
            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r].OfType<TableCell>().ToList();
                if (cells.Any(cell => cell.Count > 1 || (cell.Count == 1 && cell[0] is not ParagraphBlock) || cell.ColumnSpan > 1 || cell.RowSpan > 1))
                    throw new UnsupportedException();
                for (int c = 0; c < columns; c++)
                {
                    var runs = c < cells.Count && cells[c].Count == 1 ? Runs(((ParagraphBlock)cells[c][0]).Inline) : [];
                    output.Add(new FormatParagraph { Kind = BlockKind.TableCell, BlockId = id, Row = r, Column = c, Alignments = alignments, Containers = containers, Runs = runs });
                }
            }
        }

        private List<InlineRun> Runs(ContainerInline? inline)
        {
            var runs = new List<InlineRun>();
            if (inline is not null) Add(inline, InlineStyle.None, null, runs);
            // A task box is the item's state, not text; drop the space that followed it.
            if (inline?.FirstChild is TaskList && runs.Count > 0) runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
            return FormatRuns.Normalize(runs);
        }

        private void Add(ContainerInline container, InlineStyle style, string? url, List<InlineRun> runs)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        runs.Add(new InlineRun(literal.Content.ToString(), style, url));
                        break;
                    case EmphasisInline emphasis when emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount is 1 or 2:
                        Add(emphasis, style | (emphasis.DelimiterCount == 2 ? InlineStyle.Bold : InlineStyle.Italic), url, runs);
                        break;
                    case EmphasisInline emphasis when emphasis.DelimiterChar == '~' && emphasis.DelimiterCount == 2:
                        Add(emphasis, style | InlineStyle.Strikethrough, url, runs);
                        break;
                    case CodeInline code:
                        runs.Add(new InlineRun(code.Content, style | InlineStyle.Code, url));
                        break;
                    case LinkInline link when !link.IsImage && !link.IsAutoLink && link.Reference is null && !link.IsShortcut
                        && string.IsNullOrEmpty(link.Title) && url is null && Slice(link).EndsWith(')'):
                        Add(link, style | InlineStyle.Link, link.Url ?? "", runs);
                        break;
                    case LineBreakInline lineBreak:
                        runs.Add(new InlineRun(lineBreak.IsHard ? "\n" : " ", style, url));
                        break;
                    case HtmlInline html when html.Tag.Equals("<u>", StringComparison.OrdinalIgnoreCase):
                        style |= InlineStyle.Underline;
                        break;
                    case HtmlInline html when html.Tag.Equals("</u>", StringComparison.OrdinalIgnoreCase):
                        style &= ~InlineStyle.Underline;
                        break;
                    case TaskList:
                        break;
                    default:
                        string raw = Slice(inline);
                        if (raw.Contains('\n')) throw new UnsupportedException();
                        runs.Add(new InlineRun(raw, (style & ~InlineStyle.Link) | InlineStyle.Raw));
                        break;
                }
            }
        }

        private string Slice(Inline inline)
        {
            int start = Math.Clamp(inline.Span.Start, 0, source.Length), end = Math.Clamp(inline.Span.End + 1, start, source.Length);
            return source[start..end];
        }
    }
}
