namespace Amoxtli.Editing;

/// <summary>Token spans carry their <see cref="TokenKind"/> in <see cref="HighlightSpan.Level"/>.</summary>
public enum HighlightKind { Heading, Syntax, Bold, Italic, Underline, Strikethrough, Code, CodeBlock, LinkText, Quote, ListMarker, Token }

/// <summary>Absolute UTF-16 range. Later spans override earlier ones where they overlap.</summary>
public readonly record struct HighlightSpan(int Start, int Length, HighlightKind Kind, int Level = 0);

/// <summary>
/// Line-based Markdown highlighting for the source editor. Each line records whether it ends
/// inside a fenced block, so an edit rescans only its own lines, plus whatever follows when
/// it opens or closes a fence.
/// </summary>
public sealed class SourceHighlighter
{
    private string _text = "";
    private List<int> _lineStarts = [0];
    private List<FenceState?> _after = [null];

    public string Text => _text;
    public int LineCount => _lineStarts.Count;
    public int LineStart(int line) => _lineStarts[line];
    public int LineEnd(int line) => line + 1 < _lineStarts.Count ? _lineStarts[line + 1] - 1 : _text.Length;
    public FenceState? FenceBefore(int line) => line == 0 ? null : _after[line - 1];
    public FenceState? FenceAfter(int line) => _after[line];
    /// <summary>Inside a fenced block, fence lines included.</summary>
    public bool IsCode(int line) => FenceBefore(line) is not null || _after[line] is not null;

    public int LineAt(int offset)
    {
        int index = _lineStarts.BinarySearch(Math.Clamp(offset, 0, _text.Length));
        return index >= 0 ? index : ~index - 1;
    }

    // Tokens of the code block opening at a line; a block is lexed whole so multi-line comments and strings come out right.
    private readonly Dictionary<int, (int ContentStart, List<CodeToken> Tokens)> _blockTokens = [];

    public void Reset(string text)
    {
        _blockTokens.Clear();
        _text = text;
        _lineStarts = LineStarts(text);
        _after = new List<FenceState?>(_lineStarts.Count);
        FenceState? state = null;
        for (int line = 0; line < _lineStarts.Count; line++) _after.Add(state = Next(state, Line(line)));
    }

    /// <summary>Applies an edit already made to <paramref name="text"/> and returns the lines to restyle.</summary>
    public (int First, int Last) Update(string text, TextEdit edit)
    {
        int first = LineAt(edit.Start);
        int oldCount = _lineStarts.Count;
        var oldAfter = _after;
        _text = text;
        _lineStarts = LineStarts(text);
        int count = _lineStarts.Count, delta = count - oldCount;
        int lastEdited = LineAt(edit.Start + edit.Text.Length);
        var after = new List<FenceState?>(count);
        after.AddRange(oldAfter.Take(first));
        FenceState? state = first > 0 ? after[first - 1] : null;
        int line = first, last = first;
        for (; line < count; line++)
        {
            after.Add(state = Next(state, Line(line)));
            last = line;
            int old = line - delta;
            if (line >= lastEdited && old >= 0 && old < oldCount && Equals(oldAfter[old], state)) { line++; break; }
        }
        for (; line < count; line++) after.Add(oldAfter[line - delta]);
        _after = after;
        _blockTokens.Clear();
        return (first, last);
    }

    public List<HighlightSpan> GetSpans(int line)
    {
        var spans = new List<HighlightSpan>();
        int start = _lineStarts[line];
        var text = Line(line);
        void Add(int from, int length, HighlightKind kind, int level = 0)
        {
            if (length > 0) spans.Add(new HighlightSpan(start + from, length, kind, level));
        }

        if (FenceBefore(line) is not null)
        {
            Add(0, text.Length, HighlightKind.CodeBlock);
            if (_after[line] is null) Add(0, text.Length, HighlightKind.Syntax);
            else
            {
                int end = start + text.Length;
                foreach (var token in BlockTokens(line))
                {
                    int from = Math.Max(token.Start, start), to = Math.Min(token.Start + token.Length, end);
                    if (from < to) spans.Add(new HighlightSpan(from, to - from, HighlightKind.Token, (int)token.Kind));
                }
            }
            return spans;
        }
        if (_after[line] is not null)
        {
            Add(0, text.Length, HighlightKind.CodeBlock);
            Add(0, text.Length, HighlightKind.Syntax);
            return spans;
        }

        var parsed = MarkdownLine.Parse(text);
        if (parsed.QuoteDepth > 0)
        {
            Add(parsed.QuoteEnd, text.Length - parsed.QuoteEnd, HighlightKind.Quote);
            Add(0, parsed.QuoteEnd, HighlightKind.Syntax);
        }
        if (parsed.ThematicBreak) { Add(parsed.QuoteEnd, text.Length - parsed.QuoteEnd, HighlightKind.Syntax); return spans; }
        if (parsed.IsListItem) Add(parsed.MarkerStart, parsed.MarkerLength, HighlightKind.ListMarker);
        if (parsed.IsTask) Add(parsed.ItemStart, 3, HighlightKind.Syntax);
        if (parsed.HeadingLevel > 0)
        {
            Add(parsed.HeadingStart, text.Length - parsed.HeadingStart, HighlightKind.Heading, parsed.HeadingLevel);
            Add(parsed.HeadingStart, parsed.HeadingLevel, HighlightKind.Syntax);
            int end = text.Length;
            while (end > parsed.ContentStart && text[end - 1] == ' ') end--;
            int hashes = end;
            while (hashes > parsed.ContentStart && text[hashes - 1] == '#') hashes--;
            if (hashes < end && (hashes == parsed.ContentStart || text[hashes - 1] == ' ')) Add(hashes, end - hashes, HighlightKind.Syntax);
        }

        int content = parsed.ContentStart;
        var inline = InlineScanner.Scan(text[content..]);
        foreach (var span in inline)
        {
            var kind = span.Kind switch
            {
                InlineKind.Bold => HighlightKind.Bold,
                InlineKind.Italic => HighlightKind.Italic,
                InlineKind.Underline => HighlightKind.Underline,
                InlineKind.Strikethrough => HighlightKind.Strikethrough,
                InlineKind.Code => HighlightKind.Code,
                _ => HighlightKind.LinkText
            };
            // Lines and link color belong to the text, not to its markers.
            if (kind is HighlightKind.LinkText or HighlightKind.Underline or HighlightKind.Strikethrough)
                Add(content + span.ContentStart, span.ContentEnd - span.ContentStart, kind);
            else Add(content + span.Start, span.End - span.Start, kind);
        }
        foreach (var span in inline)
        {
            Add(content + span.Start, span.Open, HighlightKind.Syntax);
            Add(content + span.ContentEnd, span.Close, HighlightKind.Syntax);
        }
        return spans;
    }

    /// <summary>The first and last line of the fenced block holding <paramref name="line"/>, fences included.</summary>
    public (int Open, int Close) BlockBounds(int line)
    {
        int open = line;
        while (open > 0 && FenceBefore(open) is not null) open--;
        int close = Math.Max(open + 1, line);
        while (close < LineCount - 1 && _after[close] is not null) close++;
        return (open, Math.Min(close, LineCount - 1));
    }

    /// <summary>The block's tokens in absolute offsets, lexed once per text version.</summary>
    private List<CodeToken> BlockTokens(int line)
    {
        var (open, close) = BlockBounds(line);
        if (_blockTokens.TryGetValue(open, out var cached)) return cached.Tokens;
        int contentStart = open + 1 < LineCount ? _lineStarts[open + 1] : _text.Length;
        int contentEnd = _after[close] is null && close > open ? Math.Max(contentStart, _lineStarts[close] - 1) : LineEnd(close);
        var language = CodeLexer.Detect(_after[open]?.Info ?? "");
        var tokens = CodeLexer.Tokenize(_text[contentStart..Math.Max(contentStart, contentEnd)], language)
            .Select(token => token with { Start = token.Start + contentStart }).ToList();
        _blockTokens[open] = (contentStart, tokens);
        return tokens;
    }

    private ReadOnlySpan<char> Line(int line) => _text.AsSpan(_lineStarts[line], LineEnd(line) - _lineStarts[line]);

    private static FenceState? Next(FenceState? state, ReadOnlySpan<char> line) => state is null
        ? MarkdownLine.OpeningFence(line)
        : MarkdownLine.ClosesFence(line, state) ? null : state;

    internal static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1)) starts.Add(i + 1);
        return starts;
    }
}
