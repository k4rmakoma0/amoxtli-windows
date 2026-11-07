using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Amoxtli.Controls;

/// <summary>
/// Colors for rich text, which has no alpha: the surface opacities of the port document
/// (section 11) are blended onto the reading surface.
/// </summary>
internal readonly record struct EditorPalette(Color Text, Color Syntax, Color Accent, Color CodeBackground)
{
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
            return new EditorPalette(text, text, settings.UIElementColor(UIElementType.Hotlight), Blend(dark ? (byte)0 : (byte)255, dark ? (byte)255 : (byte)0, 0.16));
        }
        return dark
            ? new EditorPalette(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 154, 154, 154), accent, Blend(43, 255, 0.13))
            : new EditorPalette(Color.FromArgb(255, 26, 26, 26), Color.FromArgb(255, 122, 122, 122), accent, Blend(250, 0, 0.075));
    }
}
