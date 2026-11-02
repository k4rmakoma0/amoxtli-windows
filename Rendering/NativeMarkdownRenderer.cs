using Amoxtli.Services;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI.Text;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using XamlInline = Microsoft.UI.Xaml.Documents.Inline;

namespace Amoxtli.Rendering;

/// <summary>Markdown AST to native WinUI controls. No HTML engine or script execution.</summary>
public sealed class NativeMarkdownRenderer : IDisposable
{
    private readonly StackPanel _host;
    private readonly ReaderSettings _settings;
    private readonly string? _path;
    private readonly Func<string, bool, Task> _openLink;
    private readonly Action<string> _report;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _imageSlots;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly List<(FrameworkElement Element, string Text)> _searchable = [];
    private readonly Dictionary<string, FrameworkElement> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(RichTextBlock Text, List<(Hyperlink Link, string Url)> Links)> _links = [];
    private readonly double _font;
    private readonly Brush _foreground;
    private readonly Brush _secondary;
    private readonly Brush _stroke;
    private readonly Brush _subtle;
    private bool _disposed;
    private VirtualMarkdownDocument? _virtualDocument;
    public IReadOnlyDictionary<string, FrameworkElement> Anchors => _virtualDocument?.Anchors ?? _anchors;

    public NativeMarkdownRenderer(StackPanel host, ReaderSettings settings, string? path, Func<string, bool, Task> openLink, Action<string> report, SemaphoreSlim? imageSlots = null)
    {
        _imageSlots = imageSlots ?? new(4);
        _host = host; _settings = settings; _path = path; _openLink = openLink; _report = report; _font = settings.FontSize;
        bool dark = host.ActualTheme == ElementTheme.Dark;
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        _foreground = highContrast ? Resource("TextFillColorPrimaryBrush") : new SolidColorBrush(dark ? Colors.WhiteSmoke : Windows.UI.Color.FromArgb(255, 32, 32, 32));
        _secondary = Resource("TextFillColorSecondaryBrush");
        _stroke = Resource("CardStrokeColorDefaultBrush");
        _subtle = Resource("SubtleFillColorSecondaryBrush");
    }

    private static Brush Resource(string name) => (Brush)Application.Current.Resources[name];

    public void Render(MarkdownDocument document)
    {
        _host.Children.Clear();
        if (document.Count > 300 || document.Sum(block => (long)Math.Max(0, block.Span.End - block.Span.Start + 1)) > 512 * 1024)
        {
            _virtualDocument = new VirtualMarkdownDocument(_host, document,
                panel => new NativeMarkdownRenderer(panel, _settings, _path, _openLink, _report, _imageSlots));
            return;
        }
        foreach (var block in document) AddBlock(_host, block, 0);
    }

    internal void RenderBlock(MdBlock block) => AddBlock(_host, block, 0);
    public FrameworkElement PrepareForNavigation(FrameworkElement element) => _virtualDocument?.PrepareForNavigation(element) ?? element;
    public FrameworkElement? ResolveAnchor(string id) => _virtualDocument is not null ? _virtualDocument.ResolveAnchor(id)
        : _anchors.GetValueOrDefault(id);

    private void AddBlock(StackPanel target, MdBlock block, int depth)
    {
        if (depth > 40) { target.Children.Add(Code(Localization.Get("TooNested"), "")); return; }
        switch (block)
        {
            case HeadingBlock heading:
            {
                double[] sizes = [0, 2.0, 1.6, 1.3, 1.15, 1.05, 1.0];
                var text = Rich(heading.Inline, _font * sizes[heading.Level]);
                text.FontWeight = FontWeights.SemiBold;
                text.Margin = new Thickness(0, heading.Level == 1 ? 0 : 22, 0, 10);
                AutomationProperties.SetHeadingLevel(text, (Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel)heading.Level);
                var id = heading.GetAttributes().Id;
                if (!string.IsNullOrEmpty(id)) _anchors[id] = text;
                target.Children.Add(text);
                break;
            }
            case ParagraphBlock paragraph:
                target.Children.Add(Rich(paragraph.Inline));
                break;
            case ThematicBreakBlock:
                target.Children.Add(new Border { Height = 1, Background = _stroke, Margin = new Thickness(0, 18, 0, 18) });
                break;
            case ListBlock list:
            {
                var items = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 12) };
                int number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var task = (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild as TaskList;
                    if (task is null)
                        row.Children.Add(new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", FontSize = _font, MinWidth = 22, Margin = new Thickness(4, 5, 0, 0), Foreground = _secondary, TextAlignment = TextAlignment.Right });
                    else
                    {
                        var check = new CheckBox { IsChecked = task.Checked, IsHitTestVisible = false, IsTabStop = false, MinWidth = 22, MinHeight = 24, Margin = new Thickness(0, 0, 0, 0) };
                        AutomationProperties.SetName(check, task.Checked ? Localization.Get("TaskDone") : Localization.Get("TaskPending"));
                        row.Children.Add(check);
                    }
                    var body = new StackPanel();
                    Grid.SetColumn(body, 1); row.Children.Add(body);
                    foreach (var child in item) AddBlock(body, child, depth + 1);
                    items.Children.Add(row);
                }
                target.Children.Add(items); break;
            }
            case QuoteBlock quote:
            {
                var body = new StackPanel();
                foreach (var child in quote) AddBlock(body, child, depth + 1);
                target.Children.Add(new Border { BorderBrush = Resource("AccentFillColorDefaultBrush"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(16, 8, 12, 0), Margin = new Thickness(0, 8, 0, 16), Background = _subtle, Child = body });
                break;
            }
            case FencedCodeBlock fenced:
                target.Children.Add(Code(fenced.Lines.ToString(), fenced.Info ?? "")); break;
            case YamlFrontMatterBlock yaml:
                target.Children.Add(Code(yaml.Lines.ToString(), Localization.Get("Yaml"))); break;
            case CodeBlock code:
                target.Children.Add(Code(code.Lines.ToString(), "")); break;
            case HtmlBlock html:
                target.Children.Add(Code(html.Lines.ToString(), Localization.Get("HtmlSource"))); break;
            case Table table:
                target.Children.Add(RenderTable(table, depth)); break;
            case FootnoteGroup group:
            {
                target.Children.Add(new Border { Height = 1, Background = _stroke, Margin = new Thickness(0, 24, 0, 12) });
                foreach (var footnote in group.OfType<Footnote>())
                {
                    var body = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                    _anchors[$"fn:{FootnoteLabel(footnote)}"] = body;
                    var label = new TextBlock { Text = $"{footnote.Order}. ", FontWeight = FontWeights.SemiBold, FontSize = _font * .85 };
                    body.Children.Add(label);
                    foreach (var child in footnote) AddBlock(body, child, depth + 1);
                    target.Children.Add(body);
                }
                break;
            }
            case DefinitionTerm term:
            {
                var text = Rich(term.Inline); text.FontWeight = FontWeights.SemiBold; target.Children.Add(text); break;
            }
            case DefinitionItem definition:
            {
                var body = new StackPanel { Margin = new Thickness(20, 0, 0, 8) };
                foreach (var child in definition) AddBlock(body, child, depth + 1);
                target.Children.Add(body); break;
            }
            case CustomContainer custom:
            {
                var body = new StackPanel();
                if (!string.IsNullOrEmpty(custom.Info)) body.Children.Add(new TextBlock { Text = custom.Info, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
                foreach (var child in custom) AddBlock(body, child, depth + 1);
                target.Children.Add(new Border { Child = body, Background = _subtle, BorderBrush = _stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(16), Margin = new Thickness(0, 8, 0, 12) });
                break;
            }
            case ContainerBlock container:
                foreach (var child in container) AddBlock(target, child, depth + 1);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                target.Children.Add(Rich(leaf.Inline)); break;
        }
    }

    private RichTextBlock Rich(ContainerInline? inline, double? size = null)
    {
        var text = new RichTextBlock { FontSize = size ?? _font, Foreground = _foreground, IsTextSelectionEnabled = true, TextReadingOrder = TextReadingOrder.DetectFromContent, TextAlignment = TextAlignment.DetectFromContent, TextWrapping = TextWrapping.Wrap, LineHeight = (size ?? _font) * 1.55, Margin = new Thickness(0, 3, 0, 10) };
        var paragraph = new Paragraph();
        var links = new List<(Hyperlink Link, string Url)>();
        AppendInlines(paragraph.Inlines, inline, links, text);
        text.Blocks.Add(paragraph);
        _searchable.Add((text, MarkdownService.PlainText(inline)));
        if (links.Count > 0)
        {
            _links.Add((text, links));
            MenuFlyout ParagraphMenu()
            {
                var menu = links.Count == 1 ? LinkMenu(links[0].Url) : new MenuFlyout();
                if (links.Count > 1)
                {
                    foreach (var link in links)
                    {
                        var submenu = new MenuFlyoutSubItem { Text = link.Url.Length > 70 ? link.Url[..67] + "…" : link.Url };
                        var actions = LinkMenu(link.Url);
                        foreach (var action in actions.Items.ToList()) { actions.Items.Remove(action); submenu.Items.Add(action); }
                        menu.Items.Add(submenu);
                    }
                }
                menu.Items.Add(new MenuFlyoutSeparator());
                var copy = new MenuFlyoutItem { Text = Localization.Get("CopySelection"), IsEnabled = !string.IsNullOrEmpty(text.SelectedText) };
                copy.Click += (_, _) => CopyText(text.SelectedText); menu.Items.Add(copy);
                var select = new MenuFlyoutItem { Text = Localization.Get("SelectBlock") };
                select.Click += (_, _) => text.SelectAll(); menu.Items.Add(select);
                return menu;
            }
            text.ContextFlyout = ParagraphMenu();
            // WinUI's text command flyout handles right taps internally. Supply the
            // ContextFlyout before that gesture, rather than competing with it.
            text.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                var pointer = args.GetCurrentPoint(text);
                if (!pointer.Properties.IsRightButtonPressed) return;
                var position = text.GetPositionFromPoint(pointer.Position);
                var match = position is null ? default : links.FirstOrDefault(l => position.Offset >= l.Link.ContentStart.Offset && position.Offset <= l.Link.ContentEnd.Offset);
                text.ContextFlyout = match.Link is null ? ParagraphMenu() : LinkMenu(match.Url);
            }), true);
        }
        return text;
    }

    private void AppendInlines(InlineCollection output, ContainerInline? container, List<(Hyperlink Link, string Url)> links, RichTextBlock owner, int depth = 0)
    {
        if (container is null || depth > 40) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: output.Add(new Run { Text = literal.Content.ToString() }); break;
                case HtmlEntityInline entity: output.Add(new Run { Text = entity.Transcoded.ToString() }); break;
                case LineBreakInline line:
                    output.Add(line.IsHard ? new LineBreak() : new Run { Text = " " }); break;
                case CodeInline code:
                    output.Add(new Run { Text = code.Content, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = _font * .9 }); break;
                case EmphasisInline emphasis:
                {
                    Span span = new();
                    if (emphasis.DelimiterChar is '*' or '_')
                    {
                        if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                        else span.FontStyle = FontStyle.Italic;
                    }
                    else if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount == 2) span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterChar == '+') span.TextDecorations = TextDecorations.Underline;
                    else if (emphasis.DelimiterChar == '=') span.Foreground = Resource("AccentTextFillColorPrimaryBrush");
                    else if (emphasis.DelimiterChar is '^' or '~')
                    {
                        Typography.SetVariants(span, emphasis.DelimiterChar == '^' ? FontVariants.Superscript : FontVariants.Subscript);
                    }
                    AppendInlines(span.Inlines, emphasis, links, owner, depth + 1); output.Add(span); break;
                }
                case LinkInline link when link.IsImage:
                    output.Add(new InlineUIContainer { Child = Image(link) }); break;
                case LinkInline link:
                {
                    string url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                    var hyperlink = Hyperlink(url);
                    AppendInlines(hyperlink.Inlines, link, links, owner, depth + 1);
                    output.Add(hyperlink); links.Add((hyperlink, url)); break;
                }
                case AutolinkInline auto:
                {
                    string url = auto.IsEmail ? $"mailto:{auto.Url}" : auto.Url;
                    var hyperlink = Hyperlink(url); hyperlink.Inlines.Add(new Run { Text = auto.Url });
                    output.Add(hyperlink); links.Add((hyperlink, url)); break;
                }
                case FootnoteLink note:
                {
                    string label = FootnoteLabel(note.Footnote);
                    string id = note.IsBackLink ? $"fnref:{label}" : $"fn:{label}";
                    if (!note.IsBackLink) _anchors.TryAdd($"fnref:{label}", owner);
                    var hyperlink = Hyperlink("#" + id);
                    hyperlink.Inlines.Add(new Run { Text = note.IsBackLink ? " ↩" : $"[{note.Footnote.Order}]", FontSize = _font * .8 });
                    output.Add(hyperlink); links.Add((hyperlink, "#" + id)); break;
                }
                case TaskList: break; // The list row supplies a native read-only checkbox.
                case HtmlInline html:
                    if (html.Tag.Equals("<br>", StringComparison.OrdinalIgnoreCase) || html.Tag.Equals("<br/>", StringComparison.OrdinalIgnoreCase) || html.Tag.Equals("<br />", StringComparison.OrdinalIgnoreCase)) output.Add(new LineBreak());
                    else output.Add(new Run { Text = html.Tag, FontFamily = new FontFamily("Consolas"), FontSize = _font * .9 });
                    break;
                case ContainerInline child:
                    AppendInlines(output, child, links, owner, depth + 1); break;
            }
        }
    }

    private Hyperlink Hyperlink(string url)
    {
        var hyperlink = new Hyperlink();
        hyperlink.Click += async (_, _) => { try { await _openLink(url, false); } catch (Exception ex) { _report(ex.Message); } };
        ToolTipService.SetToolTip(hyperlink, url);
        return hyperlink;
    }

    private MenuFlyout LinkMenu(string url)
    {
        var menu = new MenuFlyout();
        void Item(string title, bool choose)
        {
            var item = new MenuFlyoutItem { Text = title };
            item.Click += async (_, _) => { try { await _openLink(url, choose); } catch (Exception ex) { _report(ex.Message); } };
            menu.Items.Add(item);
        }
        Item(Localization.Get("OpenLink"), false);
        var resolved = LinkService.Resolve(url, _path);
        if (resolved is not null && LinkService.IsWeb(resolved)) Item(Localization.Get("OpenWith"), true);
        var copy = new MenuFlyoutItem { Text = Localization.Get("CopyLink") };
        copy.Click += (_, _) => CopyText(resolved?.AbsoluteUri ?? url);
        menu.Items.Add(copy);
        return menu;
    }

    private FrameworkElement Code(string code, string language)
    {
        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(language) ? Localization.Get("Code") : language, FontSize = 12, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center });
        var copy = new Button { Content = Localization.Get("Copy"), Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
        copy.Click += (_, _) => CopyText(code);
        Grid.SetColumn(copy, 1); header.Children.Add(copy); panel.Children.Add(header);
        var text = new TextBlock { Text = code, IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = _font * .88, TextWrapping = TextWrapping.NoWrap, Foreground = _foreground, Padding = new Thickness(0, 4, 0, 8) };
        var scroll = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled };
        Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
        _searchable.Add((text, code));
        return new Border { Child = panel, Background = _subtle, BorderBrush = _stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 10, 16, 8), Margin = new Thickness(0, 6, 0, 16) };
    }

    private FrameworkElement RenderTable(Table table, int depth)
    {
        var grid = new Grid { MinWidth = 360 };
        int columns = Math.Max(1, table.OfType<TableRow>().Select(row => row.OfType<TableCell>().Sum(cell => Math.Max(1, cell.ColumnSpan))).DefaultIfEmpty(1).Max());
        for (int column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 110 });
        int rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int nextColumn = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var body = new StackPanel();
                foreach (var child in cell) AddBlock(body, child, depth + 1);
                int column = Math.Clamp(cell.ColumnIndex < 0 ? nextColumn : cell.ColumnIndex, 0, columns - 1);
                nextColumn = column + Math.Max(1, cell.ColumnSpan);
                foreach (var text in body.Children.OfType<RichTextBlock>())
                {
                    if (row.IsHeader) text.FontWeight = FontWeights.SemiBold;
                    if (column < table.ColumnDefinitions.Count)
                    {
                        text.TextAlignment = table.ColumnDefinitions[column].Alignment switch
                        {
                            TableColumnAlign.Center => TextAlignment.Center,
                            TableColumnAlign.Right => TextAlignment.Right,
                            _ => TextAlignment.Left
                        };
                    }
                    text.Margin = new Thickness(0);
                }
                var border = new Border { Child = body, Padding = new Thickness(12), BorderBrush = _stroke, BorderThickness = new Thickness(0, 0, 1, 1), Background = row.IsHeader ? _subtle : null };
                Grid.SetColumn(border, column); Grid.SetRow(border, rowIndex);
                Grid.SetColumnSpan(border, Math.Clamp(cell.ColumnSpan, 1, columns - column));
                Grid.SetRowSpan(border, Math.Max(1, cell.RowSpan));
                grid.Children.Add(border);
            }
            rowIndex++;
        }
        return new Border { Child = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, BorderBrush = _stroke, BorderThickness = new Thickness(1, 1, 0, 0), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 8, 0, 18) };
    }

    private FrameworkElement Image(LinkInline imageLink)
    {
        string alt = MarkdownService.PlainText(imageLink);
        string address = imageLink.GetDynamicUrl?.Invoke() ?? imageLink.Url ?? "";
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 12), Spacing = 6 };
        var caption = new TextBlock { Text = string.IsNullOrEmpty(alt) ? Localization.Get("Image") : alt, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = _secondary };
        var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 600, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        var uri = LinkService.Resolve(address, _path);
        void Resize() { panel.MaxWidth = Math.Max(80, Math.Min(700, _host.ActualWidth - 110)); image.MaxWidth = panel.MaxWidth; caption.MaxWidth = panel.MaxWidth; }
        Resize();
        SizeChangedEventHandler onResize = (_, _) => Resize();
        _host.SizeChanged += onResize;
        _lifetime.Token.Register(() => _host.SizeChanged -= onResize);
        AutomationProperties.SetName(image, string.IsNullOrEmpty(alt) ? Localization.Get("DocumentImage") : alt);
        ToolTipService.SetToolTip(image, imageLink.Title ?? address);
        panel.Children.Add(image); panel.Children.Add(caption);
        if (uri is null) caption.Text += " · " + Localization.Get("UnresolvedImage");
        else if (LinkService.IsWeb(uri) && !_settings.RemoteImages) caption.Text += " · " + Localization.Get("RemoteImageDisabled");
        else if (uri.IsUnc || (!uri.IsFile && !LinkService.IsWeb(uri))) caption.Text += " · " + Localization.Get("UnsupportedImage");
        else _ = LoadImageAsync(uri, image, caption);
        return panel;
    }

    private async Task LoadImageAsync(Uri uri, Image image, TextBlock caption)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        bool entered = false;
        try
        {
            await _imageSlots.WaitAsync(token); entered = true;
            const long limit = 15 * 1024 * 1024;
            using var memory = new MemoryStream();
            string extension = Path.GetExtension(uri.AbsolutePath);
            bool svg = extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
            if (uri.IsFile)
            {
                await using var file = File.OpenRead(uri.LocalPath);
                if (file.Length > limit) throw new InvalidDataException(Localization.Get("ImageTooLarge"));
                await file.CopyToAsync(memory, token);
            }
            else
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException(Localization.Get("ImageTooLarge"));
                svg |= response.Content.Headers.ContentType?.MediaType == "image/svg+xml";
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    if (memory.Length + read > limit) throw new InvalidDataException(Localization.Get("ImageTooLarge"));
                    await memory.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            token.ThrowIfCancellationRequested(); memory.Position = 0;
            if (svg)
            {
                var source = new SvgImageSource();
                var status = await source.SetSourceAsync(memory.AsRandomAccessStream());
                if (status != SvgImageSourceLoadStatus.Success) throw new InvalidDataException(Localization.Get("UnsupportedSvg"));
                image.Source = source;
            }
            else
            {
                var source = new BitmapImage { DecodePixelWidth = 1600 };
                await source.SetSourceAsync(memory.AsRandomAccessStream()); image.Source = source;
            }
            if (!token.IsCancellationRequested) image.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) caption.Text += " · " + Localization.Get("ImageTimeout"); }
        catch (Exception ex) { if (!token.IsCancellationRequested) caption.Text += " · " + Localization.Format("ImageError", ex.Message); }
        finally { if (entered) _imageSlots.Release(); }
    }

    public List<FrameworkElement> Search(string query)
    {
        if (_virtualDocument is not null) return _virtualDocument.Search(query);
        var matches = new List<FrameworkElement>();
        foreach (var (element, text) in _searchable)
        {
            var collection = element switch { RichTextBlock rich => rich.TextHighlighters, TextBlock simple => simple.TextHighlighters, _ => null };
            collection?.Clear();
            if (string.IsNullOrWhiteSpace(query) || !text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(element);
            // TextPointer offsets include inline boundary markers. Highlight a rich block
            // by its actual content length, rather than guessing offsets from Markdown.
            if (element is RichTextBlock richText)
            {
                var highlighter = new TextHighlighter { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 190, 0)) };
                highlighter.Ranges.Add(new TextRange { StartIndex = 0, Length = Math.Max(1, richText.ContentEnd.Offset) });
                collection?.Add(highlighter);
            }
            else if (element is TextBlock)
            {
                var highlighter = new TextHighlighter { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 190, 0)) };
                for (int offset = 0; offset < text.Length;)
                {
                    int index = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    highlighter.Ranges.Add(new TextRange { StartIndex = index, Length = query.Length }); offset = index + query.Length;
                }
                collection?.Add(highlighter);
            }
        }
        return matches;
    }

    private void CopyText(string text)
    {
        try { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
        catch (Exception ex) { _report(Localization.Format("CopyError", ex.Message)); }
    }

    private static string FootnoteLabel(Footnote footnote) => footnote.Label?.TrimStart('^') ?? footnote.Order.ToString();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _virtualDocument?.Dispose();
        _virtualDocument = null;
        // Detaching a control is not enough: WinUI's native text/layout state can outlive
        // its managed wrapper. Release document content before dropping the visual tree.
        foreach (var (element, _) in _searchable)
        {
            if (element is RichTextBlock rich)
            {
                rich.ContextFlyout = null;
                rich.TextHighlighters.Clear();
                rich.Blocks.Clear();
            }
            else if (element is TextBlock text)
            {
                text.TextHighlighters.Clear();
                text.Text = "";
            }
        }
        _host.Children.Clear();
        _searchable.Clear();
        _anchors.Clear();
        _links.Clear();
        _lifetime.Dispose();
    }
}
