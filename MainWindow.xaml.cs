using Amoxtli.Models;
using Amoxtli.Rendering;
using Amoxtli.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Text.RegularExpressions;
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
    private sealed record ReadingDocument(string Source, string? Path, string Name, DocumentKind Kind = DocumentKind.File);
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
        DocumentPanel.FlowDirection = SourceText.FlowDirection = FlowDirection.LeftToRight;
        if (_folderPath is not null)
        {
            FolderName.Text = System.IO.Path.GetFileName(_folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(FolderName.Text)) FolderName.Text = _folderPath;
        }
        foreach (var entry in _history)
            if (entry.Document.Kind == DocumentKind.Welcome)
                entry.Document = entry.Document with { Source = Localization.WelcomeMarkdown() };
        if (_historyIndex >= 0) _document = _history[_historyIndex].Document;
        Notice.IsOpen = false;
        UpdateSidebarControls();
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
            string source = await ReadMarkdownAsync(path);
            var parsed = await Task.Run(() => MarkdownService.Parse(source));
            if (revision != _openRevision || _closed) return;
            Navigate(new ReadingDocument(source, path, System.IO.Path.GetFileName(path)), parsed);
            if (_folderPath is null) await OpenFolderAsync(System.IO.Path.GetDirectoryName(path)!, false);
        }
        catch (Exception ex) { if (revision == _openRevision) await ShowErrorAsync(Localization.Get("OpenFileError"), ex.Message); }
    }

    private static async Task<string> ReadMarkdownAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        if (file.Length > 8 * 1024 * 1024) throw new InvalidOperationException(Localization.Get("FileTooLarge"));
        using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
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
            _renderer = new NativeMarkdownRenderer(DocumentPanel, _settings, document.Path, OpenLinkAsync, message => Notify(message, InfoBarSeverity.Warning));
            _renderer.Render(parsed);
            SourceText.Text = document.Source;
            SourceText.FontSize = _settings.FontSize * .9;
            OutlineList.ItemsSource = MarkdownService.GetHeadings(parsed);
            OutlineEmpty.Visibility = OutlineList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DocumentName.Text = document.Kind switch
            {
                DocumentKind.Welcome => Localization.Get("GuideTitle"),
                DocumentKind.Clipboard => Localization.Get("ClipboardDocument"),
                _ => document.Name
            };
            DocumentPath.Text = document.Path ?? Localization.Get("TemporaryDocument");
            ToolTipService.SetToolTip(DocumentPath, DocumentPath.Text);
            Title = $"{DocumentName.Text} — Amoxtli";
            int words = Regex.Matches(document.Source, @"\S+").Count;
            StatusText.Text = Localization.Format("ReadingStatus", words, Math.Max(1, (int)Math.Ceiling(words / 220d)));
            ZoomText.Text = Localization.Percent(_settings.FontSize / 16);
            BackButton.IsEnabled = _historyIndex > 0;
            ForwardButton.IsEnabled = _historyIndex < _history.Count - 1;
            _matches = _renderer.Search(SearchBox.Text);
            _matchIndex = -1; UpdateSearchCount();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_closed) { DocumentPanel.UpdateLayout(); ReaderScroll.ChangeView(null, offset, null, true); UpdateProgress(); if (!preserveOffset) SourceScroll.ChangeView(null, 0, null, true); }
            });
        }
        catch (Exception ex) { Notify(Localization.Format("RenderError", ex.Message), InfoBarSeverity.Error); }
        finally { _rendering = false; }
    }

    private void SaveScrollPosition()
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count) _history[_historyIndex].Offset = ReaderScroll.VerticalOffset;
    }

    private void GoHistory(int delta)
    {
        int index = _historyIndex + delta;
        if (index < 0 || index >= _history.Count) return;
        ++_openRevision;
        SaveScrollPosition(); _historyIndex = index; _document = _history[index].Document;
        double offset = _history[index].Offset;
        RenderCurrent(false); WatchCurrentFile();
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
        if (_renderer?.Anchors.TryGetValue(id, out var element) == true)
        {
            SetSource(false); ScrollTo(element);
        }
        else Notify(Localization.Format("HeadingMissing", id), InfoBarSeverity.Warning);
    }

    private void ScrollTo(FrameworkElement element)
    {
        DocumentPanel.UpdateLayout();
        var position = element.TransformToVisual(DocumentPanel).TransformPoint(new Windows.Foundation.Point());
        ReaderScroll.ChangeView(null, Math.Max(0, position.Y - 20), null);
        element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.1 });
    }

    private void Outline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is HeadingInfo heading) JumpToAnchor(heading.Id);
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
            var source = await ReadMarkdownAsync(document.Path);
            var parsed = await Task.Run(() => MarkdownService.Parse(source));
            if (revision != _openRevision || _document != document || _closed) return;
            if (automatic && source == document.Source) return;
            _document = document with { Source = source };
            if (_historyIndex >= 0)
            {
                double offset = ReaderScroll.VerticalOffset;
                _history[_historyIndex] = new HistoryEntry(_document) { Offset = offset };
            }
            RenderCurrent(true, parsed);
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
            Navigate(new ReadingDocument(source, null, "", DocumentKind.Clipboard), parsed);
        }
        catch (Exception ex) { Notify(Localization.Format("PasteError", ex.Message), InfoBarSeverity.Warning); }
    }

    private void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        // TextBox already handles its native paste. Suppress the document command
        // without inserting a second copy into the focused text input.
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is not TextBox) Paste_Click(sender, new RoutedEventArgs());
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
        if (revision == _openRevision && !_closed) Navigate(new ReadingDocument(source, null, "", DocumentKind.Welcome), parsed);
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
        ToolTipService.SetToolTip(SidebarButton, $"{action} (Ctrl+B)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SidebarButton, action);
    }
    private void Source_Click(object sender, RoutedEventArgs e) => SetSource(SourceMenu.IsChecked);
    private void SetSource(bool source)
    {
        SourceMenu.IsChecked = source;
        SourceScroll.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
        ReaderScroll.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeFont(1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeFont(-1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ChangeFont(16 - _settings.FontSize);
    private void ChangeFont(double delta) { _settings.FontSize = Math.Clamp(_settings.FontSize + delta, 12, 28); SaveSettings(); RenderCurrent(true); }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        SetSource(false); FindBar.Visibility = Visibility.Visible; SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll();
    }
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _matches = _renderer?.Search(SearchBox.Text) ?? []; _matchIndex = -1;
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
    private void UpdateProgress() => ProgressText.Text = Localization.Percent(ReaderScroll.ScrollableHeight <= 0 ? 1 : ReaderScroll.VerticalOffset / ReaderScroll.ScrollableHeight);

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
        var remote = new ToggleSwitch { Header = Localization.Get("RemoteImages"), OnContent = Localization.Get("On"), OffContent = Localization.Get("Off"), IsOn = _settings.RemoteImages };
        var theme = new ComboBox { Header = Localization.Get("Theme"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { Localization.Get("SystemTheme"), Localization.Get("Light"), Localization.Get("Dark") }, SelectedIndex = _settings.Theme == "Light" ? 1 : _settings.Theme == "Dark" ? 2 : 0 };
        var languages = new[] { new LanguageOption("auto", Localization.Get("SystemLanguage")) }.Concat(Localization.Languages).ToList();
        var language = new ComboBox { Header = Localization.Get("Language"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = languages, DisplayMemberPath = nameof(LanguageOption.NativeName), SelectedItem = languages.FirstOrDefault(item => item.Code == _settings.Language) ?? languages[0] };
        language.FlowDirection = FlowDirection.LeftToRight;
        var size = new Slider { Header = Localization.Get("FontSize"), Minimum = 12, Maximum = 28, StepFrequency = 1, Value = _settings.FontSize };
        var defaultAppPrompt = new CheckBox { Content = new TextBlock { Text = Localization.Get("DefaultAppPrompt"), TextWrapping = TextWrapping.Wrap }, IsChecked = _settings.PromptForDefaultApp };
        var panel = new StackPanel { Spacing = 20, MinWidth = 320 };
        panel.Children.Add(language);
        panel.Children.Add(remote);
        panel.Children.Add(new TextBlock { Text = Localization.Get("RemoteImagesHelp"), TextWrapping = TextWrapping.Wrap, MaxWidth = 400, FontSize = 12 });
        panel.Children.Add(theme); panel.Children.Add(size);
        panel.Children.Add(defaultAppPrompt);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, FlowDirection = Root.FlowDirection, Title = Localization.Get("Preferences"), Content = panel, PrimaryButtonText = Localization.Get("Save"), CloseButtonText = Localization.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _settings.RemoteImages = remote.IsOn; _settings.FontSize = size.Value;
                _settings.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Default" };
                _settings.PromptForDefaultApp = defaultAppPrompt.IsChecked == true;
                _settings.Language = (language.SelectedItem as LanguageOption)?.Code ?? "auto";
                SaveSettings(); ApplyTheme(); ApplyLanguage();
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
