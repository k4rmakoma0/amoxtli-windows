namespace Amoxtli.Services;

/// <summary>
/// The curated typefaces and size steps shared by both editors. Windows has no rounded system
/// typeface, so the macOS "Rounded" choice is left out. Sizes are in effective pixels.
/// </summary>
public static class Typesetting
{
    public const string System = "System", Serif = "Serif", Monospace = "Monospace";
    public static readonly string[] Typefaces = [System, Serif, Monospace];
    public static readonly double[] Sizes = [10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24, 28, 32];
    public const double DefaultSize = 16;

    public static string NormalizeTypeface(string? typeface) => Typefaces.Contains(typeface) ? typeface! : System;

    /// <summary>The nearest step; a saved size from an older version lands on the scale.</summary>
    public static double Snap(double size) => double.IsFinite(size) ? Sizes.MinBy(step => Math.Abs(step - size)) : DefaultSize;

    public static double Step(double size, int steps) => Sizes[Math.Clamp(Array.IndexOf(Sizes, Snap(size)) + steps, 0, Sizes.Length - 1)];

    /// <summary>A XAML family with fallbacks: Segoe UI Variable and Cascadia ship with Windows 11 only.</summary>
    public static string FontFamily(string typeface) => NormalizeTypeface(typeface) switch
    {
        Serif => "Georgia",
        Monospace => "Cascadia Mono, Consolas",
        _ => "Segoe UI Variable Text, Segoe UI"
    };

    public static string LabelKey(string typeface) => "Typeface" + NormalizeTypeface(typeface);
}
