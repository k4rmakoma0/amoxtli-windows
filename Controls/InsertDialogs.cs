using Amoxtli.Editing;
using Amoxtli.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amoxtli.Controls;

internal sealed class InsertLinkDialog : ContentDialog
{
    internal TextBox LinkText { get; }
    internal TextBox Address { get; }

    internal InsertLinkDialog(string text)
    {
        Title = Localization.Get("InsertLinkTitle");
        LinkText = new TextBox { Header = Localization.Get("LinkText"), PlaceholderText = Localization.Get("LinkTextPlaceholder"), Text = text };
        Address = new TextBox { Header = Localization.Get("LinkAddress"), PlaceholderText = "https://", FlowDirection = FlowDirection.LeftToRight, InputScope = new() { Names = { new(Microsoft.UI.Xaml.Input.InputScopeNameValue.Url) } } };
        var panel = new StackPanel { Spacing = 16, MinWidth = 340 };
        panel.Children.Add(LinkText);
        panel.Children.Add(Address);
        Content = panel;
        PrimaryButtonText = Localization.Get("Insert");
        CloseButtonText = Localization.Get("Cancel");
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;
        Address.TextChanged += (_, _) => IsPrimaryButtonEnabled = SourceFormatter.NormalizeAddress(Address.Text) is not null;
        Opened += (_, _) => (string.IsNullOrEmpty(text) ? LinkText : Address).Focus(FocusState.Programmatic);
    }
}

internal sealed class InsertTableDialog : ContentDialog
{
    internal NumberBox Columns { get; }
    internal NumberBox Rows { get; }
    private readonly TextBlock _limits = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    internal InsertTableDialog()
    {
        Title = Localization.Get("InsertTableTitle");
        Columns = new NumberBox { Header = Localization.Get("Columns"), Value = 3, ValidationMode = NumberBoxValidationMode.Disabled, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        Rows = new NumberBox { Header = Localization.Get("Rows"), Value = 3, ValidationMode = NumberBoxValidationMode.Disabled, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        _limits.Text = Localization.Get("TableLimits");
        _limits.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        sizes.Children.Add(Columns);
        sizes.Children.Add(Rows);
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
        panel.Children.Add(sizes);
        panel.Children.Add(new TextBlock { Text = Localization.Get("TableHeaderNote"), TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        panel.Children.Add(_limits);
        Content = panel;
        PrimaryButtonText = Localization.Get("Insert");
        CloseButtonText = Localization.Get("Cancel");
        DefaultButton = ContentDialogButton.Primary;
        // Without NumberBox validation, an out-of-range size is explained instead of silently changed.
        Columns.ValueChanged += (_, _) => Validate();
        Rows.ValueChanged += (_, _) => Validate();
    }

    internal bool IsValid => Valid(Columns.Value, SourceFormatter.MaxTableColumns) && Valid(Rows.Value, SourceFormatter.MaxTableRows);

    private static bool Valid(double value, int maximum) => double.IsFinite(value) && value == Math.Floor(value) && value >= 1 && value <= maximum;

    private void Validate()
    {
        IsPrimaryButtonEnabled = IsValid;
        _limits.Visibility = IsValid ? Visibility.Collapsed : Visibility.Visible;
    }
}
