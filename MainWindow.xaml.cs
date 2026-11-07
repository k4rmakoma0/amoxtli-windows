using Amoxtli.Models;
using Amoxtli.Controls;
using Amoxtli.Rendering;
using Amoxtli.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using FileAttributes = System.IO.FileAttributes;

namespace Amoxtli;

public sealed partial class MainWindow : Window
{
    private enum DocumentKind { File, Welcome, Clipboard }
    /// <summary>A document in history. <see cref="Saved"/> is the text last read or written; any other <see cref="Source"/> is unsaved.</summary>
    private sealed record ReadingDocument
    {
        public ReadingDocument(string source, string? path, string name, DocumentKind kind = DocumentKind.File, FileFormat? format = null)
        {
            Source = Saved = DocumentFile.NormalizeLineEndings(source);
            Path = path; Name = name; Kind = kind; Format = format;
        }
        public string Source { get; init; }
        public string Saved { get; init; }
        public string? Path { get; init; }
        public string Name { get; init; }
        public DocumentKind Kind { get; init; }
        public FileFormat? Format { get; init; }
        public bool IsDirty => !ReferenceEquals(Source, Saved) && Source != Saved;
    }
    private sealed class HistoryEntry(ReadingDocument document)
    {
        public ReadingDocument Document { get; set; } = document;
        public double Offset { get; set; }
    }

    private readonly TaskCompletionSource _ready = new();
    public Task Ready => _ready.Task;
    private readonly ReaderSettings _settings = ReaderSettings.Load();
    private readonly List<HistoryEntry> _history = [];
    private int _historyIndex = -1;
    private int _openRevision;
    private int _folderRevision;
    private string? _folderPath;
    private ReadingDocument? _document;
    private NativeMarkdownRenderer? _renderer;
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _reloadTimer;
    private List<FrameworkElement> _matches = [];
    private int _matchIndex = -1;
    private bool _dialogOpen;
    private bool _closed;
    private bool _rendering;

    public MainWindow()
    {
        InitializeComponent();
        InitializeEditing();
        ApplyLanguage();
        ReaderShadow.Receivers.Add(WorkspaceShadowReceiver);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Amoxtli.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1220, 860));
        ApplyTheme();
        NavigationSplit.RegisterPropertyChangedCallback(SplitView.IsPaneOpenProperty, (_, _) => UpdateSidebarControls());
        NavigationSplit.IsPaneOpen = _settings.NavigationOpen;
        UpdateSidebarControls();
        Root.Loaded += Root_Loaded;
        Root.ActualThemeChanged += (_, _) => { UpdateTitleBarTheme(); if (_document is not null && !_rendering && !_closed) RenderCurrent(true); };
        Root.SizeChanged += (_, args) =>
        {
            DocumentPanel.Padding = new Thickness(args.NewSize.Width < 800 ? 24 : 40, 32, args.NewSize.Width < 800 ? 24 : 40, 64);
        };
        Closed += (_, _) => { _closed = true; _watcher?.Dispose(); _reloadTimer?.Stop(); _renderer?.Dispose(); };
        AppWindow.Closing += AppWindow_Closing;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
        UpdateTitleBarTheme();
        try { await ShowWelcomeAsync(); }
        catch (Exception ex) { Notify(ex.Message, InfoBarSeverity.Error); }
        finally { _ready.TrySetResult(); }
    }

    private void ApplyTheme() => Root.RequestedTheme = _settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    private void ApplyLanguage()
    {
        Localization.SetLanguage(_settings.Language);
        Root.Language = Localization.Culture.Name;
        Root.FlowDirection = Localization.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        NavigationPivot.FlowDirection = FindBar.FlowDirection = Root.FlowDirection;
        NavigationSplit.PanePlacement = Localization.IsRightToLeft ? SplitViewPanePlacement.Right : SplitViewPanePlacement.Left;
        // App language never changes a user's Markdown or source-code direction.
        DocumentPanel.FlowDirection = SourceHost.FlowDirection = FlowDirection.LeftToRight;
        if (_folderPath is not null)
        {
            FolderName.Text = System.IO.Path.GetFileName(_folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(FolderName.Text)) FolderName.Text = _folderPath;
        }
        foreach (var entry in _history)
            if (entry.Document.Kind == DocumentKind.Welcome && !entry.Document.IsDirty)
                entry.Document = new ReadingDocument(Localization.WelcomeMarkdown(), null, "", DocumentKind.Welcome);
        if (_historyIndex >= 0 && _document != _history[_historyIndex].Document)
        {
            _document = _history[_historyIndex].Document;
            if (_mode != EditorMode.Format) LoadEditor();
        }
        Notice.IsOpen = false;
        UpdateSidebarControls();
        RefreshEditingTexts();
        RenderCurrent(true);
        UpdateProgress();
    }
    private void UpdateTitleBarTheme() => AppWindow.TitleBar.PreferredTheme = Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Notify(Localization.Format("SaveSettingsError", ex.Message), InfoBarSeverity.Warning); }
    }

    public async Task OpenFileAsync(string path)
    {
        int revision = ++_openRevision;
        try
        {
            path = System.IO.Path.GetFullPath(path);
            if (!MarkdownService.IsMarkdown(path)) throw new InvalidOperationException(Localization.Get("ChooseMarkdown"));
            // Opening the document already shown keeps its unsaved edits.
            if (_document?.Kind == DocumentKind.File && string.Equals(_document.Path, path, StringComparison.OrdinalIgnoreCase) && _document.IsDirty) return;
            var (source, format) = await DocumentFile.ReadAsync(path);
            var parsed = await Task.Run(() => MarkdownService.Parse(source));
            if (revision != _openRevision || _closed) return;
            if (!await ConfirmLeaveAsync() || revision != _openRevision) return;
            Navigate(new ReadingDocument(source, path, System.IO.Path.GetFileName(path), DocumentKind.File, format), parsed);
            if (_folderPath is null) await OpenFolderAsync(System.IO.Path.GetDirectoryName(path)!, false);
        }
        catch (Exception ex) { if (revision == _openRevision) await ShowErrorAsync(Localization.Get("OpenFileError"), ex.Message); }
    }

    public async Task OpenFolderAsync(string path, bool selectTab = true)
    {
        int revision = ++_folderRevision;
        try
        {
            path = System.IO.Path.GetFullPath(path);
            var entries = await Task.Run(() => ReadDirectory(path));
            if (revision != _folderRevision || _closed) return;
            _folderPath = path;
            FolderName.Text = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(FolderName.Text)) FolderName.Text = path;
            ToolTipService.SetToolTip(FolderName, path);
            FileTree.RootNodes.Clear();
            foreach (var item in entries) FileTree.RootNodes.Add(CreateNode(item));
            FolderPrompt.Visibility = Visibility.Collapsed;
            if (selectTab) { NavigationPivot.SelectedIndex = 0; NavigationSplit.IsPaneOpen = true; }
            if (entries.Count == 0) Notify(Localization.Get("EmptyFolder"));
        }
        catch (Exception ex) { if (revision == _folderRevision) await ShowErrorAsync(Localization.Get("OpenFolderError"), ex.Message); }
    }

    private static List<FileTreeItem> ReadDirectory(string path)
    {
        return new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
            .Where(i => i is DirectoryInfo || MarkdownService.IsMarkdown(i.FullName))
            .OrderByDescending(i => i is DirectoryInfo).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new FileTreeItem(i.FullName, i is DirectoryInfo)).ToList();
    }

    private static TreeViewNode CreateNode(FileTreeItem item) => new() { Content = item, HasUnrealizedChildren = item.IsDirectory };

    private async void FileTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;
        if (!node.HasUnrealizedChildren || node.Content is not FileTreeItem item) return;
        int revision = _folderRevision;
        try
        {
            var entries = await Task.Run(() => ReadDirectory(item.Path));
            if (revision != _folderRevision || _closed) return;
            node.Children.Clear();
            foreach (var entry in entries) node.Children.Add(CreateNode(entry));
            node.HasUnrealizedChildren = false;
        }
        catch (Exception ex) { Notify(Localization.Format("ReadFolderError", item.Name, ex.Message), InfoBarSeverity.Warning); }
    }

    private async void FileTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var node = args.InvokedItem as TreeViewNode;
        if (node?.Content is not FileTreeItem item) return;
        if (!item.IsDirectory) await OpenFileAsync(item.Path);
    }

    private void Navigate(ReadingDocument document, Markdig.Syntax.MarkdownDocument parsed)
    {
        SaveScrollPosition();
        if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(new HistoryEntry(document));
        if (_history.Count > 20) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
        _document = document;
        Notice.IsOpen = false;
        RenderCurrent(false, parsed);
        if (_mode != EditorMode.Format) LoadEditor();
        WatchCurrentFile();
    }

    private void RenderCurrent(bool preserveOffset, Markdig.Syntax.MarkdownDocument? parsed = null)
    {
        if (_document is null || _closed) return;
        _rendering = true;
        try
        {
            var document = _document;
            double offset = preserveOffset ? ReaderScroll.VerticalOffset : 0;
            parsed ??= MarkdownService.Parse(document.Source);
            _renderer?.Dispose();
            _renderer = null;
            _readerStale = _mode == EditorMode.Source;
            UpdateFormatSurface();
            if (_readerStale || FormatEditing)
            {
                DocumentPanel.Children.Clear();
                // The Format editor reloads for another document or text changed elsewhere, never for its own edits.
                if (FormatEditing && (!preserveOffset || _formatEditor.Markdown != document.Source)) _formatEditor.Load(document.Source, document.Path);
            }
            else
            {
                _renderer = new NativeMarkdownRenderer(DocumentPanel, _settings, document.Path, OpenLinkAsync, message => Notify(message, InfoBarSeverity.Warning));
                _renderer.Render(parsed);
            }
            ShowAnalysis(parsed);
            UpdateDocumentInfo();
            ZoomText.Text = Localization.Percent(_settings.FontSize / Typesetting.DefaultSize);
            BackButton.IsEnabled = _historyIndex > 0;
            ForwardButton.IsEnabled = _historyIndex < _history.Count - 1;
            _matches = SearchDocument(SearchBox.Text);
            _matchIndex = -1; UpdateSearchCount();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_closed) { DocumentPanel.UpdateLayout(); ReaderScroll.ChangeView(null, offset, null, true); UpdateProgress(); }
            });
        }
        catch (Exception ex) { Notify(Localization.Format("RenderError", ex.Message), InfoBarSeverity.Error); }
        finally { _rendering = false; }
    }

    private void SaveScrollPosition()
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count) _history[_historyIndex].Offset = ReaderScroll.VerticalOffset;
    }

    private async void GoHistory(int delta)
    {
        int index = _historyIndex + delta;
        if (index < 0 || index >= _history.Count) return;
        int revision = ++_openRevision;
        if (!await ConfirmLeaveAsync() || revision != _openRevision) return;
        SaveScrollPosition(); _historyIndex = index; _document = _history[index].Document;
        double offset = _history[index].Offset;
        RenderCurrent(false); WatchCurrentFile();
        if (_mode != EditorMode.Format) LoadEditor();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ReaderScroll.ChangeView(null, offset, null, true));
    }

    private async Task OpenLinkAsync(string address, bool chooseApplication)
    {
        if (address.StartsWith('#')) { JumpToAnchor(address[1..]); return; }
        var uri = LinkService.Resolve(address, _document?.Path) ?? throw new InvalidOperationException(Localization.Get("UnresolvedLink"));
        if (uri.IsFile)
        {
            if (uri.IsUnc) throw new InvalidOperationException(Localization.Get("NetworkFile"));
            if (!MarkdownService.IsMarkdown(uri.LocalPath)) throw new InvalidOperationException(Localization.Get("MarkdownOnly"));
            if (string.Equals(uri.LocalPath, _document?.Path, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(uri.Fragment)) JumpToAnchor(uri.Fragment[1..]);
                return;
            }
            await OpenFileAsync(uri.LocalPath);
            if (!string.IsNullOrEmpty(uri.Fragment)) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => JumpToAnchor(uri.Fragment[1..]));
            return;
        }
        await LinkService.OpenExternalAsync(uri, chooseApplication);
    }

    private void JumpToAnchor(string id)
    {
        id = Uri.UnescapeDataString(id);
        if (id.Length == 0) { ReaderScroll.ChangeView(null, 0, null); return; }
        if (_mode == EditorMode.Source) SetMode(EditorMode.Format);
        if (FormatEditing && _document is not null)
        {
            int index = MarkdownService.GetHeadings(MarkdownService.Parse(_document.Source)).FindIndex(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && _formatEditor.Heading(index) is { } heading) { heading.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.1 }); return; }
        }
        if (_renderer?.ResolveAnchor(id) is { } element) ScrollTo(element);
        else Notify(Localization.Format("HeadingMissing", id), InfoBarSeverity.Warning);
    }

    private List<FrameworkElement> SearchDocument(string query) => FormatEditing ? _formatEditor.Search(query) : _renderer?.Search(query) ?? [];

    private void ScrollTo(FrameworkElement element)
    {
        if (FormatEditing) { _formatEditor.ShowMatch(element, SearchBox.Text); return; }
        element = _renderer?.PrepareForNavigation(element) ?? element;
        DocumentPanel.UpdateLayout();
        var position = element.TransformToVisual(DocumentPanel).TransformPoint(new Windows.Foundation.Point());
        ReaderScroll.ChangeView(null, Math.Max(0, position.Y - 20), null);
        element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.1 });
    }

    private void Outline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is not HeadingInfo heading) return;
        if (_mode == EditorMode.Source) _sourceEditor.ScrollToLine(heading.Line);
        else JumpToAnchor(heading.Id);
    }

    private void WatchCurrentFile()
    {
        _reloadTimer?.Stop(); _watcher?.Dispose(); _watcher = null;
        if (_document?.Path is not string path) return;
        try
        {
            _reloadTimer ??= DispatcherQueue.CreateTimer();
            _reloadTimer.Interval = TimeSpan.FromMilliseconds(500); _reloadTimer.IsRepeating = false;
            _reloadTimer.Tick -= ReloadTimer_Tick; _reloadTimer.Tick += ReloadTimer_Tick;
            _watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(path)!) { Filter = System.IO.Path.GetFileName(path), NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            void Changed(object sender, FileSystemEventArgs args) => DispatcherQueue.TryEnqueue(() => { if (!_closed) { _reloadTimer.Stop(); _reloadTimer.Start(); } });
            _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Renamed += (s, e) => Changed(s, e); _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Notify(Localization.Format("AutoReloadUnavailable", ex.Message), InfoBarSeverity.Warning); }
    }

    private async void ReloadTimer_Tick(DispatcherQueueTimer sender, object args) => await ReloadCurrentAsync(true);

    private async Task ReloadCurrentAsync(bool automatic)
    {
        var document = _document;
        if (document?.Path is null) return;
        int revision = _openRevision;
        try
        {
            var (source, format) = await DocumentFile.ReadAsync(document.Path);
            if (revision != _openRevision || _document?.Path != document.Path || _closed) return;
            document = _document;
            if (automatic && source == document.Saved) return;
            // Never replace unsaved edits with the file on disk.
            if (document.IsDirty) { if (source != document.Saved) Notify(Localization.Get("ChangedOnDisk"), InfoBarSeverity.Warning); return; }
            var parsed = await Task.Run(() => MarkdownService.Parse(source));
            if (revision != _openRevision || _document != document || _closed) return;
            _document = new ReadingDocument(source, document.Path, document.Name, document.Kind, format);
            if (_historyIndex >= 0)
            {
                double offset = ReaderScroll.VerticalOffset;
                _history[_historyIndex] = new HistoryEntry(_document) { Offset = offset };
            }
            RenderCurrent(true, parsed);
            if (_mode != EditorMode.Format) LoadEditor(_sourceEditor.Selection.Start);
            if (automatic) Notify(Localization.Get("Reloaded"));
        }
        catch (Exception ex) { Notify(Localization.Format("ReloadError", ex.Message), InfoBarSeverity.Warning); }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            foreach (var extension in MarkdownService.Extensions) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await OpenFileAsync(file.Path);
        }
        catch (Exception ex) { await ShowErrorAsync(Localization.Get("PickerError"), ex.Message); }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await OpenFolderAsync(folder.Path);
        }
        catch (Exception ex) { await ShowErrorAsync(Localization.Get("PickerError"), ex.Message); }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = Clipboard.GetContent();
            if (clipboard.Contains(StandardDataFormats.StorageItems))
            {
                var items = await clipboard.GetStorageItemsAsync();
                var item = items.FirstOrDefault();
                if (item is StorageFolder folder) { await OpenFolderAsync(folder.Path); return; }
                if (item is StorageFile file) { await OpenFileAsync(file.Path); return; }
            }
            if (!clipboard.Contains(StandardDataFormats.Text)) { Notify(Localization.Get("ClipboardEmpty")); return; }
            var source = await clipboard.GetTextAsync();
            if (source.Length > 8 * 1024 * 1024) throw new InvalidOperationException(Localization.Get("TextTooLarge"));
            int revision = ++_openRevision;
            var parsed = await Task.Run(() => MarkdownService.Parse(source));
            if (revision != _openRevision || _closed) return;
            if (!await ConfirmLeaveAsync() || revision != _openRevision) return;
            Navigate(new ReadingDocument(source, null, "", DocumentKind.Clipboard), parsed);
        }
        catch (Exception ex) { Notify(Localization.Format("PasteError", ex.Message), InfoBarSeverity.Warning); }
    }

    private void CopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        try { var package = new DataPackage(); package.SetText(_document.Source); Clipboard.SetContent(package); Notify(Localization.Get("MarkdownCopied")); }
        catch (Exception ex) { Notify(Localization.Format("CopyError", ex.Message), InfoBarSeverity.Warning); }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        await ReloadCurrentAsync(false);
        if (_folderPath is not null) await OpenFolderAsync(_folderPath, false);
    }

    private async void Reveal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_document?.Path is not string path) { Notify(Localization.Get("UnsavedDocument")); return; }
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(path)!);
            var options = new FolderLauncherOptions(); options.ItemsToSelect.Add(await StorageFile.GetFileFromPathAsync(path));
            if (!await Launcher.LaunchFolderAsync(folder, options)) Notify(Localization.Get("ExplorerError"), InfoBarSeverity.Warning);
        }
        catch (Exception ex) { Notify(ex.Message, InfoBarSeverity.Warning); }
    }

    private async void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Notify(Localization.Get("DefaultInstructions"));
            await DefaultAppService.OpenSettingsAsync();
        }
        catch (Exception ex) { Notify(ex.Message, InfoBarSeverity.Warning); }
    }

    public async Task OfferDefaultAppAsync()
    {
        if (!_settings.PromptForDefaultApp || _dialogOpen || _closed) return;
        // Unpackaged development builds do not register file associations.
        string? appId = DefaultAppService.GetCurrentAppId();
        if (appId is null || DefaultAppService.IsDefaultForMarkdown(appId)) return;

        var remember = new CheckBox { Content = Localization.Get("NeverAsk") };
        var panel = new StackPanel { Spacing = 16, MaxWidth = 400 };
        panel.Children.Add(new TextBlock
        {
            Text = Localization.Get("DefaultPromptText"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(remember);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            FlowDirection = Root.FlowDirection,
            RequestedTheme = Root.ActualTheme,
            Title = Localization.Get("DefaultPromptTitle"),
            Content = panel,
            PrimaryButtonText = Localization.Get("ChooseDefault"),
            CloseButtonText = Localization.Get("NotNow"),
            DefaultButton = ContentDialogButton.Close
        };
        _dialogOpen = true;
        try
        {
            var result = await dialog.ShowAsync();
            if (remember.IsChecked == true)
            {
                _settings.PromptForDefaultApp = false;
                SaveSettings();
            }
            if (result == ContentDialogResult.Primary) await DefaultAppService.OpenSettingsAsync();
        }
        catch (Exception ex) { if (!_closed) Notify(ex.Message, InfoBarSeverity.Warning); }
        finally { _dialogOpen = false; }
    }

    private async Task ShowWelcomeAsync()
    {
        int revision = ++_openRevision;
        string source = Localization.WelcomeMarkdown();
        var parsed = await Task.Run(() => MarkdownService.Parse(source));
        if (revision != _openRevision || _closed || !await ConfirmLeaveAsync() || revision != _openRevision) return;
        Navigate(new ReadingDocument(source, null, "", DocumentKind.Welcome), parsed);
    }

    private async void Welcome_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowWelcomeAsync(); } catch (Exception ex) { Notify(ex.Message, InfoBarSeverity.Error); }
    }
    private void Back_Click(object sender, RoutedEventArgs e) => GoHistory(-1);
    private void Forward_Click(object sender, RoutedEventArgs e) => GoHistory(1);
    private void BackAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; GoHistory(-1); }
    private void ForwardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; GoHistory(1); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Sidebar_Click(object sender, RoutedEventArgs e) => SetSidebarOpen(SidebarMenu.IsChecked);
    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => SetSidebarOpen(SidebarButton.IsChecked == true);
    private void SetSidebarOpen(bool open)
    {
        NavigationSplit.IsPaneOpen = open;
        _settings.NavigationOpen = open;
        SaveSettings();
    }
    private void UpdateSidebarControls()
    {
        bool open = NavigationSplit.IsPaneOpen;
        SidebarButton.IsChecked = SidebarMenu.IsChecked = open;
        SidebarIcon.Glyph = open ? "\uE89F" : "\uE8A0";
        string action = open ? Localization.Get("HideNavigation") : Localization.Get("ShowNavigation");
        ToolTipService.SetToolTip(SidebarButton, $"{action} ({Localization.Shortcut("Ctrl+Shift+L")})");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SidebarButton, action);
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetTypography(fontSize: Typesetting.Step(_settings.FontSize, 1));
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetTypography(fontSize: Typesetting.Step(_settings.FontSize, -1));
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetTypography(fontSize: Typesetting.DefaultSize);

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == EditorMode.Source) SetMode(EditorMode.Format);
        FindBar.Visibility = Visibility.Visible; SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll();
    }
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _matches = SearchDocument(SearchBox.Text); _matchIndex = -1;
        if (_matches.Count > 0) MoveMatch(1); else UpdateSearchCount();
    }
    private void MoveMatch(int delta)
    {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + delta + _matches.Count) % _matches.Count;
        ScrollTo(_matches[_matchIndex]); UpdateSearchCount();
    }
    private void UpdateSearchCount() => SearchCount.Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? "" : _matches.Count == 0 ? Localization.Get("NoResults") : Localization.Format("MatchCount", _matchIndex + 1, _matches.Count);
    private void PreviousMatch_Click(object sender, RoutedEventArgs e) => MoveMatch(-1);
    private void NextMatch_Click(object sender, RoutedEventArgs e) => MoveMatch(1);
    private void CloseFind_Click(object sender, RoutedEventArgs e) { FindBar.Visibility = Visibility.Collapsed; SearchBox.Text = ""; }
    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { MoveMatch(1); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CloseFind_Click(sender, e); e.Handled = true; }
    }
    private void Reader_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdateProgress();
    private void UpdateProgress()
    {
        var scroll = FormatEditing ? _formatEditor.Scroll : ReaderScroll;
        ProgressText.Text = Localization.Percent(scroll.ScrollableHeight <= 0 ? 1 : scroll.VerticalOffset / scroll.ScrollableHeight);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = Localization.Get("DropOpen"); }
    }
    private async void Root_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var item = items.FirstOrDefault();
            if (item is StorageFolder folder) await OpenFolderAsync(folder.Path);
            else if (item is StorageFile file) await OpenFileAsync(file.Path);
        }
        catch (Exception ex) { Notify(ex.Message, InfoBarSeverity.Warning); }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        var dialog = new ReadingPreferencesDialog(_settings) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                dialog.ApplyTo(_settings);
                SaveSettings(); ApplyTheme(); ApplyLanguage(); ApplyTypography(render: false);
            }
        }
        finally { _dialogOpen = false; }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try { await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, FlowDirection = Root.FlowDirection, Title = "Amoxtli", Content = Localization.Get("AboutText"), CloseButtonText = Localization.Get("Close") }.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Notify($"{title}: {message}", InfoBarSeverity.Error); return Task.CompletedTask;
    }
    private void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_closed) return;
        Notice.Title = severity == InfoBarSeverity.Error ? Localization.Get("ErrorTitle") : severity == InfoBarSeverity.Warning ? Localization.Get("WarningTitle") : "Amoxtli";
        Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true;
    }
}
