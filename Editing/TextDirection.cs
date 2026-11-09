using System.Text;
using System.Text.RegularExpressions;

namespace Amoxtli.Editing;

/// <summary>
/// The base direction of a paragraph comes from its first strong letter, as in the Unicode
/// bidirectional algorithm (rules P2 and P3): Arabic or Hebrew makes it right to left, any other
/// letter left to right. Digits, punctuation and Markdown syntax do not decide.
/// </summary>
public static partial class TextDirection
{
    /// <summary>True for right to left, false for left to right, null when the text has no letters.</summary>
    public static bool? FirstStrong(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            return IsRightToLeftLetter(rune.Value);
        }
        return null;
    }

    public static bool IsRightToLeft(string text) => FirstStrong(text) == true;

    /// <summary>
    /// The language a right-to-left paragraph is tagged with, from the script of its first letter.
    /// Rich edit resolves spaces and punctuation between scripts by the language of the text, so
    /// untagged Arabic glues the space before a Latin word at the end of the paragraph to that word.
    /// </summary>
    public static string LanguageTag(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            return rune.Value is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F ? "he" : "ar";
        }
        return "ar";
    }

    /// <summary>
    /// A line of Markdown source: quote, list and task markers come before the text and are skipped,
    /// so the "x" of a checked task does not decide.
    /// </summary>
    public static bool IsRightToLeftLine(string line) => FirstStrongInLine(line) == true;

    /// <summary>Like <see cref="FirstStrong"/> for a line of Markdown source, after its quote, list and task markers.</summary>
    public static bool? FirstStrongInLine(string line) => FirstStrong(LinePrefix().Replace(line, "", 1));

    // Hebrew, Arabic, Syriac, Thaana, N'Ko and the other scripts of U+0590–U+08FF, their presentation
    // forms, and the right-to-left blocks of the supplementary planes.
    private static bool IsRightToLeftLetter(int value) => value is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF
        or >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EFFF;

    [GeneratedRegex(@"^[ \t]*(?:>[ \t]?)*(?:(?:[-*+]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)?")]
    private static partial Regex LinePrefix();
}
