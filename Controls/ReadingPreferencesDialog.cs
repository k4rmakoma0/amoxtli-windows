using Amoxtli.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace Amoxtli.Controls;

internal sealed class ReadingPreferencesDialog : ContentDialog
{
    internal ComboBox LanguagePicker { get; }
    internal ComboBox ThemePicker { get; }
    internal ToggleSwitch RemoteImages { get; }
    internal ComboBox TypefacePicker { get; }
    internal Slider TextSize { get; }
    internal CheckBox DefaultAppPrompt { get; }
    private readonly TextBlock _remoteImagesHelp = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 400, FontSize = 12 };
    private readonly TextBlock _defaultAppText = new() { TextWrapping = TextWrapping.Wrap };
    private bool _refreshing;

    internal ReadingPreferencesDialog(ReaderSettings settings)
    {
        var languages = GetLanguageOptions(Localization.CurrentLanguage);
        LanguagePicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FlowDirection = FlowDirection.LeftToRight,
            ItemsSource = languages,
            DisplayMemberPath = nameof(LanguageOption.NativeName),
            SelectedItem = languages.FirstOrDefault(item => item.Code == settings.Language) ?? languages[0]
        };
        ThemePicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { Localization.Get("SystemTheme"), Localization.Get("Light"), Localization.Get("Dark") },
            SelectedIndex = settings.Theme == "Light" ? 1 : settings.Theme == "Dark" ? 2 : 0
        };
        RemoteImages = new ToggleSwitch { IsOn = settings.RemoteImages };
        TypefacePicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = Array.IndexOf(Typesetting.Typefaces, Typesetting.NormalizeTypeface(settings.Typeface)) };
        TextSize = new Slider { Minimum = Typesetting.Sizes[0], Maximum = Typesetting.Sizes[^1], StepFrequency = 1, Value = Typesetting.Snap(settings.FontSize) };
        // The slider moves along the size steps, not every pixel.
        TextSize.ValueChanged += (_, args) => { double step = Typesetting.Snap(args.NewValue); if (step != args.NewValue) TextSize.Value = step; };
        DefaultAppPrompt = new CheckBox { Content = _defaultAppText, IsChecked = settings.PromptForDefaultApp };
        var panel = new StackPanel { Spacing = 20, MinWidth = 320 };
        panel.Children.Add(LanguagePicker);
        panel.Children.Add(RemoteImages);
        panel.Children.Add(_remoteImagesHelp);
        panel.Children.Add(ThemePicker);
        panel.Children.Add(TypefacePicker);
        panel.Children.Add(TextSize);
        panel.Children.Add(DefaultAppPrompt);
        Content = panel;
        DefaultButton = ContentDialogButton.Primary;
        RefreshLanguage();
        LanguagePicker.SelectionChanged += (_, _) => RefreshLanguage();
    }

    private static List<LanguageOption> GetLanguageOptions(string language) =>
        new[] { new LanguageOption("auto", Localization.Get("SystemLanguage", language)) }.Concat(Localization.Languages).ToList();

    private string SelectedLanguage => (LanguagePicker.SelectedItem as LanguageOption)?.Code ?? "auto";

    private void RefreshLanguage()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            string selection = SelectedLanguage;
            string language = Localization.ResolveLanguage(selection);
            string Text(string key) => Localization.Get(key, language);
            var culture = CultureInfo.GetCultureInfo(language);
            Language = culture.Name;
            FlowDirection = culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Title = Text("Preferences");
            PrimaryButtonText = Text("Save");
            CloseButtonText = Text("Cancel");
            LanguagePicker.Header = Text("Language");
            var languages = GetLanguageOptions(language);
            LanguagePicker.ItemsSource = languages;
            LanguagePicker.SelectedItem = languages.First(item => item.Code == selection);
            int themeIndex = ThemePicker.SelectedIndex;
            ThemePicker.Header = Text("Theme");
            ThemePicker.ItemsSource = new[] { Text("SystemTheme"), Text("Light"), Text("Dark") };
            ThemePicker.SelectedIndex = themeIndex;
            RemoteImages.Header = Text("RemoteImages");
            RemoteImages.OnContent = Text("On");
            RemoteImages.OffContent = Text("Off");
            _remoteImagesHelp.Text = Text("RemoteImagesHelp");
            int typeface = TypefacePicker.SelectedIndex;
            TypefacePicker.Header = Text("Typeface");
            TypefacePicker.ItemsSource = Typesetting.Typefaces.Select(item => Text(Typesetting.LabelKey(item))).ToArray();
            TypefacePicker.SelectedIndex = typeface;
            TextSize.Header = Text("FontSize");
            _defaultAppText.Text = Text("DefaultAppPrompt");
        }
        finally { _refreshing = false; }
    }

    internal void ApplyTo(ReaderSettings settings)
    {
        settings.Language = SelectedLanguage;
        settings.Theme = ThemePicker.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Default" };
        settings.RemoteImages = RemoteImages.IsOn;
        settings.FontSize = Typesetting.Snap(TextSize.Value);
        settings.Typeface = Typesetting.Typefaces[Math.Max(0, TypefacePicker.SelectedIndex)];
        settings.PromptForDefaultApp = DefaultAppPrompt.IsChecked == true;
    }
}
