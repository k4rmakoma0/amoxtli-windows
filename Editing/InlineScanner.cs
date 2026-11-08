using System.Globalization;

namespace Amoxtli.Editing;

public enum InlineKind { Bold, Italic, Underline, Strikethrough, Code, Link }

/// <summary>A styled run in one line. Markers are included: content is [Start + Open, End - Close).</summary>
public readonly record struct InlineSpan(InlineKind Kind, int Start, int End, int Open, int Close)
{
    public int ContentStart => Start + Open;
    public int ContentEnd => End - Close;
}

/// <summary>
/// Inline Markdown for highlighting and toggling: code spans, links, &lt;u&gt;, ~~ and
/// CommonMark delimiter runs for * and _. Approximate by design; never throws on odd input.
/// </summary>
public static class InlineScanner
{
    private sealed class Delimiter(char marker, int start, int count, bool canOpen, bool canClose)
    {
        public readonly char Marker = marker;
        public readonly int Length = count;
        public readonly bool CanOpen = canOpen, CanClose = canClose;
        public int Start = start, Count = count;
        public bool Active = true;
    }

    public static List<InlineSpan> Scan(string line) => Scan(line.AsSpan());

    public static List<InlineSpan> Scan(ReadOnlySpan<char> line)
    {
        var spans = new List<InlineSpan>();
        var masked = new bool[line.Length];
        // Escapes and code spans in one pass: an escaped backtick opens nothing, and nothing
        // inside a code span (backslashes included) is Markdown.
        for (int i = 0; i < line.Length;)
        {
            if (line[i] == '\\' && i + 1 < line.Length && IsAsciiPunctuation(line[i + 1])) { masked[i] = masked[i + 1] = true; i += 2; continue; }
            if (line[i] != '`') { i++; continue; }
            int run = Run(line, i, '`');
            int close = i + run;
            while (close < line.Length)
            {
                if (line[close] == '`')
                {
                    int closeRun = Run(line, close, '`');
                    if (closeRun == run) break;
                    close += closeRun;
                }
                else close++;
            }
            if (close >= line.Length) { i += run; continue; }
            spans.Add(new InlineSpan(InlineKind.Code, i, close + run, run, run));
            Array.Fill(masked, true, i, close + run - i);
            i = close + run;
        }

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '[' || masked[i]) continue;
            int depth = 0, close = -1;
            for (int j = i + 1; j < line.Length; j++)
            {
                if (masked[j]) continue;
                if (line[j] == '[') depth++;
                else if (line[j] == ']' && depth-- == 0) { close = j; break; }
            }
            if (close < 0 || close + 1 >= line.Length || line[close + 1] != '(') continue;
            int parens = 0, end = -1;
            for (int j = close + 2; j < line.Length; j++)
            {
                if (masked[j]) continue;
                if (line[j] == '(') parens++;
                else if (line[j] == ')' && parens-- == 0) { end = j; break; }
            }
            if (end < 0) continue;
            int start = i > 0 && line[i - 1] == '!' && !masked[i - 1] ? i - 1 : i;
            spans.Add(new InlineSpan(InlineKind.Link, start, end + 1, i + 1 - start, end + 1 - close));
            Array.Fill(masked, true, start, i + 1 - start);
            Array.Fill(masked, true, close, end + 1 - close);
        }

        for (int i = 0; i < line.Length; i++)
        {
            if (masked[i] || !At(line, i, "<u>")) continue;
            for (int j = i + 3; j + 4 <= line.Length; j++)
            {
                if (masked[j] || !At(line, j, "</u>")) continue;
                spans.Add(new InlineSpan(InlineKind.Underline, i, j + 4, 3, 4));
                Array.Fill(masked, true, i, 3); Array.Fill(masked, true, j, 4);
                i = j + 3;
                break;
            }
        }

        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (masked[i] || line[i] != '~' || Run(line, i, '~') != 2 || i + 2 >= line.Length || char.IsWhiteSpace(line[i + 2])) continue;
            for (int j = i + 3; j + 1 < line.Length; j++)
            {
                if (masked[j] || line[j] != '~') continue;
                int run = Run(line, j, '~');
                if (run == 2 && !char.IsWhiteSpace(line[j - 1]))
                {
                    spans.Add(new InlineSpan(InlineKind.Strikethrough, i, j + 2, 2, 2));
                    masked[i] = masked[i + 1] = masked[j] = masked[j + 1] = true;
                    i = j + 1;
                    break;
                }
                j += run - 1;
            }
        }

        ScanEmphasis(line, masked, spans);
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        return spans;
    }

    private static void ScanEmphasis(ReadOnlySpan<char> line, bool[] masked, List<InlineSpan> spans)
    {
        var delimiters = new List<Delimiter>();
        for (int i = 0; i < line.Length;)
        {
            char c = line[i];
            if ((c != '*' && c != '_') || masked[i]) { i++; continue; }
            int run = 1;
            while (i + run < line.Length && line[i + run] == c && !masked[i + run]) run++;
            char before = i > 0 ? line[i - 1] : ' ', after = i + run < line.Length ? line[i + run] : ' ';
            bool beforeSpace = char.IsWhiteSpace(before), afterSpace = char.IsWhiteSpace(after);
            bool beforePunct = IsPunctuation(before), afterPunct = IsPunctuation(after);
            bool left = !afterSpace && (!afterPunct || beforeSpace || beforePunct);
            bool right = !beforeSpace && (!beforePunct || afterSpace || afterPunct);
            bool open = c == '*' ? left : left && (!right || beforePunct);
            bool close = c == '*' ? right : right && (!left || afterPunct);
            delimiters.Add(new Delimiter(c, i, run, open, close));
            i += run;
        }

        for (int c = 0; c < delimiters.Count; c++)
        {
            var closer = delimiters[c];
            while (closer.Active && closer.CanClose && closer.Count > 0)
            {
                int o = c - 1;
                for (; o >= 0; o--)
                {
                    var candidate = delimiters[o];
                    if (!candidate.Active || candidate.Count == 0 || !candidate.CanOpen || candidate.Marker != closer.Marker) continue;
                    // CommonMark "rule of 3" for runs that can both open and close.
                    if ((candidate.CanClose || closer.CanOpen) && (candidate.Length + closer.Length) % 3 == 0
                        && !(candidate.Length % 3 == 0 && closer.Length % 3 == 0)) continue;
                    break;
                }
                if (o < 0) break;
                var opener = delimiters[o];
                int use = opener.Count >= 2 && closer.Count >= 2 ? 2 : 1;
                int openStart = opener.Start + opener.Count - use;
                spans.Add(new InlineSpan(use == 2 ? InlineKind.Bold : InlineKind.Italic, openStart, closer.Start + use, use, use));
                opener.Count -= use;
                closer.Start += use;
                closer.Count -= use;
                for (int k = o + 1; k < c; k++) delimiters[k].Active = false;
            }
        }
    }

    private static int Run(ReadOnlySpan<char> line, int index, char c)
    {
        int end = index;
        while (end < line.Length && line[end] == c) end++;
        return end - index;
    }

    private static bool At(ReadOnlySpan<char> line, int index, string value) =>
        index + value.Length <= line.Length && line.Slice(index, value.Length).Equals(value, StringComparison.OrdinalIgnoreCase);

    internal static bool IsAsciiPunctuation(char c) => c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static bool IsPunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol;
}
