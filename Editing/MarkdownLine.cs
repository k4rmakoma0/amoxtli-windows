namespace Amoxtli.Editing;

/// <summary>
/// Container and block syntax at the start of one Markdown line, as offsets into that line:
/// <c>&gt; - [ ] ## text</c>. A list marker only counts with a space after it.
/// </summary>
public readonly record struct MarkdownLine(
    int QuoteEnd, int QuoteDepth,
    int MarkerStart, int MarkerLength, int ItemStart, bool Ordered, int Number, char Delimiter,
    int TaskEnd, int HeadingStart, int HeadingLevel, int ContentStart, bool ThematicBreak)
{
    public bool IsListItem => MarkerStart >= 0;
    public bool IsTask => TaskEnd > ItemStart;

    public static MarkdownLine Parse(ReadOnlySpan<char> line)
    {
        int i = 0, depth = 0, quoteEnd = 0;
        while (true)
        {
            int j = i, spaces = 0;
            while (j < line.Length && line[j] == ' ' && spaces < 3) { j++; spaces++; }
            if (j >= line.Length || line[j] != '>') break;
            j++;
            if (j < line.Length && line[j] == ' ') j++;
            depth++; i = quoteEnd = j;
        }
        if (IsThematicBreak(line[quoteEnd..]))
            return new(quoteEnd, depth, -1, 0, quoteEnd, false, 0, '\0', quoteEnd, -1, 0, line.Length, true);

        int k = quoteEnd;
        while (k < line.Length && (line[k] == ' ' || line[k] == '\t')) k++;
        int markerStart = -1, markerLength = 0, number = 0;
        bool ordered = false;
        char delimiter = '\0';
        if (k + 1 < line.Length && line[k] is '-' or '*' or '+' && line[k + 1] == ' ')
        {
            markerStart = k; markerLength = 1; delimiter = line[k];
        }
        else
        {
            int d = k;
            while (d < line.Length && char.IsAsciiDigit(line[d]) && d - k < 9) d++;
            if (d > k && d + 1 < line.Length && line[d] is '.' or ')' && line[d + 1] == ' ' && int.TryParse(line[k..d], out number))
            {
                markerStart = k; markerLength = d + 1 - k; ordered = true; delimiter = line[d];
            }
        }
        int itemStart = markerStart >= 0 ? markerStart + markerLength : k;
        if (markerStart >= 0) while (itemStart < line.Length && line[itemStart] == ' ') itemStart++;
        int taskEnd = itemStart;
        if (markerStart >= 0 && itemStart + 2 < line.Length && line[itemStart] == '[' && line[itemStart + 1] is ' ' or 'x' or 'X' && line[itemStart + 2] == ']'
            && (itemStart + 3 == line.Length || line[itemStart + 3] == ' '))
            taskEnd = Math.Min(line.Length, itemStart + 4);

        int h = taskEnd, headingStart = -1, level = 0;
        while (h < line.Length && line[h] == ' ' && h - taskEnd < 3) h++;
        int hashes = 0;
        while (h + hashes < line.Length && line[h + hashes] == '#') hashes++;
        if (hashes is >= 1 and <= 6 && (h + hashes == line.Length || line[h + hashes] == ' '))
        {
            headingStart = h; level = hashes;
        }
        int content = level > 0 ? Math.Min(line.Length, h + hashes + 1) : taskEnd;
        return new(quoteEnd, depth, markerStart, markerLength, itemStart, ordered, number, delimiter, taskEnd, headingStart, level, content, false);
    }

    public static bool IsThematicBreak(ReadOnlySpan<char> line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ' && i < 3) i++;
        if (i >= line.Length || line[i] is not ('-' or '*' or '_')) return false;
        char c = line[i];
        int count = 0;
        for (; i < line.Length; i++)
        {
            if (line[i] == c) count++;
            else if (line[i] is not (' ' or '\t')) return false;
        }
        return count >= 3;
    }

    /// <summary>An opening fence: three or more ` or ~ after any container prefix.</summary>
    public static FenceState? OpeningFence(ReadOnlySpan<char> line)
    {
        int i = Parse(line).QuoteEnd;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i >= line.Length || line[i] is not ('`' or '~')) return null;
        char c = line[i];
        int count = 0;
        while (i + count < line.Length && line[i + count] == c) count++;
        if (count < 3) return null;
        var info = line[(i + count)..].Trim();
        if (c == '`' && info.Contains('`')) return null;
        return new FenceState(c, count, info.ToString());
    }

    public static bool ClosesFence(ReadOnlySpan<char> line, FenceState fence)
    {
        int i = Parse(line).QuoteEnd;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        int count = 0;
        while (i + count < line.Length && line[i + count] == fence.Marker) count++;
        return count >= fence.Length && line[(i + count)..].IsWhiteSpace();
    }
}

/// <summary>An open fenced code block. <see cref="Info"/> is the info string, such as "swift".</summary>
public sealed record FenceState(char Marker, int Length, string Info);
