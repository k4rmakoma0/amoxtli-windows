namespace Amoxtli.Editing;

/// <summary>Replace <see cref="Length"/> UTF-16 units at <see cref="Start"/>, then select the given range.</summary>
public sealed record TextEdit(int Start, int Length, string Text, int SelectionStart, int SelectionLength)
{
    public string Apply(string source) => string.Concat(source.AsSpan(0, Start), Text, source.AsSpan(Start + Length));
    public int Delta => Text.Length - Length;
}

public static class TextDiff
{
    /// <summary>
    /// Smallest single replacement that turns <paramref name="before"/> into <paramref name="after"/>.
    /// The common prefix stops at <paramref name="caret"/>, so typing "a" after "a" is an insertion at the caret.
    /// Boundaries never split a surrogate pair.
    /// </summary>
    public static TextEdit Compute(string before, string after, int caret = int.MaxValue)
    {
        int limit = Math.Min(Math.Min(before.Length, after.Length), Math.Max(0, caret));
        int prefix = 0;
        while (prefix < limit && before[prefix] == after[prefix]) prefix++;
        if (prefix > 0 && prefix < after.Length && char.IsLowSurrogate(after[prefix])) prefix--;
        int suffix = 0, maxSuffix = Math.Min(before.Length, after.Length) - prefix;
        while (suffix < maxSuffix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        if (suffix > 0 && after.Length - suffix > 0 && char.IsLowSurrogate(after[after.Length - suffix])) suffix--;
        string text = after.Substring(prefix, after.Length - suffix - prefix);
        return new TextEdit(prefix, before.Length - suffix - prefix, text, prefix + text.Length, 0);
    }
}
