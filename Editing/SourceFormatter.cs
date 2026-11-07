using System.Text;
using System.Text.RegularExpressions;

namespace Amoxtli.Editing;

public enum MarkdownTool { Bold, Italic, Underline, Strikethrough, Code, Quote, BulletList, NumberedList, CodeBlock, Link }

/// <summary>What applies at the caret or across the whole selection. Heading level 0 is body text.</summary>
public sealed record FormatState(int HeadingLevel, IReadOnlySet<MarkdownTool> Active)
{
    public static readonly FormatState None = new(0, new HashSet<MarkdownTool>());
    public bool Has(MarkdownTool tool) => Active.Contains(tool);
}

/// <summary>
/// Formatting commands on Markdown source. Each returns one <see cref="TextEdit"/> (one undo step),
/// or null when there is nothing to change. Every tool is a toggle.
/// </summary>
public static partial class SourceFormatter
{
    private readonly record struct Replacement(int Start, int Length, string Text);
    private readonly record struct Line(int Start, int End);

    public static TextEdit? Toggle(string text, int start, int length, MarkdownTool tool, SourceHighlighter? highlighter = null) => tool switch
    {
        MarkdownTool.Quote => ToggleQuote(text, start, length),
        MarkdownTool.BulletList => ToggleList(text, start, length, false),
        MarkdownTool.NumberedList => ToggleList(text, start, length, true),
        MarkdownTool.CodeBlock => ToggleCodeBlock(text, start, length, highlighter),
        MarkdownTool.Link => FindLink(text, start, length) is { } link ? RemoveLink(text, start, length, link) : null,
        _ => ToggleInline(text, start, length, tool)
    };

    public static FormatState GetState(string text, int start, int length, SourceHighlighter? highlighter = null)
    {
        var active = new HashSet<MarkdownTool>();
        var lines = SelectedLines(text, start, length);
        var parsed = lines.Select(line => (Line: line, Parsed: MarkdownLine.Parse(text.AsSpan(line.Start, line.End - line.Start)))).ToList();
        var filled = parsed.Where(item => lines.Count == 1 || !IsBlank(text, item.Line)).ToList();
        if (filled.Count == 0) filled = parsed;
        int heading = filled[0].Parsed.HeadingLevel;
        if (filled.Any(item => item.Parsed.HeadingLevel != heading)) heading = -1;
        if (filled.All(item => item.Parsed.QuoteDepth > 0)) active.Add(MarkdownTool.Quote);
        if (filled.All(item => item.Parsed.IsListItem && !item.Parsed.Ordered)) active.Add(MarkdownTool.BulletList);
        if (filled.All(item => item.Parsed.IsListItem && item.Parsed.Ordered)) active.Add(MarkdownTool.NumberedList);
        highlighter ??= Highlight(text);
        if (highlighter.IsCode(highlighter.LineAt(start))) active.Add(MarkdownTool.CodeBlock);
        foreach (var tool in new[] { MarkdownTool.Bold, MarkdownTool.Italic, MarkdownTool.Underline, MarkdownTool.Strikethrough, MarkdownTool.Code, MarkdownTool.Link })
        {
            bool all = true, any = false;
            foreach (var segment in Segments(text, start, length, lines))
            {
                any = true;
                if (Covering(text, segment.Line, segment.From, segment.To, KindOf(tool), length == 0) is null) { all = false; break; }
            }
            if (any && all) active.Add(tool);
        }
        return new FormatState(heading, active);
    }

    /// <summary>Text (level 0) or heading 1–6. Choosing the current style changes nothing.</summary>
    public static TextEdit? SetHeading(string text, int start, int length, int level)
    {
        var replacements = new List<Replacement>();
        var lines = Targets(text, start, length);
        var parsed = lines.Select(line => MarkdownLine.Parse(text.AsSpan(line.Start, line.End - line.Start))).ToList();
        if (parsed.All(p => p.HeadingLevel == level)) return null;
        string marks = level > 0 ? new string('#', level) + " " : "";
        for (int i = 0; i < lines.Count; i++)
        {
            var p = parsed[i];
            if (p.HeadingLevel == level) continue;
            if (p.HeadingLevel > 0) replacements.Add(new(lines[i].Start + p.HeadingStart, p.ContentStart - p.HeadingStart, marks));
            else replacements.Add(new(lines[i].Start + p.TaskEnd, 0, marks));
        }
        return Compose(text, replacements, start, length);
    }

    public static TextEdit InsertLink(string text, int start, int length, string label, string address)
    {
        string link = $"[{(string.IsNullOrWhiteSpace(label) ? address : label.Trim())}]({address})";
        return new TextEdit(start, length, link, start + link.Length, 0);
    }

    /// <summary>A link that covers the caret or selection, as absolute offsets.</summary>
    public static InlineSpan? FindLink(string text, int start, int length)
    {
        var lines = SelectedLines(text, start, length);
        if (lines.Count != 1) return null;
        var segment = Segments(text, start, length, lines).FirstOrDefault();
        return segment.Line == default ? null : Covering(text, segment.Line, segment.From, segment.To, InlineKind.Link, length == 0);
    }

    /// <summary>
    /// Empty: not inserted. A scheme, or a leading #, / or .: unchanged. An @ without /: mailto.
    /// A dot: https. Spaces are encoded so the Markdown link stays intact.
    /// </summary>
    public static string? NormalizeAddress(string address)
    {
        string value = address.Trim();
        if (value.Length == 0) return null;
        value = value.Replace(" ", "%20");
        if (SchemePattern().IsMatch(value) || value[0] is '#' or '/' or '.') return value;
        if (value.Contains('@') && !value.Contains('/')) return "mailto:" + value;
        if (value.Contains('.')) return "https://" + value;
        return value;
    }

    public const int MaxTableColumns = 20, MaxTableRows = 100;

    /// <summary>A table whose first row is the header. The caret lands in the first header cell.</summary>
    public static TextEdit InsertTable(string text, int start, int length, int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, MaxTableColumns);
        rows = Math.Clamp(rows, 1, MaxTableRows);
        var table = new StringBuilder();
        string row = "|" + string.Concat(Enumerable.Repeat("   |", columns));
        table.Append(row).Append('\n').Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns)));
        for (int i = 1; i < rows; i++) table.Append('\n').Append(row);
        return InsertBlock(text, start, length, table.ToString(), 2);
    }

    public static TextEdit InsertThematicBreak(string text, int start, int length) => InsertBlock(text, start, length, "---", 3);

    /// <summary>
    /// Return in a list item or quote continues it, as if typed. An empty item or quote line ends it.
    /// Null means a plain line break.
    /// </summary>
    public static TextEdit? ContinueOnReturn(string text, int start, int length, SourceHighlighter? highlighter = null)
    {
        var (lineStart, lineEnd) = LineBounds(text, start);
        if (start + length > lineEnd) return null;
        highlighter ??= Highlight(text);
        if (highlighter.IsCode(highlighter.LineAt(start))) return null;
        var line = text.AsSpan(lineStart, lineEnd - lineStart);
        var p = MarkdownLine.Parse(line);
        if (p.HeadingLevel > 0 || p.ThematicBreak || (!p.IsListItem && p.QuoteDepth == 0)) return null;
        int prefixEnd = p.IsListItem ? p.TaskEnd : p.QuoteEnd;
        int caret = start - lineStart;
        if (caret < prefixEnd) return null;
        if (line[prefixEnd..].IsWhiteSpace())
        {
            int remove = p.IsListItem ? p.QuoteEnd : line[..p.QuoteEnd].LastIndexOf('>');
            return new TextEdit(lineStart + remove, lineEnd - lineStart - remove, "", lineStart + remove, 0);
        }
        var prefix = new StringBuilder().Append(line[..p.QuoteEnd]);
        if (p.IsListItem)
        {
            prefix.Append(line[p.QuoteEnd..p.MarkerStart]);
            prefix.Append(p.Ordered ? $"{p.Number + 1}{p.Delimiter}" : p.Delimiter.ToString());
            prefix.Append(line[(p.MarkerStart + p.MarkerLength)..p.ItemStart]);
            if (p.IsTask) prefix.Append("[ ] ");
        }
        string inserted = "\n" + prefix;
        return new TextEdit(start, length, inserted, start + inserted.Length, 0);
    }

    private static TextEdit? ToggleInline(string text, int start, int length, MarkdownTool tool)
    {
        var (open, close) = tool switch
        {
            MarkdownTool.Bold => ("**", "**"),
            MarkdownTool.Italic => ("*", "*"),
            MarkdownTool.Underline => ("<u>", "</u>"),
            MarkdownTool.Strikethrough => ("~~", "~~"),
            _ => ("`", "`")
        };
        var kind = KindOf(tool);
        var lines = SelectedLines(text, start, length);
        var covered = new List<InlineSpan>();
        var bare = new List<(int From, int To)>();
        foreach (var segment in Segments(text, start, length, lines))
        {
            if (Covering(text, segment.Line, segment.From, segment.To, kind, length == 0) is { } span) covered.Add(span);
            else
            {
                // Spaces at the edge of a style stay outside its markers ("** a**" is not bold).
                int from = segment.From, to = segment.To;
                while (from < to && char.IsWhiteSpace(text[from])) from++;
                while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
                if (from < to || length == 0) bare.Add((from, to));
            }
        }
        var replacements = new List<Replacement>();
        if (bare.Count == 0)
        {
            foreach (var span in covered.Distinct())
            {
                replacements.Add(new(span.Start, span.Open, ""));
                replacements.Add(new(span.ContentEnd, span.Close, ""));
            }
        }
        else if (length == 0)
        {
            string markers = open + close;
            return new TextEdit(start, 0, markers, start + open.Length, 0);
        }
        else
        {
            foreach (var (from, to) in bare)
            {
                replacements.Add(new(from, 0, open));
                replacements.Add(new(to, 0, close));
            }
        }
        return replacements.Count == 0 ? null : Compose(text, replacements, start, length);
    }

    private static TextEdit RemoveLink(string text, int start, int length, InlineSpan link)
    {
        var replacements = new List<Replacement> { new(link.Start, link.Open, ""), new(link.ContentEnd, link.Close, "") };
        return Compose(text, replacements, start, length);
    }

    private static TextEdit? ToggleList(string text, int start, int length, bool ordered)
    {
        var lines = Targets(text, start, length);
        var parsed = lines.Select(line => MarkdownLine.Parse(text.AsSpan(line.Start, line.End - line.Start))).ToList();
        bool all = parsed.All(p => p.IsListItem && p.Ordered == ordered);
        var replacements = new List<Replacement>();
        int number = 1;
        for (int i = 0; i < lines.Count; i++)
        {
            var (line, p) = (lines[i], parsed[i]);
            if (all) replacements.Add(new(line.Start + p.QuoteEnd, p.ItemStart - p.QuoteEnd, ""));
            else
            {
                string marker = ordered ? $"{number++}. " : "- ";
                if (p.IsListItem) replacements.Add(new(line.Start + p.MarkerStart, p.ItemStart - p.MarkerStart, marker));
                else replacements.Add(new(line.Start + p.TaskEnd, 0, marker));
            }
        }
        return Compose(text, replacements, start, length);
    }

    private static TextEdit? ToggleQuote(string text, int start, int length)
    {
        var lines = SelectedLines(text, start, length);
        bool single = lines.Count == 1;
        var filled = lines.Where(line => single || !IsBlank(text, line)).ToList();
        bool all = filled.All(line => MarkdownLine.Parse(text.AsSpan(line.Start, line.End - line.Start)).QuoteDepth > 0);
        var replacements = new List<Replacement>();
        foreach (var line in lines)
        {
            var span = text.AsSpan(line.Start, line.End - line.Start);
            var p = MarkdownLine.Parse(span);
            if (all)
            {
                if (p.QuoteDepth == 0) continue;
                int marker = span.IndexOf('>');
                int end = marker + 1 < span.Length && span[marker + 1] == ' ' ? marker + 2 : marker + 1;
                replacements.Add(new(line.Start, end, ""));
            }
            else if (p.QuoteDepth == 0) replacements.Add(new(line.Start, 0, IsBlank(text, line) && !single ? ">" : "> "));
        }
        return replacements.Count == 0 ? null : Compose(text, replacements, start, length);
    }

    private static TextEdit? ToggleCodeBlock(string text, int start, int length, SourceHighlighter? highlighter)
    {
        highlighter ??= Highlight(text);
        int line = highlighter.LineAt(start);
        if (highlighter.IsCode(line))
        {
            int open = line;
            while (highlighter.FenceBefore(open) is not null) open--;
            int close = open + 1;
            while (close < highlighter.LineCount && highlighter.FenceAfter(close) is not null) close++;
            var replacements = new List<Replacement>();
            int openStart = highlighter.LineStart(open), openEnd = highlighter.LineEnd(open);
            if (close == open + 1 && close < highlighter.LineCount)
                return Compose(text, [new(openStart, highlighter.LineEnd(close) - openStart, "")], start, length);
            replacements.Add(new(openStart, Math.Min(text.Length, openEnd + 1) - openStart, ""));
            if (close < highlighter.LineCount)
            {
                int closeStart = highlighter.LineStart(close);
                replacements.Add(new(closeStart - 1, highlighter.LineEnd(close) - closeStart + 1, ""));
            }
            return Compose(text, replacements, start, length);
        }
        var lines = SelectedLines(text, start, length);
        if (lines.Count == 1 && IsBlank(text, lines[0]))
            return new TextEdit(lines[0].Start, lines[0].End - lines[0].Start, "```\n\n```", lines[0].Start + 4, 0);
        // The opening fence lands before the selection and the closing one after it, even at a line end.
        return Compose(text, [new(lines[0].Start, 0, "```\n"), new(lines[^1].End, 0, "\n```")], start, length) with { SelectionStart = start + 4, SelectionLength = length };
    }

    /// <summary>Inserts a block on its own line, separated from neighboring text by blank lines.</summary>
    private static TextEdit InsertBlock(string text, int start, int length, string block, int caretInBlock)
    {
        var (lineStart, lineEnd) = LineBounds(text, start + length);
        bool nextFilled = lineEnd < text.Length && !IsBlank(text, LineBounds(text, lineEnd + 1));
        string after = nextFilled ? "\n" : "";
        if (IsBlank(text, new Line(lineStart, lineEnd)))
        {
            string before = lineStart > 0 && !IsBlank(text, LineBounds(text, lineStart - 1)) ? "\n" : "";
            return new TextEdit(lineStart, lineEnd - lineStart, before + block + after, lineStart + before.Length + caretInBlock, 0);
        }
        return new TextEdit(lineEnd, 0, "\n\n" + block + after, lineEnd + 2 + caretInBlock, 0);
    }

    private static InlineKind KindOf(MarkdownTool tool) => tool switch
    {
        MarkdownTool.Bold => InlineKind.Bold,
        MarkdownTool.Italic => InlineKind.Italic,
        MarkdownTool.Underline => InlineKind.Underline,
        MarkdownTool.Strikethrough => InlineKind.Strikethrough,
        MarkdownTool.Link => InlineKind.Link,
        _ => InlineKind.Code
    };

    /// <summary>The innermost span of <paramref name="kind"/> around [from, to], as absolute offsets.</summary>
    private static InlineSpan? Covering(string text, Line line, int from, int to, InlineKind kind, bool caret)
    {
        int content = line.Start + MarkdownLine.Parse(text.AsSpan(line.Start, line.End - line.Start)).ContentStart;
        if (content > line.End) return null;
        InlineSpan? best = null;
        foreach (var span in InlineScanner.Scan(text.AsSpan(content, line.End - content)))
        {
            if (span.Kind != kind) continue;
            var absolute = span with { Start = span.Start + content, End = span.End + content };
            bool inside = caret ? absolute.ContentStart <= from && to <= absolute.ContentEnd : absolute.Start <= from && to <= absolute.End;
            if (inside && (best is null || absolute.End - absolute.Start < best.Value.End - best.Value.Start)) best = absolute;
        }
        return best;
    }

    /// <summary>The selected part of each line's content, or the caret.</summary>
    private static IEnumerable<(Line Line, int From, int To)> Segments(string text, int start, int length, List<Line> lines)
    {
        foreach (var line in lines)
        {
            if (length == 0) { yield return (line, start, start); yield break; }
            int content = line.Start + MarkdownLine.Parse(text.AsSpan(line.Start, line.End - line.Start)).ContentStart;
            int from = Math.Max(start, Math.Min(content, line.End)), to = Math.Min(start + length, line.End);
            if (from < to && !text.AsSpan(from, to - from).IsWhiteSpace()) yield return (line, from, to);
        }
    }

    /// <summary>Lines touched by the selection; a selection that ends at a line start does not include that line.</summary>
    private static List<Line> SelectedLines(string text, int start, int length)
    {
        int end = start + length;
        if (length > 0 && end > 0 && text[end - 1] == '\n') end--;
        var lines = new List<Line>();
        var line = LineBounds(text, start);
        while (true)
        {
            lines.Add(new Line(line.Start, line.End));
            if (line.End >= end || line.End >= text.Length) break;
            line = LineBounds(text, line.End + 1);
        }
        return lines;
    }

    /// <summary>Selected lines with text; a single blank line counts, so a list can start on it.</summary>
    private static List<Line> Targets(string text, int start, int length)
    {
        var lines = SelectedLines(text, start, length);
        var filled = lines.Where(line => !IsBlank(text, line)).ToList();
        return lines.Count == 1 || filled.Count == 0 ? lines : filled;
    }

    private static (int Start, int End) LineBounds(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        int end = text.IndexOf('\n', offset);
        return (start, end < 0 ? text.Length : end);
    }

    private static bool IsBlank(string text, Line line) => text.AsSpan(line.Start, line.End - line.Start).IsWhiteSpace();
    private static bool IsBlank(string text, (int Start, int End) line) => IsBlank(text, new Line(line.Start, line.End));

    private static SourceHighlighter Highlight(string text)
    {
        var highlighter = new SourceHighlighter();
        highlighter.Reset(text);
        return highlighter;
    }

    /// <summary>
    /// Folds non-overlapping replacements into one edit and carries the selection through them:
    /// its start moves past text inserted at it, its end stays before it.
    /// </summary>
    private static TextEdit Compose(string text, List<Replacement> replacements, int start, int length)
    {
        replacements.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Length.CompareTo(b.Length));
        int from = replacements[0].Start, to = replacements.Max(r => r.Start + r.Length);
        var built = new StringBuilder();
        int cursor = from;
        foreach (var replacement in replacements)
        {
            built.Append(text, cursor, replacement.Start - cursor).Append(replacement.Text);
            cursor = replacement.Start + replacement.Length;
        }
        built.Append(text, cursor, to - cursor);
        int Map(int position, bool after)
        {
            int shift = 0;
            foreach (var r in replacements)
            {
                int end = r.Start + r.Length;
                if (position < r.Start || (position == r.Start && r.Length == 0 && !after)) break;
                if (position > end || (position == end && r.Length == 0)) { shift += r.Text.Length - r.Length; continue; }
                return r.Start + shift + (after ? r.Text.Length : 0);
            }
            return position + shift;
        }
        int selectionStart = Map(start, true);
        int selectionEnd = length == 0 ? selectionStart : Math.Max(selectionStart, Map(start + length, false));
        return new TextEdit(from, to - from, built.ToString(), selectionStart, selectionEnd - selectionStart);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:")]
    private static partial Regex SchemePattern();
}
