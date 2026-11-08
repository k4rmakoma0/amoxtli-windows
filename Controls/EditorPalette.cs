using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Amoxtli.Controls;

/// <summary>
/// Colors for rich text, which has no alpha: the surface opacities of the port document
/// (section 11) are blended onto the reading surface.
/// </summary>
internal readonly record struct EditorPalette(Color Text, Color Syntax, Color Accent, Color CodeBackground, bool Dark = false, bool HighContrast = false)
{
    /// <summary>
    /// Code token colors, light and dark, close to Xcode's default theme (port document, section 6.3): saturated
    /// system colors looked alike or did not read on white. High contrast keeps the text color.
    /// </summary>
    public Color Token(Amoxtli.Editing.TokenKind kind)
    {
        if (HighContrast) return Text;
        if (kind == Amoxtli.Editing.TokenKind.Comment) return Syntax;
        uint rgb = (kind, Dark) switch
        {
            (Amoxtli.Editing.TokenKind.Keyword, false) => 0x9B2393, (Amoxtli.Editing.TokenKind.Keyword, true) => 0xFF7AB2,
            (Amoxtli.Editing.TokenKind.Type, false) => 0x3900A0, (Amoxtli.Editing.TokenKind.Type, true) => 0xDABAFF,
            (Amoxtli.Editing.TokenKind.String, false) => 0xC41A16, (Amoxtli.Editing.TokenKind.String, true) => 0xFF8170,
            (Amoxtli.Editing.TokenKind.Number, false) => 0x1C00CF, (Amoxtli.Editing.TokenKind.Number, true) => 0xD9C97C,
            (Amoxtli.Editing.TokenKind.Property, false) => 0x326D74, (Amoxtli.Editing.TokenKind.Property, true) => 0x67B7A4,
            (Amoxtli.Editing.TokenKind.Tag, false) => 0x0B4F79, (Amoxtli.Editing.TokenKind.Tag, true) => 0x5DD8FF,
            (_, false) => 0x815F03, _ => 0xCC9768
        };
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>
    /// A surface for XAML elements, which do have alpha: black over light, white over dark, and the high contrast
    /// value in the high contrast themes (port document, section 11).
    /// </summary>
    public static Color Surface(ElementTheme theme, double light, double dark, double contrast)
    {
        bool isDark = theme == ElementTheme.Dark;
        double alpha = new AccessibilitySettings().HighContrast ? contrast : isDark ? dark : light;
        byte ink = isDark ? (byte)255 : (byte)0;
        return Color.FromArgb((byte)Math.Round(alpha * 255), ink, ink, ink);
    }

    public static Color CodeSurface(ElementTheme theme) => Surface(theme, 0.075, 0.13, 0.16);
    public static Color TableHeaderSurface(ElementTheme theme) => Surface(theme, 0.05, 0.08, 0.12);
    public static Color TableLineSurface(ElementTheme theme) => Surface(theme, 0.2, 0.26, 0.5);

    public static EditorPalette For(ElementTheme theme)
    {
        bool dark = theme == ElementTheme.Dark;
        var settings = new UISettings();
        var accent = settings.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        static Color Blend(byte surface, byte ink, double alpha)
        {
            byte value = (byte)Math.Round(surface + (ink - surface) * alpha);
            return Color.FromArgb(255, value, value, value);
        }
        if (new AccessibilitySettings().HighContrast)
        {
            var text = settings.UIElementColor(UIElementType.WindowText);
            return new EditorPalette(text, text, settings.UIElementColor(UIElementType.Hotlight), Blend(dark ? (byte)0 : (byte)255, dark ? (byte)255 : (byte)0, 0.16), dark, true);
        }
        return dark
            ? new EditorPalette(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 154, 154, 154), accent, Blend(43, 255, 0.13), true)
            : new EditorPalette(Color.FromArgb(255, 26, 26, 26), Color.FromArgb(255, 122, 122, 122), accent, Blend(250, 0, 0.075));
    }
}
