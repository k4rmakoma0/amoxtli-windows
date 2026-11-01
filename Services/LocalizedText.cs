using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;

namespace Amoxtli.Services;

/// <summary>Localizes XAML elements, including menus that are not in the visual tree.</summary>
public sealed class LocalizedText : DependencyObject
{
    private static readonly List<WeakReference<DependencyObject>> Targets = [];
    private static readonly ConditionalWeakTable<DependencyObject, object> Registered = new();
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LocalizedText), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
        "ToolTip", typeof(string), typeof(LocalizedText), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty NameProperty = DependencyProperty.RegisterAttached(
        "Name", typeof(string), typeof(LocalizedText), new PropertyMetadata(null, Changed));
    public static string? GetText(DependencyObject target) => (string?)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string? GetToolTip(DependencyObject target) => (string?)target.GetValue(ToolTipProperty);
    public static void SetToolTip(DependencyObject target, string value) => target.SetValue(ToolTipProperty, value);
    public static string? GetName(DependencyObject target) => (string?)target.GetValue(NameProperty);
    public static void SetName(DependencyObject target, string value) => target.SetValue(NameProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (!Registered.TryGetValue(target, out _))
        {
            Registered.Add(target, new object());
            Targets.Add(new WeakReference<DependencyObject>(target));
        }
        Apply(target);
    }

    internal static void Refresh()
    {
        for (int i = Targets.Count - 1; i >= 0; i--)
            if (Targets[i].TryGetTarget(out var target)) Apply(target);
            else Targets.RemoveAt(i);
    }

    private static void Apply(DependencyObject target)
    {
        if (GetText(target) is string key)
        {
            string text = Localization.Get(key);
            switch (target)
            {
                case TextBlock block: block.Text = text; break;
                case TextBox box: box.PlaceholderText = text; break;
                case MenuBarItem item: item.Title = text; break;
                case ToggleMenuFlyoutItem item: item.Text = text; break;
                case MenuFlyoutItem item: item.Text = text; break;
                case PivotItem item: item.Header = text; break;
                case ContentControl control: control.Content = text; break;
            }
        }
        if (GetToolTip(target) is string tip) ToolTipService.SetToolTip(target, Localization.Get(tip));
        if (GetName(target) is string name) AutomationProperties.SetName(target, Localization.Get(name));
    }
}
