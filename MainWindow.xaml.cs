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
    private sealed record ReadingDocument(string Source, string? Path, string Name);
    private sealed class HistoryEntry(ReadingDocument document)
    {
        public ReadingDocument Document { get; } = document;
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
            if (args.NewSize.Width < 740) NavigationSplit.DisplayMode = SplitViewDisplayMode.Overlay;
            else NavigationSplit.DisplayMode = SplitViewDisplayMode.Inline;
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
    private void UpdateTitleBarTheme() => AppWindow.TitleBar.PreferredTheme = Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Notify($"No se pudieron guardar las preferencias: {ex.Message}", InfoBarSeverity.Warning); }
    }

    public async Task OpenFileAsync(string path)
    {
        int revision = ++_openRevision;
        try
        {
            path = System.IO.Path.GetFullPath(path);
            if (!MarkdownService.IsMarkdown(path)) throw new InvalidOperationException("Elige un archivo .md, .markdown, .mdown, .mkd o .mkdn.");
            string source = await ReadMarkdownAsync(path);
            var parsed = await Task.Run(() => MarkdownService.Parse(source));
            if (revision != _openRevision || _closed) return;
            Navigate(new ReadingDocument(source, path, System.IO.Path.GetFileName(path)), parsed);
            if (_folderPath is null) await OpenFolderAsync(System.IO.Path.GetDirectoryName(path)!, false);
        }
        catch (Exception ex) { if (revision == _openRevision) await ShowErrorAsync("No se pudo abrir el archivo", ex.Message); }
    }

    private static async Task<string> ReadMarkdownAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        if (file.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Este archivo supera el límite de lectura de 8 MB.");
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
            if (entries.Count == 0) Notify("Esta carpeta no contiene archivos Markdown ni subcarpetas visibles.");
        }
        catch (Exception ex) { if (revision == _folderRevision) await ShowErrorAsync("No se pudo abrir la carpeta", ex.Message); }
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
        catch (Exception ex) { Notify($"No se pudo leer {item.Name}: {ex.Message}", InfoBarSeverity.Warning); }
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
            DocumentName.Text = document.Name;
            DocumentPath.Text = document.Path ?? "Documento temporal · solo lectura";
            ToolTipService.SetToolTip(DocumentPath, DocumentPath.Text);
            Title = $"{document.Name} — Amoxtli";
            int words = Regex.Matches(document.Source, @"\S+").Count;
            StatusText.Text = $"Solo lectura  ·  {words:N0} palabras  ·  {Math.Max(1, (int)Math.Ceiling(words / 220d))} min de lectura";
            ZoomText.Text = $"{_settings.FontSize / 16:P0}";
            BackButton.IsEnabled = _historyIndex > 0;
            ForwardButton.IsEnabled = _historyIndex < _history.Count - 1;
            _matches = _renderer.Search(SearchBox.Text);
            _matchIndex = -1; UpdateSearchCount();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_closed) { DocumentPanel.UpdateLayout(); ReaderScroll.ChangeView(null, offset, null, true); if (!preserveOffset) SourceScroll.ChangeView(null, 0, null, true); }
            });
        }
        catch (Exception ex) { Notify($"No se pudo representar el documento: {ex.Message}", InfoBarSeverity.Error); }
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
        var uri = LinkService.Resolve(address, _document?.Path) ?? throw new InvalidOperationException("No se pudo resolver el enlace. El Markdown pegado no tiene una carpeta base.");
        if (uri.IsFile)
        {
            if (uri.IsUnc) throw new InvalidOperationException("Abre los archivos de red desde el selector de archivos.");
            if (!MarkdownService.IsMarkdown(uri.LocalPath)) throw new InvalidOperationException("En este visor solo se abren enlaces a archivos Markdown.");
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
        else Notify($"No se encontró el encabezado «{id}».", InfoBarSeverity.Warning);
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
        catch (Exception ex) { Notify($"La actualización automática no está disponible: {ex.Message}", InfoBarSeverity.Warning); }
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
            if (automatic) Notify("El archivo cambió y se actualizó la vista.");
        }
        catch (Exception ex) { Notify($"No se pudo recargar: {ex.Message}", InfoBarSeverity.Warning); }
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
        catch (Exception ex) { await ShowErrorAsync("No se pudo abrir el selector", ex.Message); }
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
        catch (Exception ex) { await ShowErrorAsync("No se pudo abrir el selector", ex.Message); }
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
            if (!clipboard.Contains(StandardDataFormats.Text)) { Notify("El portapapeles no contiene texto Markdown ni archivos."); return; }
            var source = await clipboard.GetTextAsync();
            if (source.Length > 8 * 1024 * 1024) throw new InvalidOperationException("El texto supera el límite de 8 MB.");
            int revision = ++_openRevision;
            var parsed = await Task.Run(() => MarkdownService.Parse(source));
            if (revision != _openRevision || _closed) return;
            Navigate(new ReadingDocument(source, null, "Markdown del portapapeles"), parsed);
        }
        catch (Exception ex) { Notify($"No se pudo pegar: {ex.Message}", InfoBarSeverity.Warning); }
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
        try { var package = new DataPackage(); package.SetText(_document.Source); Clipboard.SetContent(package); Notify("Markdown copiado al portapapeles."); }
        catch (Exception ex) { Notify($"No se pudo copiar: {ex.Message}", InfoBarSeverity.Warning); }
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
            if (_document?.Path is not string path) { Notify("Este documento no está guardado en un archivo."); return; }
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(path)!);
            var options = new FolderLauncherOptions(); options.ItemsToSelect.Add(await StorageFile.GetFileFromPathAsync(path));
            if (!await Launcher.LaunchFolderAsync(folder, options)) Notify("Windows no pudo abrir el Explorador.", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { Notify(ex.Message, InfoBarSeverity.Warning); }
    }

    private async void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Notify("En Aplicaciones predeterminadas, busca Amoxtli o .md y selecciona las extensiones que quieras asociar. Requiere instalar el paquete MSIX.");
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

        var remember = new CheckBox { Content = "No volver a preguntar" };
        var panel = new StackPanel { Spacing = 16, MaxWidth = 400 };
        panel.Children.Add(new TextBlock
        {
            Text = "Amoxtli todavía no es tu app predeterminada para los archivos .md. Elígela en la configuración de Windows para abrir tus documentos Markdown con un doble clic.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(remember);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "¿Abrir tus archivos Markdown con Amoxtli?",
            Content = panel,
            PrimaryButtonText = "Elegir como predeterminada",
            CloseButtonText = "Ahora no",
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
        string source = await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Welcome.md"));
        var parsed = await Task.Run(() => MarkdownService.Parse(source));
        if (revision == _openRevision && !_closed) Navigate(new ReadingDocument(source, null, "Guía de Amoxtli"), parsed);
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
        string action = open ? "Ocultar panel de navegación" : "Mostrar panel de navegación";
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
    private void UpdateSearchCount() => SearchCount.Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? "" : _matches.Count == 0 ? "Sin resultados" : $"{_matchIndex + 1} / {_matches.Count} bloques";
    private void PreviousMatch_Click(object sender, RoutedEventArgs e) => MoveMatch(-1);
    private void NextMatch_Click(object sender, RoutedEventArgs e) => MoveMatch(1);
    private void CloseFind_Click(object sender, RoutedEventArgs e) { FindBar.Visibility = Visibility.Collapsed; SearchBox.Text = ""; }
    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { MoveMatch(1); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CloseFind_Click(sender, e); e.Handled = true; }
    }
    private void Reader_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => ProgressText.Text = ReaderScroll.ScrollableHeight <= 0 ? "100 %" : $"{ReaderScroll.VerticalOffset / ReaderScroll.ScrollableHeight:P0}";

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "Abrir en Amoxtli"; }
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
        var remote = new ToggleSwitch { Header = "Cargar imágenes remotas (HTTP/HTTPS)", IsOn = _settings.RemoteImages };
        var theme = new ComboBox { Header = "Tema", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "Usar el tema de Windows", "Claro", "Oscuro" }, SelectedIndex = _settings.Theme == "Light" ? 1 : _settings.Theme == "Dark" ? 2 : 0 };
        var size = new Slider { Header = "Tamaño del texto", Minimum = 12, Maximum = 28, StepFrequency = 1, Value = _settings.FontSize };
        var defaultAppPrompt = new CheckBox { Content = "Sugerir Amoxtli como app predeterminada al iniciar", IsChecked = _settings.PromptForDefaultApp };
        var panel = new StackPanel { Spacing = 20, MinWidth = 320 };
        panel.Children.Add(remote);
        panel.Children.Add(new TextBlock { Text = "Las imágenes remotas se solicitan al servidor que aparece en el documento. Puedes desactivarlas para leer sin esas conexiones.", TextWrapping = TextWrapping.Wrap, MaxWidth = 400, FontSize = 12 });
        panel.Children.Add(theme); panel.Children.Add(size);
        panel.Children.Add(defaultAppPrompt);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Preferencias de lectura", Content = panel, PrimaryButtonText = "Guardar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Primary };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _settings.RemoteImages = remote.IsOn; _settings.FontSize = size.Value;
                _settings.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Default" };
                _settings.PromptForDefaultApp = defaultAppPrompt.IsChecked == true;
                SaveSettings(); ApplyTheme(); RenderCurrent(true);
            }
        }
        finally { _dialogOpen = false; }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try { await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Amoxtli", Content = "Visor de Markdown para Windows\nVersión 1.0\n\nInterfaz y lectura nativas con WinUI 3.\nAnálisis de Markdown con Markdig (licencia BSD-2-Clause).\n\nSelecciona texto para copiarlo con Ctrl+C.\nPega Markdown con Ctrl+V para leerlo sin guardar cambios.", CloseButtonText = "Cerrar" }.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Notify($"{title}: {message}", InfoBarSeverity.Error); return Task.CompletedTask;
    }
    private void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_closed) return;
        Notice.Title = severity == InfoBarSeverity.Error ? "Ocurrió un problema" : severity == InfoBarSeverity.Warning ? "Atención" : "Amoxtli";
        Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true;
    }
}
