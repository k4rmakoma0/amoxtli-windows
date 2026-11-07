using Amoxtli.Controls;
using Amoxtli.Editing;
using Amoxtli.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Amoxtli;

// Editing: view modes, the source editor, formatting tools, saving and unsaved changes.
public sealed partial class MainWindow
{
    internal enum EditorMode { Source, Format, Split }
    private const string SourceFontFamily = "Segoe UI";
    private EditorMode _mode = EditorMode.Format;
    private SourceEditor _sourceEditor = null!;
    private DispatcherQueueTimer _editTimer = null!;
    private DispatcherQueueTimer _toolStateTimer = null!;
    private readonly Dictionary<(VirtualKey Key, VirtualKeyModifiers Modifiers), Action> _shortcuts = [];
    private readonly List<Action> _localizers = [];
    private readonly List<(MarkdownTool Tool, Action<bool> SetChecked)> _toolChecks = [];
    private readonly List<(int Level, RadioMenuFlyoutItem Item)> _styleItems = [];
    private int _analysisRevision;
    private bool _readerStale, _updatingTools, _closeConfirmed;

    private SourceEditor? ActiveEditor => _mode != EditorMode.Format && _document is not null ? _sourceEditor : null;

    private void InitializeEditing()
    {
        _sourceEditor = new SourceEditor();
        _sourceEditor.SetTypography(SourceFontFamily, _settings.FontSize);
        _sourceEditor.Edited += SourceEditor_Edited;
        _sourceEditor.SelectionMoved += ScheduleToolStates;
        _sourceEditor.Shortcut = TryShortcut;
        _sourceEditor.Box.ContextFlyout = BuildContextMenu();
        SourceHost.Child = _sourceEditor;
        _editTimer = DispatcherQueue.CreateTimer();
        _editTimer.Interval = TimeSpan.FromMilliseconds(200);
        _editTimer.IsRepeating = false;
        _editTimer.Tick += async (_, _) => await RefreshAfterEditAsync();
        _toolStateTimer = DispatcherQueue.CreateTimer();
        _toolStateTimer.Interval = TimeSpan.FromMilliseconds(50);
        _toolStateTimer.IsRepeating = false;
        _toolStateTimer.Tick += (_, _) => UpdateToolStates();
        BuildFormatMenu();
        BuildFormatBar();
        RegisterShortcuts();
        // Shortcuts pressed outside the editor still act on it, like the toolbar does.
        Root.KeyDown += (_, e) => { if (!e.Handled && TryShortcut(e.Key, SourceEditor.CurrentModifiers())) e.Handled = true; };
        UpdateModeControls();
    }

    private void RegisterShortcuts()
    {
        const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control, CtrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
        _shortcuts[(VirtualKey.B, Ctrl)] = () => RunTool(MarkdownTool.Bold);
        _shortcuts[(VirtualKey.I, Ctrl)] = () => RunTool(MarkdownTool.Italic);
        _shortcuts[(VirtualKey.U, Ctrl)] = () => RunTool(MarkdownTool.Underline);
        _shortcuts[(VirtualKey.X, CtrlShift)] = () => RunTool(MarkdownTool.Strikethrough);
        _shortcuts[(VirtualKey.E, Ctrl)] = () => RunTool(MarkdownTool.Code);
        _shortcuts[(VirtualKey.Number8, CtrlShift)] = () => RunTool(MarkdownTool.BulletList);
        _shortcuts[(VirtualKey.Number7, CtrlShift)] = () => RunTool(MarkdownTool.NumberedList);
        _shortcuts[(VirtualKey.K, Ctrl)] = () => RunTool(MarkdownTool.Link);
        _shortcuts[(VirtualKey.K, CtrlShift)] = () => RunTool(MarkdownTool.CodeBlock);
        // Ctrl+' uses whichever key types an apostrophe on the current keyboard layout.
        short apostrophe = VkKeyScanW('\'');
        if (apostrophe != -1)
            _shortcuts[((VirtualKey)(apostrophe & 0xFF), (apostrophe & 0x100) != 0 ? CtrlShift : Ctrl)] = () => RunTool(MarkdownTool.Quote);
        for (int level = 1; level <= 6; level++)
        {
            int heading = level;
            _shortcuts[(VirtualKey.Number0 + level, Ctrl)] = () => SetHeading(heading);
        }
        _shortcuts[(VirtualKey.Number7, Ctrl)] = () => SetHeading(0);
        _shortcuts[(VirtualKey.Number1, CtrlShift)] = () => SetMode(EditorMode.Source);
        _shortcuts[(VirtualKey.Number2, CtrlShift)] = () => SetMode(EditorMode.Format);
        _shortcuts[(VirtualKey.Number3, CtrlShift)] = () => SetMode(EditorMode.Split);
        _shortcuts[(VirtualKey.L, CtrlShift)] = () => SetSidebarOpen(!NavigationSplit.IsPaneOpen);
        _shortcuts[(VirtualKey.Z, Ctrl)] = () => ActiveEditor?.Undo();
        _shortcuts[(VirtualKey.Y, Ctrl)] = () => ActiveEditor?.Redo();
    }

    private bool TryShortcut(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (!_shortcuts.TryGetValue((key, modifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Menu)), out var action)) return false;
        action();
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char character);

    // Modes

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<EditorMode>(tag, out var mode)) SetMode(mode);
        else UpdateModeControls();
    }

    internal void SetMode(EditorMode mode)
    {
        if (mode == _mode) { UpdateModeControls(); return; }
        bool wasSource = _mode != EditorMode.Format;
        _mode = mode;
        bool source = mode != EditorMode.Format, reader = mode != EditorMode.Source;
        SourceColumn.Width = source ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ReaderColumn.Width = reader ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DividerColumn.Width = mode == EditorMode.Split ? GridLength.Auto : new GridLength(0);
        SourceHost.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
        ReaderScroll.Visibility = reader ? Visibility.Visible : Visibility.Collapsed;
        SplitDivider.Visibility = mode == EditorMode.Split ? Visibility.Visible : Visibility.Collapsed;
        // Closing the source editor releases its text and its undo steps.
        if (source && !wasSource) LoadEditor();
        else if (!source) _sourceEditor.Load("");
        if (reader && _readerStale) RenderCurrent(true);
        else if (!reader) { _renderer?.Dispose(); _renderer = null; DocumentPanel.Children.Clear(); _readerStale = true; _matches = []; }
        UpdateModeControls();
        UpdateToolStates();
        if (source) _sourceEditor.Box.Focus(FocusState.Programmatic);
    }

    private void UpdateModeControls()
    {
        ModeSourceButton.IsChecked = _mode == EditorMode.Source;
        ModeFormatButton.IsChecked = _mode == EditorMode.Format;
        ModeSplitButton.IsChecked = _mode == EditorMode.Split;
        ViewSourceMenu.IsChecked = _mode == EditorMode.Source;
        ViewFormatMenu.IsChecked = _mode == EditorMode.Format;
        ViewSplitMenu.IsChecked = _mode == EditorMode.Split;
    }

    private void LoadEditor(int caret = 0)
    {
        _sourceEditor.Load(_document?.Source ?? "", caret);
        ScheduleToolStates();
    }

    // Edits

    private void SourceEditor_Edited(TextEdit edit)
    {
        if (_document is null) return;
        bool wasDirty = _document.IsDirty;
        UpdateDocument(_document with { Source = _sourceEditor.Text });
        if (wasDirty != _document.IsDirty) UpdateDocumentInfo();
        _readerStale = true;
        _editTimer.Stop();
        _editTimer.Start();
    }

    private void UpdateDocument(ReadingDocument document)
    {
        _document = document;
        if (_historyIndex >= 0 && _historyIndex < _history.Count) _history[_historyIndex].Document = document;
    }

    /// <summary>Outline, statistics and the formatted view follow the text shortly after the last edit.</summary>
    private async Task RefreshAfterEditAsync()
    {
        if (_document is not { } document) return;
        int revision = ++_analysisRevision;
        var parsed = await Task.Run(() => MarkdownService.Parse(document.Source));
        if (revision != _analysisRevision || _closed || _document is null) return;
        if (_mode == EditorMode.Source) ShowAnalysis(parsed);
        else RenderCurrent(true, parsed);
    }

    private void ShowAnalysis(Markdig.Syntax.MarkdownDocument parsed)
    {
        OutlineList.ItemsSource = MarkdownService.GetHeadings(parsed);
        OutlineEmpty.Visibility = OutlineList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_document is null) return;
        int words = Regex.Count(_document.Source, @"\S+");
        StatusText.Text = Localization.Format("DocumentStatus", words, Math.Max(1, (int)Math.Ceiling(words / 220d)));
    }

    private void UpdateDocumentInfo()
    {
        if (_document is not { } document) return;
        string name = DisplayName(document);
        DocumentName.Text = document.IsDirty ? "• " + name : name;
        DocumentPath.Text = document.Path ?? Localization.Get("UnsavedFile");
        ToolTipService.SetToolTip(DocumentPath, DocumentPath.Text);
        Title = $"{DocumentName.Text} — Amoxtli";
    }

    private static string DisplayName(ReadingDocument document) => document.Kind switch
    {
        DocumentKind.Welcome => Localization.Get("GuideTitle"),
        DocumentKind.Clipboard => Localization.Get("ClipboardDocument"),
        _ => document.Name
    };

    // Formatting tools

    private async void RunTool(MarkdownTool tool)
    {
        if (_updatingTools || ActiveEditor is not { } editor) return;
        var (start, length) = editor.Selection;
        if (tool == MarkdownTool.Link && SourceFormatter.FindLink(editor.Text, start, length) is null)
        {
            await InsertLinkAsync(editor);
            return;
        }
        if (SourceFormatter.Toggle(editor.Text, start, length, tool, editor.Highlighter) is { } edit) editor.Apply(edit);
        FinishTool(editor);
    }

    private void SetHeading(int level)
    {
        if (_updatingTools || ActiveEditor is not { } editor) return;
        var (start, length) = editor.Selection;
        if (SourceFormatter.SetHeading(editor.Text, start, length, level) is { } edit) editor.Apply(edit);
        FinishTool(editor);
    }

    private void InsertThematicBreak()
    {
        if (ActiveEditor is not { } editor) return;
        var (start, length) = editor.Selection;
        editor.Apply(SourceFormatter.InsertThematicBreak(editor.Text, start, length));
        FinishTool(editor);
    }

    private async Task InsertLinkAsync(SourceEditor editor)
    {
        if (_dialogOpen) return;
        var (start, length) = editor.Selection;
        var dialog = new InsertLinkDialog(editor.Text.Substring(start, length).Split('\n')[0]) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, FlowDirection = Root.FlowDirection };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || SourceFormatter.NormalizeAddress(dialog.Address.Text) is not { } address) return;
            (start, length) = editor.Selection;
            editor.Apply(SourceFormatter.InsertLink(editor.Text, start, length, dialog.LinkText.Text, address));
        }
        finally { _dialogOpen = false; FinishTool(editor); }
    }

    private async Task InsertTableAsync()
    {
        if (_dialogOpen || ActiveEditor is not { } editor) return;
        var dialog = new InsertTableDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, FlowDirection = Root.FlowDirection };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !dialog.IsValid) return;
            var (start, length) = editor.Selection;
            editor.Apply(SourceFormatter.InsertTable(editor.Text, start, length, (int)dialog.Columns.Value, (int)dialog.Rows.Value));
        }
        finally { _dialogOpen = false; FinishTool(editor); }
    }

    private void FinishTool(SourceEditor editor)
    {
        editor.Box.Focus(FocusState.Programmatic);
        UpdateToolStates();
    }

    private void ScheduleToolStates()
    {
        _toolStateTimer.Stop();
        _toolStateTimer.Start();
    }

    /// <summary>Pressed buttons and menu checkmarks show what applies at the caret or across the selection.</summary>
    private void UpdateToolStates()
    {
        var editor = ActiveEditor;
        FormatBar.IsEnabled = editor is not null;
        foreach (var item in FormatMenu.Items) item.IsEnabled = editor is not null;
        var state = FormatState.None;
        if (editor is not null)
        {
            var (start, length) = editor.Selection;
            state = SourceFormatter.GetState(editor.Text, start, length, editor.Highlighter);
        }
        _updatingTools = true;
        try
        {
            foreach (var (tool, setChecked) in _toolChecks) setChecked(state.Has(tool));
            foreach (var (level, item) in _styleItems) item.IsChecked = editor is not null && state.HeadingLevel == level;
        }
        finally { _updatingTools = false; }
    }

    private sealed record ToolInfo(MarkdownTool? Tool, string Key, string Glyph, string? Shortcut, Action Run, string? GlyphFont = null);

    private List<List<ToolInfo>> ToolGroups() =>
    [
        [
            new(MarkdownTool.Bold, "Bold", "", "Ctrl+B", () => RunTool(MarkdownTool.Bold)),
            new(MarkdownTool.Italic, "Italic", "", "Ctrl+I", () => RunTool(MarkdownTool.Italic)),
            new(MarkdownTool.Underline, "Underline", "", "Ctrl+U", () => RunTool(MarkdownTool.Underline)),
            new(MarkdownTool.Strikethrough, "Strikethrough", "", "Ctrl+Shift+X", () => RunTool(MarkdownTool.Strikethrough)),
            new(MarkdownTool.Code, "InlineCode", "</>", "Ctrl+E", () => RunTool(MarkdownTool.Code), "Cascadia Mono")
        ],
        [
            new(MarkdownTool.Quote, "Quote", "", "Ctrl+'", () => RunTool(MarkdownTool.Quote)),
            new(MarkdownTool.BulletList, "BulletedList", "", "Ctrl+Shift+8", () => RunTool(MarkdownTool.BulletList)),
            new(MarkdownTool.NumberedList, "NumberedList", "1.", "Ctrl+Shift+7", () => RunTool(MarkdownTool.NumberedList), "Segoe UI")
        ],
        [
            new(MarkdownTool.Link, "Link", "", "Ctrl+K", () => RunTool(MarkdownTool.Link)),
            new(MarkdownTool.CodeBlock, "CodeBlock", "", "Ctrl+Shift+K", () => RunTool(MarkdownTool.CodeBlock)),
            new(null, "InsertTable", "", null, async () => await InsertTableAsync()),
            new(null, "HorizontalRule", "", null, InsertThematicBreak)
        ]
    ];

    private static string Tip(string key, string? shortcut) => shortcut is null ? Localization.Get(key) : $"{Localization.Get(key)} ({Localization.Shortcut(shortcut)})";

    private static IconElement Icon(ToolInfo info) => info.GlyphFont is null
        ? new FontIcon { Glyph = info.Glyph }
        : new FontIcon { Glyph = info.Glyph, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(info.GlyphFont), FontSize = 13 };

    private void BuildFormatBar()
    {
        var style = new AppBarButton { Icon = new FontIcon { Glyph = "" }, Flyout = new MenuFlyout() };
        foreach (var item in StyleItems()) ((MenuFlyout)style.Flyout).Items.Add(item);
        _localizers.Add(() => { style.Label = Localization.Get("Style"); ToolTipService.SetToolTip(style, style.Label); });
        FormatBar.PrimaryCommands.Add(style);
        foreach (var group in ToolGroups())
        {
            FormatBar.PrimaryCommands.Add(new AppBarSeparator());
            foreach (var info in group)
            {
                ICommandBarElement element;
                if (info.Tool is { } tool)
                {
                    var button = new AppBarToggleButton { Icon = Icon(info) };
                    button.Click += (_, _) => { if (!_updatingTools) info.Run(); };
                    _toolChecks.Add((tool, value => button.IsChecked = value));
                    _localizers.Add(() => { button.Label = Localization.Get(info.Key); ToolTipService.SetToolTip(button, Tip(info.Key, info.Shortcut)); });
                    element = button;
                }
                else
                {
                    var button = new AppBarButton { Icon = Icon(info) };
                    button.Click += (_, _) => info.Run();
                    _localizers.Add(() => { button.Label = Localization.Get(info.Key); ToolTipService.SetToolTip(button, Tip(info.Key, info.Shortcut)); });
                    element = button;
                }
                FormatBar.PrimaryCommands.Add(element);
            }
        }
    }

    private void BuildFormatMenu()
    {
        foreach (var item in FormatItems()) FormatMenu.Items.Add(item);
    }

    private IEnumerable<MenuFlyoutItemBase> StyleItems()
    {
        string group = "ParagraphStyle" + _styleItems.Count;
        for (int level = 0; level <= 6; level++)
        {
            int heading = level;
            var item = new RadioMenuFlyoutItem { GroupName = group };
            item.Click += (_, _) => { if (!_updatingTools) SetHeading(heading); };
            _styleItems.Add((heading, item));
            _localizers.Add(() =>
            {
                item.Text = heading == 0 ? Localization.Get("BodyText") : Localization.Format("HeadingLevel", heading);
                item.KeyboardAcceleratorTextOverride = Localization.Shortcut($"Ctrl+{(heading == 0 ? 7 : heading)}");
            });
            yield return item;
        }
    }

    /// <summary>The Format menu, also used as a submenu of the editor's context menu.</summary>
    private IEnumerable<MenuFlyoutItemBase> FormatItems()
    {
        var style = new MenuFlyoutSubItem();
        foreach (var item in StyleItems()) style.Items.Add(item);
        _localizers.Add(() => style.Text = Localization.Get("Style"));
        yield return style;
        foreach (var group in ToolGroups())
        {
            yield return new MenuFlyoutSeparator();
            foreach (var info in group)
            {
                MenuFlyoutItem item;
                if (info.Tool is { } tool)
                {
                    var toggle = new ToggleMenuFlyoutItem();
                    _toolChecks.Add((tool, value => toggle.IsChecked = value));
                    item = toggle;
                }
                else item = new MenuFlyoutItem();
                item.Click += (_, _) => { if (!_updatingTools) info.Run(); };
                bool dialog = info.Key is "InsertTable" || info.Tool == MarkdownTool.Link;
                _localizers.Add(() =>
                {
                    item.Text = Localization.Get(info.Key) + (dialog ? "…" : "");
                    item.KeyboardAcceleratorTextOverride = info.Shortcut is null ? "" : Localization.Shortcut(info.Shortcut);
                });
                yield return item;
            }
        }
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Command(string key, string shortcut, Action action)
        {
            var item = new MenuFlyoutItem();
            item.Click += (_, _) => action();
            _localizers.Add(() => { item.Text = Localization.Get(key); item.KeyboardAcceleratorTextOverride = Localization.Shortcut(shortcut); });
            menu.Items.Add(item);
            return item;
        }
        var cut = Command("Cut", "Ctrl+X", () => _sourceEditor.Cut());
        var copy = Command("Copy", "Ctrl+C", () => _sourceEditor.Copy());
        Command("Paste", "Ctrl+V", () => _sourceEditor.Paste());
        var format = new MenuFlyoutSubItem();
        foreach (var item in FormatItems()) format.Items.Add(item);
        _localizers.Add(() => format.Text = Localization.Get("MenuFormat"));
        menu.Items.Add(format);
        menu.Items.Add(new MenuFlyoutSeparator());
        Command("SelectAll", "Ctrl+A", () => _sourceEditor.SelectAll());
        menu.Opening += (_, _) =>
        {
            cut.IsEnabled = copy.IsEnabled = _sourceEditor.Selection.Length > 0;
            UpdateToolStates();
        };
        return menu;
    }

    private void RefreshEditingTexts()
    {
        foreach (var localize in _localizers) localize();
        foreach (var (item, keys) in new (MenuFlyoutItem, string)[] { (UndoMenu, "Ctrl+Z"), (RedoMenu, "Ctrl+Y"), (SidebarMenu, "Ctrl+Shift+L"),
            (ViewSourceMenu, "Ctrl+Shift+1"), (ViewFormatMenu, "Ctrl+Shift+2"), (ViewSplitMenu, "Ctrl+Shift+3") })
            item.KeyboardAcceleratorTextOverride = Localization.Shortcut(keys);
        UpdateDocumentInfo();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => ActiveEditor?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => ActiveEditor?.Redo();

    // Saving

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);

    internal async Task<bool> SaveAsync(bool saveAs, string? targetPath = null)
    {
        if (_document is not { } document) return false;
        string? path = targetPath ?? (document.Kind == DocumentKind.File && !saveAs ? document.Path : null);
        if (path is null)
        {
            try
            {
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = document.Kind == DocumentKind.File ? System.IO.Path.GetFileNameWithoutExtension(document.Name) : Localization.Get("Untitled") };
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                picker.FileTypeChoices.Add("Markdown", MarkdownService.Extensions.ToList());
                path = (await picker.PickSaveFileAsync())?.Path;
            }
            catch (Exception ex) { await ShowErrorAsync(Localization.Get("PickerError"), ex.Message); return false; }
            if (path is null) return false;
        }
        var format = document.Format ?? FileFormat.Default;
        string text = document.Source;
        try { await Task.Run(() => DocumentFile.Write(path, text, format)); }
        catch (Exception ex)
        {
            Notify(Localization.Format("SaveError", System.IO.Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
            return false;
        }
        if (_document is not { } current || _closed || current.Path != document.Path || current.Kind != document.Kind) return true;
        bool moved = !string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase) || current.Kind != DocumentKind.File;
        // Typing during the write leaves the newer text unsaved.
        UpdateDocument(current with { Saved = text, Path = path, Name = System.IO.Path.GetFileName(path), Kind = DocumentKind.File, Format = format });
        UpdateDocumentInfo();
        if (moved) WatchCurrentFile();
        return true;
    }

    /// <summary>Asks to save unsaved changes before leaving the document. False means stay.</summary>
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (_document is not { IsDirty: true } document) return true;
        if (_dialogOpen) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Localization.Format("UnsavedTitle", DisplayName(document)),
            Content = Localization.Get("UnsavedText"),
            PrimaryButtonText = Localization.Get("Save"),
            SecondaryButtonText = Localization.Get("DontSave"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        _dialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (result == ContentDialogResult.Primary) return await SaveAsync(false);
        if (result != ContentDialogResult.Secondary) return false;
        if (_document is { } current) UpdateDocument(current with { Source = current.Saved });
        return true;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || _document is not { IsDirty: true }) return;
        args.Cancel = true;
        if (await ConfirmLeaveAsync()) { _closeConfirmed = true; Close(); }
    }
}
