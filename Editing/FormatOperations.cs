namespace Amoxtli.Editing;

/// <summary>
/// Structural edits on the flat model, as in Pages: Return, Backspace and Delete at the edges,
/// Tab nesting, typed "# " headings and the paragraph tools. Each returns the new paragraphs and
/// where the caret goes; none mutates its input.
/// </summary>
public static class FormatOperations
{
    public sealed record Result(List<FormatParagraph> Paragraphs, int Focus, int Offset);

    public static int NextId(IReadOnlyList<FormatParagraph> paragraphs) => 1 + paragraphs
        .SelectMany(p => p.Containers.SelectMany(c => new[] { c.Id, c.ListId }).Append(p.BlockId)).DefaultIfEmpty(0).Max();

    public static int TextLength(IReadOnlyList<InlineRun> runs) => runs.Sum(run => run.Text.Length);

    public static (List<InlineRun> Left, List<InlineRun> Right) SplitRuns(IReadOnlyList<InlineRun> runs, int offset)
    {
        var left = new List<InlineRun>();
        var right = new List<InlineRun>();
        int position = 0;
        foreach (var run in runs)
        {
            int end = position + run.Text.Length;
            if (end <= offset) left.Add(run);
            else if (position >= offset) right.Add(run);
            else
            {
                left.Add(run with { Text = run.Text[..(offset - position)] });
                right.Add(run with { Text = run.Text[(offset - position)..] });
            }
            position = end;
        }
        return (FormatRuns.Normalize(left), FormatRuns.Normalize(right));
    }

    /// <summary>
    /// Keeps the document editable: never empty, and an editable paragraph after a table, code
    /// block or static block at the end, so its last cell or line can be left behind.
    /// </summary>
    public static List<FormatParagraph> Ensure(List<FormatParagraph> paragraphs)
    {
        var result = paragraphs.ToList();
        if (result.Count == 0 || result[^1].Kind is not (BlockKind.Paragraph or BlockKind.Heading)) result.Add(FormatParagraph.Plain(""));
        return result;
    }

    public static bool StartsItem(IReadOnlyList<FormatParagraph> paragraphs, int index, Container item) =>
        index == 0 || !paragraphs[index - 1].Containers.Contains(item);

    /// <summary>Return at <paramref name="offset"/>: a new item in a list, plain text after a heading, out of the list from an empty item.</summary>
    public static Result Split(List<FormatParagraph> paragraphs, int index, int offset)
    {
        var p = paragraphs[index];
        var result = paragraphs.ToList();
        if (p.Runs.Count == 0 && p.Containers.Count > 0 && !p.Containers[^1].IsQuote)
        {
            result[index] = Outdented(result, index, p);
            return new Result(result, index, 0);
        }
        var (left, right) = SplitRuns(p.Runs, offset);
        int length = TextLength(p.Runs);
        var containers = p.Containers.ToList();
        if (containers.Count > 0 && !containers[^1].IsQuote)
        {
            var item = containers[^1];
            containers[^1] = item with { Id = NextId(paragraphs), Task = item.Task is null ? null : false };
        }
        var first = p with { Runs = left, Kind = p.Kind == BlockKind.Heading && offset == 0 && length > 0 ? BlockKind.Paragraph : p.Kind, LiteralHash = false };
        var second = p with { Runs = right, Containers = containers, Kind = p.Kind == BlockKind.Heading && offset >= length ? BlockKind.Paragraph : p.Kind, LiteralHash = false };
        // Return at the start of a heading leaves a plain line above it, like Pages.
        if (p.Kind == BlockKind.Heading && offset == 0 && length > 0) { first = first with { Kind = BlockKind.Paragraph }; second = second with { Kind = BlockKind.Heading }; }
        result[index] = first;
        result.Insert(index + 1, second);
        return new Result(result, index + 1, 0);
    }

    /// <summary>Backspace at the start: drops a heading, then the innermost list or quote, then joins the paragraph above.</summary>
    public static Result? Backspace(List<FormatParagraph> paragraphs, int index)
    {
        var p = paragraphs[index];
        var result = paragraphs.ToList();
        if (p.Kind == BlockKind.Heading) { result[index] = p with { Kind = BlockKind.Paragraph }; return new Result(result, index, 0); }
        if (p.Kind is BlockKind.Paragraph && p.Containers.Count > 0)
        {
            var inner = p.Containers[^1];
            if (inner.IsQuote || StartsItem(paragraphs, index, inner))
            {
                result[index] = p with { Containers = p.Containers.Take(p.Containers.Count - 1).ToList() };
                return new Result(result, index, 0);
            }
        }
        if (index == 0 || p.Kind is not (BlockKind.Paragraph or BlockKind.Heading)) return null;
        var previous = paragraphs[index - 1];
        if (!previous.IsEditable)
        {
            int first = index - 1;
            while (first > 0 && paragraphs[first - 1].Kind == previous.Kind && paragraphs[first - 1].BlockId == previous.BlockId) first--;
            result.RemoveRange(first, index - first);
            return new Result(result, first, 0);
        }
        if (previous.Kind == BlockKind.TableCell) return new Result(result, index - 1, TextLength(previous.Runs));
        int offset = TextLength(previous.Runs);
        result[index - 1] = previous with { Runs = FormatRuns.Normalize(previous.Runs.Concat(p.Runs)) };
        result.RemoveAt(index);
        return new Result(result, index - 1, offset);
    }

    /// <summary>Delete at the end: joins the next paragraph, or removes a static block after it.</summary>
    public static Result? DeleteForward(List<FormatParagraph> paragraphs, int index)
    {
        if (index + 1 >= paragraphs.Count) return null;
        var p = paragraphs[index];
        var next = paragraphs[index + 1];
        var result = paragraphs.ToList();
        int offset = TextLength(p.Runs);
        if (!next.IsEditable)
        {
            int last = index + 1;
            while (last + 1 < paragraphs.Count && paragraphs[last + 1].Kind == next.Kind && paragraphs[last + 1].BlockId == next.BlockId) last++;
            result.RemoveRange(index + 1, last - index);
            return new Result(result, index, offset);
        }
        if (p.Kind is not (BlockKind.Paragraph or BlockKind.Heading) || next.Kind is not (BlockKind.Paragraph or BlockKind.Heading)) return null;
        result[index] = p with { Runs = FormatRuns.Normalize(p.Runs.Concat(next.Runs)) };
        result.RemoveAt(index + 1);
        return new Result(result, index, offset);
    }

    /// <summary>Tab: the item joins the list under the item above it.</summary>
    public static Result? Indent(List<FormatParagraph> paragraphs, int index, int offset)
    {
        var p = paragraphs[index];
        int depth = p.Containers.Count - 1;
        if (depth < 0 || p.Containers[depth].IsQuote) return null;
        var item = p.Containers[depth];
        // The previous item of the same list, and any list already nested under it.
        int previousIndex = -1;
        for (int i = index - 1; i >= 0; i--)
        {
            var c = paragraphs[i].Containers;
            if (c.Count <= depth || !c.Take(depth).SequenceEqual(p.Containers.Take(depth))) break;
            if (!c[depth].IsQuote && c[depth].ListId == item.ListId && c[depth].Id != item.Id) { previousIndex = i; break; }
        }
        if (previousIndex < 0) return null;
        var parent = paragraphs[previousIndex].Containers[depth];
        int childList = paragraphs.Take(index).Select(x => x.Containers).FirstOrDefault(c => c.Count > depth + 1 && c[depth] == parent && !c[depth + 1].IsQuote)?[depth + 1].ListId ?? NextId(paragraphs);
        var nested = item with { ListId = childList };
        var result = paragraphs.Select(x => x.Containers.Count > depth && x.Containers[depth] == item
            ? x with { Containers = [.. x.Containers.Take(depth), parent, nested, .. x.Containers.Skip(depth + 1)] } : x).ToList();
        return new Result(result, index, offset);
    }

    /// <summary>Shift+Tab: the item moves up a level, or leaves the list at the top level.</summary>
    public static Result? Outdent(List<FormatParagraph> paragraphs, int index, int offset)
    {
        var p = paragraphs[index];
        if (p.Containers.Count == 0 || p.Containers[^1].IsQuote) return null;
        var result = paragraphs.ToList();
        result[index] = Outdented(paragraphs, index, p);
        var item = p.Containers[^1];
        // The rest of the item follows it.
        for (int i = index + 1; i < result.Count && result[i].Containers.Contains(item); i++)
            result[i] = result[i] with { Containers = result[index].Containers.Concat(result[i].Containers.SkipWhile(c => c != item).Skip(1)).ToList() };
        return new Result(result, index, offset);
    }

    private static FormatParagraph Outdented(IReadOnlyList<FormatParagraph> paragraphs, int index, FormatParagraph p)
    {
        int depth = p.Containers.Count - 1;
        if (depth >= 1 && !p.Containers[depth - 1].IsQuote)
        {
            var parent = p.Containers[depth - 1];
            var moved = p.Containers[depth] with { ListId = parent.ListId, Ordered = parent.Ordered, Start = parent.Start, Id = NextId(paragraphs) };
            return p with { Containers = [.. p.Containers.Take(depth - 1), moved] };
        }
        return p with { Containers = p.Containers.Take(depth).ToList() };
    }

    /// <summary>"# " to "###### " typed at the start of plain text makes a heading of that level, keeping styles and containers.</summary>
    public static FormatParagraph? TypedHeading(FormatParagraph p)
    {
        if (p.Kind != BlockKind.Paragraph || p.LiteralHash) return null;
        string text = p.Text;
        int hashes = 0;
        while (hashes < text.Length && hashes < 7 && text[hashes] == '#') hashes++;
        if (hashes is < 1 or > 6 || hashes >= text.Length || text[hashes] != ' ' || p.Runs[0].Style.HasFlag(InlineStyle.Raw)) return null;
        return p with { Kind = BlockKind.Heading, Level = hashes, Runs = SplitRuns(p.Runs, hashes + 1).Right };
    }

    /// <summary>A typed "\#" shows "#" and stays literal text.</summary>
    public static FormatParagraph? TypedEscapedHash(FormatParagraph p)
    {
        if (p.Kind != BlockKind.Paragraph || !p.Text.StartsWith("\\#")) return null;
        return p with { Runs = SplitRuns(p.Runs, 1).Right, LiteralHash = true };
    }

    /// <summary>A heading, item, quote or code line whose text was all deleted becomes plain text at once.</summary>
    public static FormatParagraph? Emptied(FormatParagraph p) =>
        p.Runs.Count == 0 && (p.Kind is BlockKind.Heading or BlockKind.Code || p.Kind == BlockKind.Paragraph && p.Containers.Count > 0)
            ? FormatParagraph.Plain("") : null;

    public static Result SetHeading(List<FormatParagraph> paragraphs, int index, int offset, int level)
    {
        var result = paragraphs.ToList();
        var p = result[index];
        if (p.Kind is BlockKind.Paragraph or BlockKind.Heading)
            result[index] = level == 0 ? p with { Kind = BlockKind.Paragraph } : p with { Kind = BlockKind.Heading, Level = level };
        return new Result(result, index, offset);
    }

    /// <summary>Bullets or numbers: off when already that list, switches the whole list's type when the other, else joins the list above.</summary>
    public static Result ToggleList(List<FormatParagraph> paragraphs, int index, int offset, bool ordered)
    {
        var result = paragraphs.ToList();
        var p = result[index];
        if (p.Kind is not (BlockKind.Paragraph or BlockKind.Heading)) return new Result(result, index, offset);
        var inner = p.Containers.Count > 0 ? p.Containers[^1] : null;
        if (inner is { IsQuote: false } && inner.Ordered == ordered)
        {
            result[index] = p with { Containers = p.Containers.Take(p.Containers.Count - 1).ToList() };
            return new Result(result, index, offset);
        }
        if (inner is { IsQuote: false })
        {
            int listId = inner.ListId;
            for (int i = 0; i < result.Count; i++)
                result[i] = result[i] with { Containers = result[i].Containers.Select(c => !c.IsQuote && c.ListId == listId ? c with { Ordered = ordered } : c).ToList() };
            return new Result(result, index, offset);
        }
        var above = index > 0 ? paragraphs[index - 1].Containers : [];
        var join = above.Count == p.Containers.Count + 1 && above.Take(p.Containers.Count).SequenceEqual(p.Containers) && !above[^1].IsQuote && above[^1].Ordered == ordered ? above[^1] : null;
        var item = Container.Item(join?.ListId ?? NextId(paragraphs), NextId(paragraphs) + 1, ordered, join?.Start ?? 1);
        result[index] = p with { Containers = [.. p.Containers, item] };
        return new Result(result, index, offset);
    }

    public static Result ToggleQuote(List<FormatParagraph> paragraphs, int index, int offset)
    {
        var result = paragraphs.ToList();
        var p = result[index];
        int quote = p.Containers.ToList().FindLastIndex(c => c.IsQuote);
        if (quote >= 0) result[index] = p with { Containers = p.Containers.Where((_, i) => i != quote).ToList() };
        else
        {
            var above = index > 0 && paragraphs[index - 1].Containers is [{ IsQuote: true } outer, ..] ? outer : null;
            result[index] = p with { Containers = [above ?? Container.Quote(NextId(paragraphs)), .. p.Containers] };
        }
        return new Result(result, index, offset);
    }

    /// <summary>Text becomes a one-line code block; in a code block, every line becomes text.</summary>
    public static Result ToggleCode(List<FormatParagraph> paragraphs, int index, int offset)
    {
        var result = paragraphs.ToList();
        var p = result[index];
        if (p.Kind == BlockKind.Code)
        {
            for (int i = 0; i < result.Count; i++)
                if (result[i].Kind == BlockKind.Code && result[i].BlockId == p.BlockId)
                    result[i] = result[i] with { Kind = BlockKind.Paragraph, BlockId = 0, Info = "" };
        }
        else if (p.Kind is BlockKind.Paragraph or BlockKind.Heading)
            result[index] = p with { Kind = BlockKind.Code, BlockId = NextId(paragraphs), Runs = p.Text.Length == 0 ? [] : [new InlineRun(p.Text)] };
        return new Result(result, index, offset);
    }

    public static Result InsertTable(List<FormatParagraph> paragraphs, int index, int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, SourceFormatter.MaxTableColumns);
        rows = Math.Clamp(rows, 1, SourceFormatter.MaxTableRows);
        int id = NextId(paragraphs);
        var containers = paragraphs[index].Containers;
        var alignments = Enumerable.Repeat(TableAlignment.None, columns).ToArray();
        var cells = Enumerable.Range(0, rows).SelectMany(r => Enumerable.Range(0, columns).Select(c =>
            new FormatParagraph { Kind = BlockKind.TableCell, BlockId = id, Row = r, Column = c, Alignments = alignments, Containers = containers }));
        return InsertAfter(paragraphs, index, cells.ToList(), 0);
    }

    public static Result InsertRule(List<FormatParagraph> paragraphs, int index) =>
        InsertAfter(paragraphs, index, [new FormatParagraph { Kind = BlockKind.ThematicBreak, BlockId = NextId(paragraphs), Containers = paragraphs[index].Containers }], 1);

    /// <summary>Replaces an empty paragraph, or goes after the current one, with an editable paragraph following.</summary>
    private static Result InsertAfter(List<FormatParagraph> paragraphs, int index, List<FormatParagraph> block, int focusAfter)
    {
        var result = paragraphs.ToList();
        var p = result[index];
        int at = p.Kind == BlockKind.Paragraph && p.Runs.Count == 0 ? index : index + 1;
        if (at == index) result.RemoveAt(index);
        result.InsertRange(at, block);
        int after = at + block.Count;
        if (after >= result.Count || !result[after].IsEditable || result[after].Kind == BlockKind.TableCell)
            result.Insert(after, FormatParagraph.Plain("", p.Containers));
        return new Result(Ensure(result), focusAfter == 0 ? at : after, 0);
    }
}
