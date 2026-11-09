using Amoxtli.Editing;
using Amoxtli.Rendering;
using Amoxtli.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Amoxtli.Controls;

/// <summary>
/// The Format (WYSIWYG) editor: the flat paragraph model shown as native blocks, one rich text
/// field per paragraph, code block or table cell. Bullets, numbers and quote bars are drawn
/// beside the text, never typed. Every change serializes the model to canonical Markdown.
/// </summary>
internal sealed class FormatEditor : Grid
{
    private sealed class BlockView
    {
        public int First, Last;
        public BlockKind Kind;
        public RichField? Field;
        public FrameworkElement? Element;
    }

    private readonly StackPanel _panel = new() { MaxWidth = 920, Padding = new Thickness(40, 32, 40, 64), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<BlockView> _blocks = [];
    private readonly List<(RichField Field, BlockView Block, int Paragraph)> _order = [];
    private readonly List<NativeMarkdownRenderer> _renderers = [];
    private readonly EditHistory _history = new();
    private readonly ReaderSettings _settings;
    private readonly Func<string, bool, Task> _openLink;
    private readonly Action<string> _report;
    private List<FormatParagraph> _paragraphs = [FormatParagraph.Plain("")];
    private string _markdown = "";
    private string? _path;
    private EditorPalette _palette;
    private RichField? _focused;
    private bool _composing;

    public ScrollViewer Scroll { get; }
    public string Markdown => _markdown;
    public IReadOnlyList<FormatParagraph> Paragraphs => _paragraphs;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public MenuFlyout? ContextMenu { get; set; }
    public Func<VirtualKey, VirtualKeyModifiers, bool>? Shortcut { get; set; }
    /// <summary>After every change to <see cref="Markdown"/>.</summary>
    public event Action? Edited;
    public event Action? SelectionMoved;
    public event Action? FocusEntered;
    /// <summary>The blocks were rebuilt, so their positions changed.</summary>
    public event Action? Rebuilt;
    private int[] _offsets = [];

    public FormatEditor(ReaderSettings settings, Func<string, bool, Task> openLink, Action<string> report)
    {
        _settings = settings; _openLink = openLink; _report = report;
        Scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _panel };
        Children.Add(Scroll);
        FlowDirection = FlowDirection.LeftToRight;
        _palette = EditorPalette.For(ActualTheme);
        ActualThemeChanged += (_, _) => { _palette = EditorPalette.For(ActualTheme); Rebuild(); };
        SizeChanged += (_, args) => _panel.Padding = new Thickness(args.NewSize.Width < 800 ? 24 : 40, 32, args.NewSize.Width < 800 ? 24 : 40, 64);
    }

    /// <summary>
    /// Shows another document, or the same one changed elsewhere, keeping scroll and caret. Clears undo.
    /// With <paramref name="keepFocus"/>, the caret's field takes the keyboard focus back after the rebuild.
    /// </summary>
    public void Load(string markdown, string? path, bool keepFocus = false)
    {
        var position = Position;
        double offset = Scroll.VerticalOffset;
        _path = path;
        _paragraphs = FormatOperations.Ensure(FormatParser.Parse(markdown));
        SetMarkdown(markdown, null);
        _history.Clear();
        Rebuild();
        if (position is { } at) FocusParagraph(Math.Min(at.Paragraph, _paragraphs.Count - 1), at.Offset, keepFocus);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Scroll.ChangeView(null, offset, null, true));
    }

    /// <summary>The text and where each paragraph starts in it; read back from the parser unless the serializer gave them.</summary>
    private void SetMarkdown(string markdown, int[]? offsets)
    {
        _markdown = markdown;
        _offsets = offsets ?? _paragraphs.Select(p => p.SourceOffset < 0 ? markdown.Length : p.SourceOffset).ToArray();
    }

    /// <summary>Where each block starts in the Markdown, and its top in this view.</summary>
    public IEnumerable<(int Offset, double Top)> BlockTops()
    {
        foreach (var block in _blocks)
        {
            if (block.Element is not { } element || block.First >= _offsets.Length) continue;
            double top;
            try { top = element.TransformToVisual(_panel).TransformPoint(new Windows.Foundation.Point()).Y; }
            catch (ArgumentException) { continue; }
            yield return (_offsets[block.First], top);
        }
    }

    /// <summary>Releases fields and images, for example when the editor is hidden.</summary>
    public void Clear()
    {
        _markdown = "";
        _paragraphs = [FormatParagraph.Plain("")];
        _history.Clear();
        _focused = null;
        foreach (var renderer in _renderers) renderer.Dispose();
        _renderers.Clear();
        _blocks.Clear(); _order.Clear();
        _panel.Children.Clear();
    }

    public void Refresh() => Rebuild();

    // Building

    private void Rebuild()
    {
        foreach (var renderer in _renderers) renderer.Dispose();
        _renderers.Clear();
        _blocks.Clear(); _order.Clear();
        _panel.Children.Clear();
        _focused = null;
        var numbers = ItemNumbers();
        for (int i = 0; i < _paragraphs.Count;)
        {
            var p = _paragraphs[i];
            int end = i;
            if (p.Kind is BlockKind.Code or BlockKind.TableCell or BlockKind.Raw or BlockKind.Image or BlockKind.ThematicBreak)
                while (end + 1 < _paragraphs.Count && _paragraphs[end + 1].Kind == p.Kind && _paragraphs[end + 1].BlockId == p.BlockId && p.Kind is BlockKind.Code or BlockKind.TableCell) end++;
            var block = new BlockView { First = i, Last = end, Kind = p.Kind };
            FrameworkElement view = p.Kind switch
            {
                BlockKind.Code => CodeView(block),
                BlockKind.TableCell => TableView(block),
                BlockKind.Raw or BlockKind.Image or BlockKind.ThematicBreak => StaticView(p),
                _ => TextView(block)
            };
            _blocks.Add(block);
            block.Element = Wrap(i, view, numbers);
            SetDirection(block, DirectionOf(block, _blocks.Count - 1));
            _panel.Children.Add(block.Element);
            i = end + 1;
        }
        Rebuilt?.Invoke();
    }

    /// <summary>
    /// A paragraph, heading or table reads in the direction of its first letter, and its bullets and
    /// quote bars follow; one without letters keeps the direction of the block before it. Code and
    /// raw Markdown stay left to right.
    /// </summary>
    private FlowDirection DirectionOf(BlockView block, int position)
    {
        if (block.Kind is BlockKind.Code or BlockKind.Raw or BlockKind.Image or BlockKind.ThematicBreak) return FlowDirection.LeftToRight;
        for (int i = block.First; i <= block.Last; i++)
            if (TextDirection.FirstStrong(_paragraphs[i].Text) is { } rightToLeft)
                return rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        for (int before = position - 1; before >= 0; before--)
            if (_blocks[before].Kind is not (BlockKind.Code or BlockKind.Raw or BlockKind.Image or BlockKind.ThematicBreak) && _blocks[before].Element is { } previous)
                return previous.FlowDirection;
        return FlowDirection.LeftToRight;
    }

    /// <summary>After typing, the block turns around when its first letter changed direction.</summary>
    private void UpdateDirection(BlockView block)
    {
        if (block.Element is not { } element) return;
        var direction = DirectionOf(block, _blocks.IndexOf(block));
        if (element.FlowDirection != direction) SetDirection(block, direction);
    }

    /// <summary>The block's layout mirrors (bullets, quote bars, table columns), and its fields' text reads the same way.</summary>
    private void SetDirection(BlockView block, FlowDirection direction)
    {
        if (block.Element is { } element) element.FlowDirection = direction;
        foreach (var entry in _order)
            if (entry.Block == block && !entry.Field.Multiline) entry.Field.SetDirection(direction == FlowDirection.RightToLeft);
    }

    private RichField NewField(BlockView block, int paragraph, bool multiline, bool heading)
    {
        var field = new RichField(multiline, heading);
        field.Box.FontFamily = new FontFamily(multiline ? Typesetting.FontFamily(Typesetting.Monospace) : Typesetting.FontFamily(_settings.Typeface));
        field.Box.FontSize = multiline ? _settings.FontSize * .9 : _settings.FontSize;
        field.Box.ContextFlyout = ContextMenu;
        field.Changed += Field_Changed;
        field.Box.PreviewKeyDown += (_, e) => Field_KeyDown(field, e);
        field.Box.GotFocus += (_, _) => { _focused = field; FocusEntered?.Invoke(); SelectionMoved?.Invoke(); };
        field.Box.SelectionChanged += (_, _) => { if (_focused == field) SelectionMoved?.Invoke(); };
        field.Box.TextCompositionStarted += (_, _) => _composing = true;
        field.Box.TextCompositionEnded += (_, _) => { _composing = false; Field_Changed(field); };
        field.Box.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) => OpenLinkAt(field, e)), true);
        _order.Add((field, block, paragraph));
        return field;
    }

    private FrameworkElement TextView(BlockView block)
    {
        var p = _paragraphs[block.First];
        bool heading = p.Kind == BlockKind.Heading;
        var field = NewField(block, block.First, false, heading);
        block.Field = field;
        if (heading)
        {
            double[] sizes = [0, 2.0, 1.6, 1.3, 1.15, 1.05, 1.0];
            field.Box.FontSize = _settings.FontSize * sizes[Math.Clamp(p.Level, 1, 6)];
            field.Box.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            AutomationProperties.SetHeadingLevel(field.Box, (Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel)Math.Clamp(p.Level, 1, 6));
        }
        field.SetContent(p.Runs, _palette);
        return field.Box;
    }

    /// <summary>
    /// A code block: a strip with the Copy button and the language chip at the top right, then the code,
    /// colored by language.
    /// </summary>
    private FrameworkElement CodeView(BlockView block)
    {
        var field = NewField(block, block.First, true, false);
        block.Field = field;
        var lines = Enumerable.Range(block.First, block.Last - block.First + 1).Select(i => _paragraphs[i].Text);
        field.SetContent([new InlineRun(string.Join("\n", lines))], _palette);
        string info = _paragraphs[block.First].Info;
        field.WhenRealized(() => field.Colorize(CodeLexer.Detect(info)));
        var grid = new Grid { RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        tools.Children.Add(CopyButton(block));
        tools.Children.Add(LanguageChip(block, info));
        grid.Children.Add(tools);
        Grid.SetRow(field.Box, 1);
        grid.Children.Add(field.Box);
        return new Border
        {
            Child = grid,
            Background = new SolidColorBrush(EditorPalette.CodeSurface(ActualTheme)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 6, 8, 12),
            Margin = new Thickness(0, 4, 0, 8)
        };
    }

    /// <summary>The chip shows a hand cursor, like a link.</summary>
    internal sealed class LanguageButton : Button
    {
        public LanguageButton() => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
    }

    /// <summary>The language name with a chevron, or "Choose Language"; an unknown fence word shows as written.</summary>
    private Button LanguageChip(BlockView block, string info)
    {
        var language = CodeLexer.Detect(info);
        string word = CodeLexer.FirstWord(info);
        string label = language != CodeLanguage.None ? CodeLexer.Name(language) : word.Length > 0 ? word : Localization.Get("ChooseLanguage");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var chip = new LanguageButton
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(10, 2, 8, 3),
            MinHeight = 0,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            IsTabStop = true,
            Tag = block
        };
        AutomationProperties.SetName(chip, $"{Localization.Get("CodeLanguage")}: {label}");
        chip.Click += (_, _) => OpenLanguageMenu(chip);
        return chip;
    }

    /// <summary>The Copy button of a code block, styled like the language chip.</summary>
    internal sealed class CopyCodeButton : Button
    {
        public CopyCodeButton() => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
        public FontIcon Icon { get; } = new() { Glyph = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        public TextBlock Label { get; } = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        public Microsoft.UI.Dispatching.DispatcherQueueTimer? Restore { get; set; }
    }

    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1.5);

    private Button CopyButton(BlockView block)
    {
        var button = new CopyCodeButton
        {
            Padding = new Thickness(8, 2, 10, 3),
            MinHeight = 0,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            IsTabStop = true,
            Tag = block
        };
        button.Label.Text = Localization.Get("Copy");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(button.Icon);
        content.Children.Add(button.Label);
        button.Content = content;
        AutomationProperties.SetName(button, Localization.Get("CopyCode"));
        ToolTipService.SetToolTip(button, Localization.Get("CopyCode"));
        button.Click += (_, _) => CopyCode(button);
        return button;
    }

    /// <summary>
    /// Puts the whole block's code on the clipboard as plain text, without the fences, and shows
    /// "Copied" on the button for a moment.
    /// </summary>
    internal string? CopyCode(Button sender)
    {
        if (sender is not CopyCodeButton button || button.Tag is not BlockView block || block.Field is not { } field) return null;
        string code = field.ReadText();
        try
        {
            var data = new DataPackage();
            data.SetText(code.Replace("\n", "\r\n"));
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            _report(Localization.Format("CopyError", ex.Message));
            return null;
        }
        button.Icon.Glyph = "";
        button.Label.Text = Localization.Get("Copied");
        button.Restore ??= DispatcherQueue.CreateTimer();
        button.Restore.Interval = CopiedFeedback;
        button.Restore.IsRepeating = false;
        button.Restore.Tick += Reset;
        button.Restore.Start();
        return code;

        void Reset(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object _)
        {
            timer.Tick -= Reset;
            button.Icon.Glyph = "";
            button.Label.Text = Localization.Get("Copy");
        }
    }

    /// <summary>Clicking the chip moves the caret into its block when it was elsewhere, then opens the menu below it.</summary>
    internal void OpenLanguageMenu(Button chip)
    {
        if (chip.Tag is not BlockView block || block.Field is not { } field) return;
        if (_focused != field) field.WhenRealized(() => { field.Box.Focus(FocusState.Programmatic); field.Select(0); });
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var item in LanguageItems(block)) menu.Items.Add(item);
        menu.ShowAt(chip);
    }

    /// <summary>
    /// "None", a separator, then the known languages in alphabetical order; the current one is checked.
    /// An unknown fence word comes first, checked and disabled.
    /// </summary>
    internal List<MenuFlyoutItemBase> LanguageItems(int paragraph) =>
        _blocks.FirstOrDefault(b => b.Kind == BlockKind.Code && paragraph >= b.First && paragraph <= b.Last) is { } block ? LanguageItems(block) : [];

    private List<MenuFlyoutItemBase> LanguageItems(BlockView block)
    {
        string info = _paragraphs[block.First].Info;
        var current = CodeLexer.Detect(info);
        string word = CodeLexer.FirstWord(info);
        var items = new List<MenuFlyoutItemBase>();
        if (current == CodeLanguage.None && word.Length > 0)
        {
            items.Add(new ToggleMenuFlyoutItem { Text = word, IsChecked = true, IsEnabled = false });
            items.Add(new MenuFlyoutSeparator());
        }
        RadioMenuFlyoutItem Item(string text, CodeLanguage language)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "CodeLanguage", IsChecked = language == current && (language != CodeLanguage.None || word.Length == 0) };
            item.Click += (_, _) => SetLanguage(block.First, language);
            return item;
        }
        items.Add(Item(Localization.Get("NoLanguage"), CodeLanguage.None));
        items.Add(new MenuFlyoutSeparator());
        foreach (var language in CodeLexer.Known) items.Add(Item(CodeLexer.Name(language), language));
        return items;
    }

    /// <summary>
    /// Changes the language of the whole block holding <paramref name="paragraph"/>, as one undo step: the fence gets
    /// the language's word (the rest of the info string stays), or nothing for None.
    /// </summary>
    internal void SetLanguage(int paragraph, CodeLanguage language)
    {
        var p = _paragraphs[paragraph];
        if (p.Kind != BlockKind.Code) return;
        string rest = p.Info.Trim()[CodeLexer.FirstWord(p.Info).Length..].Trim();
        string info = language == CodeLanguage.None ? "" : (CodeLexer.Fence(language) + " " + rest).Trim();
        var result = _paragraphs.Select(x => x.Kind == BlockKind.Code && x.BlockId == p.BlockId ? x with { Info = info } : x).ToList();
        var position = Position is { } at && at.Paragraph >= 0 ? at : (paragraph, 0);
        Restructure(new FormatOperations.Result(result, position.Item1, position.Item2));
    }

    /// <summary>The code block holding the caret, for the context menu's Language submenu.</summary>
    internal int? ActiveCodeParagraph => Position is { } at && _paragraphs[at.Paragraph].Kind == BlockKind.Code ? at.Paragraph : null;

    private FrameworkElement TableView(BlockView block)
    {
        var cells = Enumerable.Range(block.First, block.Last - block.First + 1).ToList();
        int columns = cells.Max(i => _paragraphs[i].Column) + 1, rows = cells.Max(i => _paragraphs[i].Row) + 1;
        // A rich edit box sized by an Auto column only reports its minimum width, and its text arrives
        // after the first layout; star columns measure their cells narrower than they end up, which
        // leaves rows too tall. Columns get fixed widths measured from their text (kept as MaxWidth),
        // scaled down together when the table is wider than the space it has.
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var host = new Border { Child = grid, Margin = new Thickness(0, 6, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = TableColumnMinimum });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var widths = new double[columns, rows];
        void Layout()
        {
            // Columns that reach their minimum stop shrinking; the others share what is left.
            double room = host.ActualWidth, scale = 1;
            for (int pass = 0; pass < columns && room > 0; pass++)
            {
                double pinned = 0, flexible = 0;
                foreach (var definition in grid.ColumnDefinitions)
                    if (definition.MaxWidth * scale <= TableColumnMinimum) pinned += TableColumnMinimum; else flexible += definition.MaxWidth;
                if (flexible == 0 || pinned + flexible * scale <= room) break;
                scale = Math.Max(0, room - pinned) / flexible;
            }
            bool changed = false;
            foreach (var definition in grid.ColumnDefinitions)
            {
                double width = Math.Floor(Math.Max(TableColumnMinimum, definition.MaxWidth * scale));
                if (definition.Width.Value != width || !definition.Width.IsAbsolute) { definition.Width = new GridLength(width); changed = true; }
            }
            // A rich edit box measures its height from the line layout of its previous width, so after
            // a column widens its row keeps the height of the wrapped text. Measuring again once the new
            // width is arranged brings the row back to its text.
            if (changed)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    foreach (var child in grid.Children) if (child is Border { Child: RichEditBox box }) box.InvalidateMeasure();
                });
        }
        void Fit(int column)
        {
            double natural = TableColumnMinimum;
            for (int r = 0; r < rows; r++) natural = Math.Max(natural, widths[column, r]);
            grid.ColumnDefinitions[column].MaxWidth = natural;
        }
        host.SizeChanged += (_, args) => { if (args.NewSize.Width != args.PreviousSize.Width) Layout(); };
        var stroke = new SolidColorBrush(EditorPalette.TableLineSurface(ActualTheme));
        var headerSurface = new SolidColorBrush(EditorPalette.TableHeaderSurface(ActualTheme));
        foreach (int i in cells)
        {
            var p = _paragraphs[i];
            bool header = p.Row == 0;
            var field = NewField(block, i, false, header);
            if (header) field.Box.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var alignment = p.Column < p.Alignments.Count ? p.Alignments[p.Column] : TableAlignment.None;
            field.KeepsAlignment = alignment != TableAlignment.None;
            field.Box.TextAlignment = alignment switch
            {
                TableAlignment.Center => TextAlignment.Center,
                TableAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left
            };
            field.SetContent(p.Runs, _palette);
            var cell = new Border
            {
                Child = field.Box,
                Padding = new Thickness(12, 8, 12, 8),
                BorderBrush = stroke,
                // The whole grid is drawn, outer edges included.
                BorderThickness = new Thickness(p.Column == 0 ? 1 : 0, p.Row == 0 ? 1 : 0, 1, 1),
                Background = header ? headerSurface : null
            };
            Grid.SetRow(cell, p.Row); Grid.SetColumn(cell, p.Column);
            grid.Children.Add(cell);
            int column = p.Column, row = p.Row;
            widths[column, row] = CellWidth(string.Concat(p.Runs.Select(run => run.Text)), field.Box, cell.Padding);
            field.Changed += changed =>
            {
                changed.Box.Document.GetText(TextGetOptions.UseLf, out string text);
                widths[column, row] = CellWidth(text, changed.Box, cell.Padding);
                Fit(column);
                Layout();
            };
        }
        for (int c = 0; c < columns; c++) Fit(c);
        Layout();
        block.Field = _order.FirstOrDefault(entry => entry.Block == block).Field;
        return host;
    }

    private const double TableColumnMinimum = 72;

    /// <summary>The width a cell needs to show its longest line unwrapped, caret and bold runs included.</summary>
    internal static double CellWidth(string text, RichEditBox like, Thickness padding)
    {
        double widest = 0;
        foreach (string line in text.Split('\n', '\r', '\v'))
        {
            if (line.Length == 0) continue;
            var probe = new TextBlock { Text = line, FontSize = like.FontSize, FontFamily = like.FontFamily, FontWeight = like.FontWeight };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, probe.DesiredSize.Width);
        }
        return Math.Ceiling(widest * 1.06 + 8 + padding.Left + padding.Right);
    }

    /// <summary>Raw Markdown and images keep the reader's presentation; they are not edited here.</summary>
    private FrameworkElement StaticView(FormatParagraph p)
    {
        if (p.Kind == BlockKind.ThematicBreak)
            return new Border { Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Margin = new Thickness(0, 18, 0, 18) };
        var host = new StackPanel();
        var renderer = new NativeMarkdownRenderer(host, _settings, _path, _openLink, _report);
        renderer.Render(MarkdownService.Parse(p.Source));
        _renderers.Add(renderer);
        return host;
    }

    /// <summary>Quote bars and list markers in columns beside the content.</summary>
    private FrameworkElement Wrap(int index, FrameworkElement content, Dictionary<int, int> numbers)
    {
        var p = _paragraphs[index];
        bool listItem = p.Containers.Count > 0 && !p.Containers[^1].IsQuote;
        var grid = new Grid { Padding = new Thickness(0, p.Kind == BlockKind.Heading && index > 0 ? 14 : 2, 0, listItem ? 2 : p.Kind is BlockKind.Paragraph or BlockKind.Heading ? 8 : 4) };
        int column = 0, depth = 0;
        foreach (var c in p.Containers)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement gutter;
            if (c.IsQuote)
                gutter = new Border { Width = 3, Margin = new Thickness(2, 0, 14, 0), Background = new SolidColorBrush(_palette.Accent), VerticalAlignment = VerticalAlignment.Stretch };
            else if (!FormatOperations.StartsItem(_paragraphs, index, c))
                gutter = new Border { Width = 28 };
            else if (c.Task is { } done)
            {
                var box = new CheckBox { IsChecked = done, MinWidth = 28, Width = 28, MinHeight = 24, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
                AutomationProperties.SetName(box, Localization.Get(done ? "TaskDone" : "TaskPending"));
                box.Click += (_, _) => SetTask(c, box.IsChecked == true);
                gutter = box;
            }
            else
            {
                string marker = c.Ordered ? $"{c.Start + numbers.GetValueOrDefault(c.Id)}." : (depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
                gutter = new TextBlock
                {
                    Text = marker, Width = 28, TextAlignment = TextAlignment.Right, Padding = new Thickness(0, 0, 8, 0),
                    FontFamily = new FontFamily(Typesetting.FontFamily(_settings.Typeface)), FontSize = _settings.FontSize,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Top
                };
                // A screen reader announces the bullet or number and the level.
                AutomationProperties.SetName(content, $"{marker} {Localization.Format("ListLevel", depth + 1)}");
            }
            if (!c.IsQuote) depth++;
            Grid.SetColumn(gutter, column++);
            grid.Children.Add(gutter);
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(content, column);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>Each ordered item's position in its list, so numbering follows edits.</summary>
    private Dictionary<int, int> ItemNumbers()
    {
        var numbers = new Dictionary<int, int>();
        var counts = new Dictionary<int, int>();
        foreach (var c in _paragraphs.SelectMany(p => p.Containers).Where(c => !c.IsQuote))
            if (!numbers.ContainsKey(c.Id)) numbers[c.Id] = counts[c.ListId] = counts.GetValueOrDefault(c.ListId, -1) + 1;
        return numbers;
    }

    // Editing

    private (BlockView Block, int Paragraph)? Locate(RichField field)
    {
        foreach (var entry in _order) if (entry.Field == field) return (entry.Block, entry.Paragraph);
        return null;
    }

    private void Field_Changed(RichField field)
    {
        if (_composing || Locate(field) is not { } at) return;
        var (block, index) = at;
        if (block.Kind == BlockKind.Code)
        {
            var template = _paragraphs[block.First];
            var lines = field.ReadText().Split('\n');
            if (lines is [""])
            {
                Restructure(new FormatOperations.Result(Replace(block.First, block.Last, [FormatParagraph.Plain("", template.Containers)]), block.First, 0));
                return;
            }
            var code = lines.Select(line => template with { Runs = line.Length == 0 ? [] : [new InlineRun(line)] }).ToList();
            int delta = code.Count - (block.Last - block.First + 1);
            _paragraphs = Replace(block.First, block.Last, code);
            Shift(block, delta);
            field.Colorize(CodeLexer.Detect(template.Info));
            Commit(true);
            return;
        }
        var p = _paragraphs[index] with { Runs = field.ReadRuns() };
        if (p.Kind == BlockKind.TableCell)
        {
            _paragraphs[index] = p;
            UpdateDirection(block);
            Commit(true);
            return;
        }
        int caret = field.Selection.Start;
        if (FormatOperations.TypedEscapedHash(p) is { } literal) { Restructure(Swap(index, literal), index, Math.Max(0, caret - 1)); return; }
        if (FormatOperations.TypedHeading(p) is { } heading) { Restructure(Swap(index, heading), index, Math.Max(0, caret - p.Text.Length + heading.Text.Length)); return; }
        if (FormatOperations.Emptied(p) is { } plain) { Restructure(Swap(index, plain), index, 0); return; }
        _paragraphs[index] = p;
        UpdateDirection(block);
        Commit(true);
    }

    private List<FormatParagraph> Swap(int index, FormatParagraph p)
    {
        var result = _paragraphs.ToList();
        result[index] = p;
        return result;
    }

    private List<FormatParagraph> Replace(int first, int last, List<FormatParagraph> paragraphs)
    {
        var result = _paragraphs.ToList();
        result.RemoveRange(first, last - first + 1);
        result.InsertRange(first, paragraphs);
        return result;
    }

    /// <summary>A code block gained or lost lines: later blocks move by <paramref name="delta"/> paragraphs.</summary>
    private void Shift(BlockView changed, int delta)
    {
        if (delta == 0) return;
        changed.Last += delta;
        foreach (var block in _blocks.Where(b => b.First > changed.First)) { block.First += delta; block.Last += delta; }
        for (int i = 0; i < _order.Count; i++)
            if (_order[i].Block != changed && _order[i].Block.First > changed.First)
                _order[i] = (_order[i].Field, _order[i].Block, _order[i].Paragraph + delta);
    }

    private void Restructure(List<FormatParagraph> paragraphs, int focus, int offset) => Restructure(new FormatOperations.Result(paragraphs, focus, offset));

    private void Restructure(FormatOperations.Result result)
    {
        _paragraphs = FormatOperations.Ensure(result.Paragraphs);
        Rebuild();
        FocusParagraph(Math.Clamp(result.Focus, 0, _paragraphs.Count - 1), result.Offset);
        Commit(false);
    }

    private void Commit(bool typing)
    {
        var offsets = new int[_paragraphs.Count];
        string markdown = FormatSerializer.Serialize(_paragraphs, offsets);
        if (markdown == _markdown) { _offsets = offsets; return; }
        _history.Record(_markdown, TextDiff.Compute(_markdown, markdown), typing);
        SetMarkdown(markdown, offsets);
        Edited?.Invoke();
    }

    public void Undo() { if (_history.Undo() is { } edit) Restore(edit); }
    public void Redo() { if (_history.Redo() is { } edit) Restore(edit); }

    private void Restore(TextEdit edit)
    {
        int focus = Position?.Paragraph ?? 0;
        string markdown = edit.Apply(_markdown);
        _paragraphs = FormatOperations.Ensure(FormatParser.Parse(markdown));
        SetMarkdown(markdown, null);
        Rebuild();
        FocusParagraph(Math.Min(focus, _paragraphs.Count - 1), 0);
        Edited?.Invoke();
    }

    private void SetTask(Container item, bool done)
    {
        var updated = item with { Task = done };
        _paragraphs = _paragraphs.Select(p => p with { Containers = p.Containers.Select(c => c == item ? updated : c).ToList() }).ToList();
        Rebuild();
        Commit(false);
    }

    private void Field_KeyDown(RichField field, KeyRoutedEventArgs e)
    {
        if (Locate(field) is not { } at) return;
        var (block, index) = at;
        var modifiers = SourceEditor.CurrentModifiers();
        bool control = modifiers.HasFlag(VirtualKeyModifiers.Control), shift = modifiers.HasFlag(VirtualKeyModifiers.Shift), alt = modifiers.HasFlag(VirtualKeyModifiers.Menu);
        var (start, length) = field.Selection;
        int total = field.Length;
        if (control && !alt)
        {
            if (e.Key == VirtualKey.Z) { if (shift) Redo(); else Undo(); e.Handled = true; }
            else if (e.Key == VirtualKey.Y && !shift) { Redo(); e.Handled = true; }
            else if (Shortcut?.Invoke(e.Key, modifiers) == true) e.Handled = true;
            else if (e.Key is VirtualKey.E or VirtualKey.L or VirtualKey.R or VirtualKey.J or VirtualKey.Number1 or VirtualKey.Number2
                or VirtualKey.Number5 or (VirtualKey)187 or (VirtualKey)189 or (VirtualKey)190 or (VirtualKey)188 || (shift && e.Key == VirtualKey.A))
                e.Handled = true;
            return;
        }
        if (alt) return;
        switch (e.Key)
        {
            case VirtualKey.Enter when block.Kind == BlockKind.TableCell:
                e.Handled = true;
                MoveInTable(index, 1, 0);
                break;
            case VirtualKey.Enter when block.Kind != BlockKind.Code && !shift:
                e.Handled = true;
                Return(field);
                break;
            case VirtualKey.Back when length == 0 && start == 0 && block.Kind is BlockKind.Paragraph or BlockKind.Heading:
                if (FormatOperations.Backspace(_paragraphs, index) is { } back) { e.Handled = true; Restructure(back); }
                break;
            case VirtualKey.Delete when length == 0 && start == total && block.Kind is BlockKind.Paragraph or BlockKind.Heading:
                if (FormatOperations.DeleteForward(_paragraphs, index) is { } forward) { e.Handled = true; Restructure(forward); }
                break;
            case VirtualKey.Tab when block.Kind == BlockKind.TableCell:
                e.Handled = true;
                MoveInTable(index, 0, shift ? -1 : 1);
                break;
            case VirtualKey.Tab when _paragraphs[index].Containers is [.., { IsQuote: false }]:
                e.Handled = true;
                if ((shift ? FormatOperations.Outdent(_paragraphs, index, start) : FormatOperations.Indent(_paragraphs, index, start)) is { } nested) Restructure(nested);
                break;
            case VirtualKey.Up when length == 0 && field.CaretOnFirstLine():
                if (MoveFocus(field, -1, int.MaxValue)) e.Handled = true;
                break;
            case VirtualKey.Down when length == 0 && field.CaretOnLastLine():
                if (MoveFocus(field, 1, 0)) e.Handled = true;
                break;
            case VirtualKey.Left when length == 0 && start == 0:
                if (MoveFocus(field, -1, int.MaxValue)) e.Handled = true;
                break;
            case VirtualKey.Right when length == 0 && start == total:
                if (MoveFocus(field, 1, 0)) e.Handled = true;
                break;
        }
    }

    internal IReadOnlyList<RichField> Fields => _order.Select(entry => entry.Field).ToList();

    /// <summary>Return in a paragraph, heading or list item: the selection goes, then the paragraph splits at the caret.</summary>
    internal void Return(RichField field)
    {
        if (Locate(field) is not { } at || at.Block.Kind is BlockKind.Code or BlockKind.TableCell) return;
        if (field.Selection.Length > 0) field.Insert("", InlineStyle.None, null);
        Restructure(FormatOperations.Split(_paragraphs, at.Paragraph, field.Selection.Start));
    }

    /// <summary>Backspace with the caret at the start of a paragraph. False when the key should do its usual work.</summary>
    internal bool BackspaceAtStart(RichField field)
    {
        if (Locate(field) is not { } at || FormatOperations.Backspace(_paragraphs, at.Paragraph) is not { } result) return false;
        Restructure(result);
        return true;
    }

    internal bool Nest(RichField field, bool outdent)
    {
        if (Locate(field) is not { } at) return false;
        var result = outdent ? FormatOperations.Outdent(_paragraphs, at.Paragraph, field.Selection.Start) : FormatOperations.Indent(_paragraphs, at.Paragraph, field.Selection.Start);
        if (result is null) return false;
        Restructure(result);
        return true;
    }

    private bool MoveFocus(RichField field, int direction, int offset)
    {
        int position = _order.FindIndex(entry => entry.Field == field) + direction;
        if (position < 0 || position >= _order.Count) return false;
        var target = _order[position].Field;
        target.WhenRealized(() =>
        {
            target.Box.Focus(FocusState.Keyboard);
            target.Select(Math.Min(offset, target.Length));
            target.Box.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        });
        return true;
    }

    private void MoveInTable(int index, int rows, int columns)
    {
        var p = _paragraphs[index];
        var cells = _order.Where(entry => _paragraphs[entry.Paragraph] is { Kind: BlockKind.TableCell } cell && cell.BlockId == p.BlockId).ToList();
        int width = cells.Max(entry => _paragraphs[entry.Paragraph].Column) + 1;
        int position = p.Row * width + p.Column + rows * width + columns;
        if (position >= 0 && position < cells.Count) FocusParagraph(cells[position].Paragraph, 0);
        else if (position >= cells.Count && MoveFocus(cells[^1].Field, 1, 0)) { }
    }

    /// <summary>The caret: a paragraph index and an offset in it.</summary>
    public (int Paragraph, int Offset)? Position
    {
        get
        {
            if (_focused is null || Locate(_focused) is not { } at) return null;
            int caret = _focused.Selection.Start;
            if (at.Block.Kind != BlockKind.Code) return (at.Paragraph, caret);
            var lines = _focused.ReadText()[..caret].Split('\n');
            return (at.Block.First + lines.Length - 1, lines[^1].Length);
        }
    }

    public void FocusParagraph(int paragraph, int offset, bool keyboard = true)
    {
        foreach (var (field, block, index) in _order)
        {
            if (block.Kind == BlockKind.Code ? paragraph < block.First || paragraph > block.Last : index != paragraph) continue;
            int caret = block.Kind != BlockKind.Code ? offset
                : Enumerable.Range(block.First, paragraph - block.First).Sum(i => _paragraphs[i].Text.Length + 1) + offset;
            field.WhenRealized(() =>
            {
                if (keyboard) field.Box.Focus(FocusState.Programmatic);
                field.Select(caret);
                field.Box.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
            return;
        }
    }

    private void OpenLinkAt(RichField field, PointerRoutedEventArgs e)
    {
        if (!SourceEditor.CurrentModifiers().HasFlag(VirtualKeyModifiers.Control)) return;
        // Rich text measures in physical pixels; the pointer is in effective pixels.
        var point = e.GetCurrentPoint(field.Box).Position;
        double scale = field.Box.XamlRoot?.RasterizationScale ?? 1;
        var range = field.Box.Document.GetRangeFromPoint(new Windows.Foundation.Point(point.X * scale, point.Y * scale), PointOptions.ClientCoordinates);
        int at = range.StartPosition;
        if (field.ReadRuns(at, at + 1) is [{ Style: var style, Url: { } url }] && style.HasFlag(InlineStyle.Link))
        {
            e.Handled = true;
            _ = _openLink(url, false);
        }
    }

    // Tools

    public RichField? ActiveField => _focused is not null && Locate(_focused) is not null ? _focused : null;

    public FormatState GetState()
    {
        var active = new HashSet<MarkdownTool>();
        if (ActiveField is not { } field || Position is not { } position) return FormatState.None;
        var p = _paragraphs[position.Paragraph];
        if (p.Kind == BlockKind.Code) active.Add(MarkdownTool.CodeBlock);
        else
        {
            var styles = field.ActiveStyles();
            foreach (var (tool, style) in InlineTools) if (styles.HasFlag(style)) active.Add(tool);
        }
        if (p.Containers.Any(c => c.IsQuote)) active.Add(MarkdownTool.Quote);
        if (p.Containers is [.., { IsQuote: false } item]) active.Add(item.Ordered ? MarkdownTool.NumberedList : MarkdownTool.BulletList);
        return new FormatState(p.Kind == BlockKind.Heading ? p.Level : p.Kind == BlockKind.Paragraph ? 0 : -1, active);
    }

    private static readonly (MarkdownTool Tool, InlineStyle Style)[] InlineTools =
    [
        (MarkdownTool.Bold, InlineStyle.Bold), (MarkdownTool.Italic, InlineStyle.Italic), (MarkdownTool.Underline, InlineStyle.Underline),
        (MarkdownTool.Strikethrough, InlineStyle.Strikethrough), (MarkdownTool.Code, InlineStyle.Code), (MarkdownTool.Link, InlineStyle.Link)
    ];

    /// <summary>A tool on the last focused field. False when the tool needs a dialog (a new link).</summary>
    public bool Toggle(MarkdownTool tool)
    {
        if (ActiveField is not { } field || Position is not { } position) return true;
        var (paragraph, offset) = position;
        switch (tool)
        {
            case MarkdownTool.Quote: Restructure(FormatOperations.ToggleQuote(_paragraphs, paragraph, offset)); return true;
            case MarkdownTool.BulletList: Restructure(FormatOperations.ToggleList(_paragraphs, paragraph, offset, false)); return true;
            case MarkdownTool.NumberedList: Restructure(FormatOperations.ToggleList(_paragraphs, paragraph, offset, true)); return true;
            case MarkdownTool.CodeBlock: Restructure(FormatOperations.ToggleCode(_paragraphs, paragraph, offset)); return true;
        }
        if (_paragraphs[paragraph].Kind == BlockKind.Code) return true;
        var style = InlineTools.First(entry => entry.Tool == tool).Style;
        if (tool == MarkdownTool.Link && !field.ActiveStyles().HasFlag(InlineStyle.Link)) return false;
        if (field.Selection.Length == 0)
        {
            if (tool == MarkdownTool.Link) { SelectRunAtCaret(field, InlineStyle.Link); field.ToggleStyle(InlineStyle.Link); return true; }
            // No selection: the style applies to what is typed next.
            var format = field.Box.Document.Selection.CharacterFormat;
            switch (style)
            {
                case InlineStyle.Bold: format.Bold = FormatEffect.Toggle; break;
                case InlineStyle.Italic: format.Italic = FormatEffect.Toggle; break;
                case InlineStyle.Underline: format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None; break;
                case InlineStyle.Strikethrough: format.Strikethrough = FormatEffect.Toggle; break;
                case InlineStyle.Code:
                    bool code = format.Name == SourceEditor.MonospaceFont;
                    format.Name = code ? field.Box.FontFamily.Source.Split(',')[0].Trim() : SourceEditor.MonospaceFont;
                    format.BackgroundColor = code ? Microsoft.UI.Colors.Transparent : _palette.CodeBackground;
                    break;
            }
            SelectionMoved?.Invoke();
            return true;
        }
        field.ToggleStyle(style);
        return true;
    }

    private static void SelectRunAtCaret(RichField field, InlineStyle style)
    {
        var runs = field.ReadRuns();
        int caret = field.Selection.Start, position = 0;
        foreach (var run in runs)
        {
            int end = position + run.Text.Length;
            if (run.Style.HasFlag(style) && caret >= position && caret <= end) { field.Select(position, run.Text.Length); return; }
            position = end;
        }
    }

    public string SelectedText => ActiveField is { } field ? field.ReadText().Substring(field.Selection.Start, field.Selection.Length) : "";

    public void InsertLink(string text, string url)
    {
        if (ActiveField is not { } field) return;
        if (field.Selection.Length > 0 && field.ReadText().Substring(field.Selection.Start, field.Selection.Length) == text) field.ToggleStyle(InlineStyle.Link, url);
        else field.Insert(string.IsNullOrWhiteSpace(text) ? url : text, InlineStyle.Link, url);
    }

    public void SetHeading(int level)
    {
        if (Position is { } position) Restructure(FormatOperations.SetHeading(_paragraphs, position.Paragraph, position.Offset, level));
    }

    public void InsertTable(int columns, int rows)
    {
        if (Position is { } position) Restructure(FormatOperations.InsertTable(_paragraphs, position.Paragraph, columns, rows));
    }

    public void InsertRule()
    {
        if (Position is { } position) Restructure(FormatOperations.InsertRule(_paragraphs, position.Paragraph));
    }

    // Reading

    public List<FrameworkElement> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return _order.Where(entry => entry.Field.ReadText().Contains(query, StringComparison.CurrentCultureIgnoreCase) || !entry.Field.IsRealized
                && _paragraphs[entry.Paragraph].Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(entry => (FrameworkElement)entry.Field.Box).ToList();
    }

    public void ShowMatch(FrameworkElement element, string query)
    {
        var entry = _order.FirstOrDefault(item => item.Field.Box == element);
        if (entry.Field is not { } field) return;
        field.WhenRealized(() =>
        {
            int at = field.ReadText().IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (at >= 0) field.Select(at, query.Length);
            field.Box.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.2 });
        });
    }

    /// <summary>The n-th heading of the document, counting as the outline does.</summary>
    public FrameworkElement? Heading(int index)
    {
        int count = 0;
        foreach (var entry in _order)
        {
            if (entry.Block.Kind != BlockKind.Heading) continue;
            if (count++ == index) return entry.Field.Box;
        }
        return null;
    }
}
