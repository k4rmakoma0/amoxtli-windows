using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Amoxtli.Editing;

/// <summary>
/// The flat model to canonical Markdown: "-" bullets (alternating with "*" for adjacent lists),
/// ** bold, * italic, &lt;u&gt; underline, ~~ strikethrough, fenced code, pipe tables. Spaces at
/// the edge of a style go outside its markers. Escapes cover what would otherwise read as syntax.
/// </summary>
public static partial class FormatSerializer
{
    private static readonly ConditionalWeakTable<IReadOnlyList<InlineRun>, string> InlineCache = new();
    private static readonly ConditionalWeakTable<IReadOnlyList<InlineRun>, string> CellCache = new();

    public static string Serialize(IReadOnlyList<FormatParagraph> paragraphs) => Serialize(paragraphs, null);

    /// <summary>Also fills <paramref name="offsets"/> with where each paragraph's block starts in the output.</summary>
    public static string Serialize(IReadOnlyList<FormatParagraph> paragraphs, int[]? offsets)
    {
        var output = new StringBuilder();
        var markers = ListMarkers(paragraphs);
        FormatParagraph? previous = null;
        for (int i = 0; i < paragraphs.Count; i++)
        {
            var p = paragraphs[i];
            // An empty paragraph only exists while editing; an empty list item still keeps its marker.
            if (p.Kind == BlockKind.Paragraph && p.Runs.Count == 0 && !StartsItem(previous, p))
            {
                if (offsets is not null) offsets[i] = output.Length;
                continue;
            }
            int end = i;
            if (p.Kind is BlockKind.Code or BlockKind.TableCell)
                while (end + 1 < paragraphs.Count && paragraphs[end + 1].Kind == p.Kind && paragraphs[end + 1].BlockId == p.BlockId) end++;
            var lines = p.Kind switch
            {
                BlockKind.Code => CodeLines(paragraphs, i, end),
                BlockKind.TableCell => TableLines(paragraphs, i, end),
                BlockKind.Heading => [new string('#', p.Level) + " " + HeadingText(p.Runs)],
                BlockKind.ThematicBreak => ["---"],
                BlockKind.Image or BlockKind.Raw => p.Source.Split('\n').ToList(),
                _ => InlineText(p.Runs).Split('\n').ToList()
            };
            if (previous is not null)
            {
                if (NeedsBlankLine(previous, p)) output.Append(ContinuationPrefix(Common(previous, p), previous, markers).TrimEnd()).Append('\n');
            }
            if (offsets is not null) for (int k = i; k <= end; k++) offsets[k] = output.Length;
            for (int line = 0; line < lines.Count; line++)
            {
                string prefix = line == 0 ? FirstPrefix(previous, p, markers) : ContinuationPrefix(p.Containers, p, markers);
                output.Append((prefix + lines[line]).TrimEnd()).Append('\n');
            }
            previous = paragraphs[end];
            i = end;
        }
        return output.ToString().TrimEnd('\n') + (output.Length > 0 ? "\n" : "");
    }

    /// <summary>A paragraph's inline Markdown, verified to read back as the same runs.</summary>
    public static string InlineText(IReadOnlyList<InlineRun> runs)
    {
        if (InlineCache.TryGetValue(runs, out var cached)) return cached;
        var normalized = FormatRuns.Normalize(runs);
        string expected = FormatRuns.Describe([new FormatParagraph { Runs = normalized }]);
        string? first = null;
        foreach (var options in Variants)
        {
            string text = EscapeLineStarts(Inlines(normalized, options, false));
            first ??= text;
            var parsed = FormatParser.Parse(text);
            if (parsed.Count == 1 && parsed[0].Kind == BlockKind.Paragraph && FormatRuns.Describe([parsed[0] with { Containers = [] }]) == expected)
            {
                InlineCache.AddOrUpdate(runs, text);
                return text;
            }
        }
        InlineCache.AddOrUpdate(runs, first!);
        return first!;
    }

    private readonly record struct Options(string Bold, string Italic);
    private static readonly Options[] Variants = [new("**", "*"), new("**", "_"), new("__", "*"), new("__", "_")];

    private static string HeadingText(IReadOnlyList<InlineRun> runs)
    {
        string text = InlineText(runs.Select(run => run with { Text = run.Text.Replace('\n', ' ') }).ToList());
        // A closing "#" would be read as an optional closing sequence.
        return text.EndsWith('#') ? text[..^1] + "\\#" : text;
    }

    /// <summary>
    /// A link becomes one verbatim piece carrying the styles all its text shares, so markers that
    /// start before the link close after it: "**bold [link](u)**".
    /// </summary>
    private static string Inlines(List<InlineRun> runs, Options options, bool inTable)
    {
        var pieces = new List<InlineRun>();
        for (int i = 0; i < runs.Count; i++)
        {
            if (!runs[i].Style.HasFlag(InlineStyle.Link)) { pieces.Add(runs[i]); continue; }
            string url = runs[i].Url ?? "";
            int end = i;
            while (end + 1 < runs.Count && runs[end + 1].Style.HasFlag(InlineStyle.Link) && runs[end + 1].Url == url) end++;
            var group = runs.Skip(i).Take(end - i + 1).ToList();
            var shared = group.Aggregate(InlineStyle.Bold | InlineStyle.Italic | InlineStyle.Strikethrough | InlineStyle.Underline, (all, run) => all & run.Style);
            var inner = group.Select(run => run with { Style = run.Style & ~InlineStyle.Link & ~shared, Url = null }).ToList();
            pieces.Add(new InlineRun($"[{Styled(inner, options, inTable, true)}]({Destination(url)})", shared | InlineStyle.Raw));
            i = end;
        }
        return Styled(pieces, options, inTable, false);
    }

    /// <summary>Opens and closes markers between runs, keeping edge spaces outside them.</summary>
    private static string Styled(List<InlineRun> runs, Options options, bool inTable, bool inLink)
    {
        var order = new[] { InlineStyle.Bold, InlineStyle.Italic, InlineStyle.Strikethrough, InlineStyle.Underline };
        string Open(InlineStyle s) => s switch { InlineStyle.Bold => options.Bold, InlineStyle.Italic => options.Italic, InlineStyle.Strikethrough => "~~", _ => "<u>" };
        string Close(InlineStyle s) => s == InlineStyle.Underline ? "</u>" : Open(s);
        var output = new StringBuilder();
        var stack = new List<InlineStyle>();
        string pending = "";
        foreach (var run in runs)
        {
            var target = run.Style & (InlineStyle.Bold | InlineStyle.Italic | InlineStyle.Strikethrough | InlineStyle.Underline);
            int keep = 0;
            while (keep < stack.Count && target.HasFlag(stack[keep])) keep++;
            for (int s = stack.Count - 1; s >= keep; s--) output.Append(Close(stack[s]));
            stack.RemoveRange(keep, stack.Count - keep);
            output.Append(pending);
            pending = "";
            string text = run.Text;
            bool verbatim = run.Style.HasFlag(InlineStyle.Raw);
            bool code = run.Style.HasFlag(InlineStyle.Code);
            if (!verbatim)
            {
                int lead = text.Length - text.TrimStart(' ').Length;
                bool opening = order.Any(s => target.HasFlag(s) && !stack.Contains(s));
                if (opening && lead > 0 && lead < text.Length) { output.Append(text[..lead]); text = text[lead..]; }
                int trail = text.Length - text.TrimEnd(' ').Length;
                if (trail > 0 && trail < text.Length) { pending = text[^trail..]; text = text[..^trail]; }
            }
            foreach (var style in order)
                if (target.HasFlag(style) && !stack.Contains(style)) { stack.Add(style); output.Append(Open(style)); }
            output.Append(verbatim ? text : code ? CodeSpan(text) : Escape(text, inTable, inLink));
        }
        for (int s = stack.Count - 1; s >= 0; s--) output.Append(Close(stack[s]));
        output.Append(pending);
        return output.ToString();
    }

    private static string CodeSpan(string text)
    {
        int longest = 0, run = 0;
        foreach (char c in text) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        string fence = new('`', longest + 1);
        bool pad = text.StartsWith('`') || text.EndsWith('`') || (text.Length > 1 && text[0] == ' ' && text[^1] == ' ' && text.Trim().Length > 0);
        return pad ? $"{fence} {text} {fence}" : fence + text + fence;
    }

    private static string Destination(string url) =>
        url.Length == 0 ? "" : url.IndexOfAny([' ', '<', '>']) >= 0 || url.Count(c => c == '(') != url.Count(c => c == ')')
            ? "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">" : url;

    /// <summary>Backslash-escapes characters that would start inline syntax. A forced line break becomes "\" + newline.</summary>
    private static string Escape(string text, bool inTable, bool inLink)
    {
        var output = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char before = i > 0 ? text[i - 1] : ' ', after = i + 1 < text.Length ? text[i + 1] : ' ';
            if (c == '\n') { output.Append(inTable ? "<br>" : "\\\n"); continue; }
            bool escape = c switch
            {
                '\\' => after < 128 && (char.IsPunctuation(after) || char.IsSymbol(after)) || i == text.Length - 1,
                '*' or '`' or '[' or '~' or '^' => true,
                ']' => inLink,
                '_' => !char.IsLetterOrDigit(before) || !char.IsLetterOrDigit(after),
                '<' => char.IsLetter(after) || after is '/' or '!' or '?',
                '&' => EntityStart().IsMatch(text.AsSpan(i)),
                '=' or '+' => after == c,
                '|' => inTable,
                _ => false
            };
            if (escape) output.Append('\\');
            output.Append(c);
        }
        return output.ToString();
    }

    /// <summary>Escapes what would read as block syntax at the start of a line: headings, quotes, lists, rules, fences.</summary>
    private static string EscapeLineStarts(string text) => string.Join("\n", text.Split('\n').Select(line =>
    {
        line = line.TrimStart(' ');
        if (line.Length == 0) return line;
        if (line[0] is '#' or '>' || (line[0] is '-' or '+' && (line.Length == 1 || line[1] == ' ')) || line.StartsWith("```") || line.StartsWith("~~~")
            || SetextOrRule().IsMatch(line))
            return "\\" + line;
        var ordered = OrderedMarker().Match(line);
        return ordered.Success ? line.Insert(ordered.Groups[1].Length, "\\") : line;
    }));

    private static List<string> CodeLines(IReadOnlyList<FormatParagraph> paragraphs, int first, int last)
    {
        var lines = Enumerable.Range(first, last - first + 1).Select(i => paragraphs[i].Text).ToList();
        int longest = 2;
        foreach (var line in lines)
        {
            var match = BacktickRun().Match(line.TrimStart());
            if (match.Success) longest = Math.Max(longest, match.Length);
        }
        string fence = new('`', longest + 1);
        return [fence + paragraphs[first].Info, .. lines, fence];
    }

    private static List<string> TableLines(IReadOnlyList<FormatParagraph> paragraphs, int first, int last)
    {
        var cells = Enumerable.Range(first, last - first + 1).Select(i => paragraphs[i]).ToList();
        int columns = Math.Max(1, cells.Max(c => c.Column) + 1);
        var alignments = cells[0].Alignments;
        var lines = new List<string>();
        foreach (var row in cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            var texts = Enumerable.Range(0, columns).Select(c => row.FirstOrDefault(cell => cell.Column == c) is { } cell ? CellText(cell.Runs) : "");
            lines.Add("| " + string.Join(" | ", texts) + " |");
            if (lines.Count == 1)
                lines.Add("| " + string.Join(" | ", Enumerable.Range(0, columns).Select(c => (c < alignments.Count ? alignments[c] : TableAlignment.None) switch
                {
                    TableAlignment.Left => ":---",
                    TableAlignment.Center => ":---:",
                    TableAlignment.Right => "---:",
                    _ => "---"
                })) + " |");
        }
        return lines;
    }

    private static string CellText(IReadOnlyList<InlineRun> runs)
    {
        if (CellCache.TryGetValue(runs, out var cached)) return cached;
        string text = Inlines(FormatRuns.Normalize(runs), Variants[0], true);
        CellCache.AddOrUpdate(runs, text);
        return text;
    }

    private static List<Container> Common(FormatParagraph a, FormatParagraph b)
    {
        var common = new List<Container>();
        for (int i = 0; i < Math.Min(a.Containers.Count, b.Containers.Count) && a.Containers[i] == b.Containers[i]; i++) common.Add(a.Containers[i]);
        return common;
    }

    /// <summary>A new item in the same list, or a nested list right after its parent, keeps the list tight.</summary>
    private static bool NeedsBlankLine(FormatParagraph previous, FormatParagraph p)
    {
        int shared = Common(previous, p).Count;
        if (shared == p.Containers.Count) return true;
        var next = p.Containers[shared];
        if (next.IsQuote) return true;
        // Only a list nested right after its item's own text stays tight; elsewhere a list could be read as paragraph text.
        if (shared == previous.Containers.Count) return shared == 0 || previous.Containers[^1].IsQuote || previous.Kind is not BlockKind.Paragraph;
        var other = previous.Containers[shared];
        return other.IsQuote || other.ListId != next.ListId;
    }

    private static bool StartsItem(FormatParagraph? previous, FormatParagraph p) =>
        p.Containers.Count > 0 && !p.Containers[^1].IsQuote && (previous is null || !previous.Containers.Contains(p.Containers[^1]));

    private static string FirstPrefix(FormatParagraph? previous, FormatParagraph p, Dictionary<int, (string Bullet, Dictionary<int, int> Numbers)> markers)
    {
        var prefix = new StringBuilder();
        foreach (var c in p.Containers)
        {
            if (c.IsQuote) { prefix.Append("> "); continue; }
            string marker = Marker(c, markers);
            if (previous is null || !previous.Containers.Contains(c))
                prefix.Append(marker).Append(' ').Append(c.Task is { } done ? (done ? "[x] " : "[ ] ") : "");
            else prefix.Append(' ', marker.Length + 1);
        }
        return prefix.ToString();
    }

    private static string ContinuationPrefix(IReadOnlyList<Container> containers, FormatParagraph owner, Dictionary<int, (string Bullet, Dictionary<int, int> Numbers)> markers)
    {
        var prefix = new StringBuilder();
        foreach (var c in containers) prefix.Append(c.IsQuote ? "> " : new string(' ', Marker(c, markers).Length + 1));
        return prefix.ToString();
    }

    private static string Marker(Container item, Dictionary<int, (string Bullet, Dictionary<int, int> Numbers)> markers)
    {
        var (bullet, numbers) = markers[item.ListId];
        return item.Ordered ? $"{item.Start + numbers[item.Id]}{bullet}" : bullet;
    }

    /// <summary>Per list: its bullet ("-" or "*", ". " or ")" for numbers) and each item's position.</summary>
    private static Dictionary<int, (string Bullet, Dictionary<int, int> Numbers)> ListMarkers(IReadOnlyList<FormatParagraph> paragraphs)
    {
        var markers = new Dictionary<int, (string, Dictionary<int, int>)>();
        // The last list seen at each nesting position, so two lists in a row get different bullets.
        var lastAtDepth = new Dictionary<string, (int ListId, bool Ordered, string Bullet)>();
        foreach (var p in paragraphs)
        {
            var path = new StringBuilder();
            foreach (var c in p.Containers)
            {
                if (c.IsQuote) { path.Append('q').Append(c.Id).Append('/'); continue; }
                string key = path.ToString();
                if (!markers.TryGetValue(c.ListId, out var entry))
                {
                    string bullet = c.Ordered ? "." : "-";
                    if (lastAtDepth.TryGetValue(key, out var last) && last.ListId != c.ListId && last.Ordered == c.Ordered)
                        bullet = c.Ordered ? (last.Bullet == "." ? ")" : ".") : (last.Bullet == "-" ? "*" : "-");
                    entry = (bullet, new Dictionary<int, int>());
                    markers[c.ListId] = entry;
                }
                if (!entry.Item2.ContainsKey(c.Id)) entry.Item2[c.Id] = entry.Item2.Count;
                lastAtDepth[key] = (c.ListId, c.Ordered, entry.Item1);
                path.Append('l').Append(c.Id).Append('/');
            }
        }
        return markers;
    }

    [GeneratedRegex(@"^&(#[0-9]+|#[xX][0-9a-fA-F]+|[A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex EntityStart();
    [GeneratedRegex(@"^(=+|-+|(\*\s*){3,}|(_\s*){3,}|(-\s*){3,})\s*$")]
    private static partial Regex SetextOrRule();
    [GeneratedRegex(@"^(\d{1,9})[.)](\s|$)")]
    private static partial Regex OrderedMarker();
    [GeneratedRegex(@"^`+|^~+")]
    private static partial Regex BacktickRun();
}
