using Amoxtli.Editing;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Amoxtli.Controls;

/// <summary>
/// Markdown source in a plain-text <see cref="RichEditBox"/>. Highlighting is character formatting
/// applied to the lines an edit touches; undo is text-only, so styling never becomes an undo step.
/// </summary>
internal sealed class SourceEditor : Grid
{
    private const int StyleBatch = 600;
    /// <summary>Per-run font names must be static fonts: variable Cascadia Mono does not resolve in rich text.</summary>
    internal const string MonospaceFont = "Consolas";
    private readonly SourceHighlighter _highlighter = new();
    private readonly EditHistory _history = new();
    private readonly DispatcherQueueTimer _styleTimer;
    private string _text = "";
    private bool _suppress, _contentChanging, _composing, _realized;
    private int _styledLines, _pendingFirst = -1, _pendingLast = -1;
    private EditorPalette _palette;
    private string _fontFamily = "Segoe UI";
    private double _fontSize = 16;


    public RichEditBox Box { get; }
    public string Text => _text;
    public SourceHighlighter Highlighter => _highlighter;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    /// <summary>After every change to <see cref="Text"/>: typing, paste, a tool, undo or redo.</summary>
    public event Action<TextEdit>? Edited;
    public event Action? SelectionMoved;
    /// <summary>Ctrl shortcuts, routed here before the text box applies its own rich-text formatting.</summary>
    public Func<VirtualKey, VirtualKeyModifiers, bool>? Shortcut { get; set; }

    public SourceEditor()
    {
        Box = new RichEditBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = true,
            IsTextPredictionEnabled = false,
            DisabledFormattingAccelerators = DisabledFormattingAccelerators.All,
            ClipboardCopyFormat = RichEditClipboardFormat.PlainText,
            SelectionFlyout = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(32, 24, 32, 48),
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            Box.Resources[key] = clear;
        // Undo is ours; the text box's own history would also record styling.
        try { Box.Document.UndoLimit = 0; } catch (ArgumentException) { }
        Box.TextChanging += (_, args) => { if (!_suppress && args.IsContentChanging) _contentChanging = true; };
        Box.TextChanged += Box_TextChanged;
        Box.SelectionChanged += (_, _) => { if (!_suppress) SelectionMoved?.Invoke(); };
        Box.Paste += Box_Paste;
        Box.PreviewKeyDown += Box_PreviewKeyDown;
        Box.TextCompositionStarted += (_, _) => _composing = true;
        Box.TextCompositionEnded += (_, _) => { _composing = false; StylePending(); };
        Box.ActualThemeChanged += (_, _) => { _palette = EditorPalette.For(Box.ActualTheme); RestyleAll(); };
        Box.SizeChanged += (_, args) => { if (!_realized && args.NewSize.Width > 0) { _realized = true; Reload(); } };
        Children.Add(Box);
        _styleTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _styleTimer.Interval = TimeSpan.FromMilliseconds(15);
        _styleTimer.Tick += (_, _) => StyleNextBatch();
        _palette = EditorPalette.For(Box.ActualTheme);
    }

    public (int Start, int Length) Selection
    {
        get
        {
            var selection = Box.Document.Selection;
            int start = Math.Clamp(selection.StartPosition, 0, _text.Length), end = Math.Clamp(selection.EndPosition, start, _text.Length);
            return (start, end - start);
        }
    }

    /// <summary>Shows a new document. Clears undo; styles the first lines now and the rest in the background.</summary>
    public void Load(string text, int caret = 0)
    {
        _text = text;
        _history.Clear();
        _suppress = true;
        try { Box.Document.SetText(TextSetOptions.None, ToBox(text)); }
        finally { _suppress = false; }
        _contentChanging = false;
        _highlighter.Reset(text);
        RestyleAll();
        Select(Math.Min(caret, text.Length), 0, false);
    }

    /// <summary>
    /// Font names applied before the text box is first laid out are dropped (other formatting
    /// survives), and it starts hidden, so the text is set and styled again once it has a size.
    /// </summary>
    private void Reload()
    {
        // Keep typing the text box has not reported yet.
        if (_contentChanging) Box_TextChanged(Box, new RoutedEventArgs());
        var (start, length) = Selection;
        _suppress = true;
        try { Box.Document.SetText(TextSetOptions.None, ToBox(_text)); }
        finally { _suppress = false; }
        _contentChanging = false;
        RestyleAll();
        Select(start, length, false);
    }

    public void SetTypography(string fontFamily, double fontSize)
    {
        if (_fontFamily == fontFamily && _fontSize == fontSize) return;
        _fontFamily = fontFamily; _fontSize = fontSize;
        RestyleAll();
    }

    /// <summary>A programmatic edit: a formatting tool, Return in a list, paste, undo or redo.</summary>
    public void Apply(TextEdit edit, bool typing = false) => Apply(edit, typing, true);

    /// <summary>
    /// The text changed in the other editor: apply only the difference, keeping caret, scroll and
    /// highlighting. Its own undo steps no longer match the text, so they are dropped.
    /// </summary>
    public void Replace(string text)
    {
        if (text == _text) return;
        var edit = TextDiff.Compute(_text, text);
        var (start, length) = Selection;
        _suppress = true;
        try { Box.Document.GetRange(edit.Start, edit.Start + edit.Length).SetText(TextSetOptions.None, ToBox(edit.Text)); }
        finally { _suppress = false; }
        Commit(edit, text, false, false);
        _history.Clear();
        int Map(int position) => position <= edit.Start ? position : position >= edit.Start + edit.Length ? position + edit.Delta : edit.Start + edit.Text.Length;
        int newStart = Map(start);
        Select(newStart, Math.Max(0, Map(start + length) - newStart), false);
    }

    public void Undo() { if (_history.Undo() is { } edit) Apply(edit, false, false); }
    public void Redo() { if (_history.Redo() is { } edit) Apply(edit, false, false); }

    public void Select(int start, int length, bool scroll = true)
    {
        start = Math.Clamp(start, 0, _text.Length);
        int end = Math.Clamp(start + length, start, _text.Length);
        Box.Document.Selection.SetRange(start, end);
        if (scroll) Box.Document.Selection.ScrollIntoView(PointOptions.None);
    }

    public void ScrollToLine(int line)
    {
        line = Math.Clamp(line, 0, _highlighter.LineCount - 1);
        Select(_highlighter.LineStart(line), 0);
        Box.Focus(FocusState.Programmatic);
    }

    private ScrollViewer? _scroll;

    /// <summary>The text box's own scroll viewer, available once its template is applied.</summary>
    public ScrollViewer? Scroll => _scroll ??= Descendant<ScrollViewer>(Box);

    private double _rectUnits = 1;

    /// <summary>The top of the line holding <paramref name="offset"/>, in the scrolled content (the rectangle already is).</summary>
    public double TopOf(int offset)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        Box.Document.GetRange(offset, offset).GetRect(PointOptions.ClientCoordinates, out var rect, out _);
        return rect.Y / _rectUnits;
    }

    /// <summary>
    /// Rich text reports rectangles in effective pixels in a shown window but in physical pixels in some others
    /// (a hidden one): compare the end of the text with the scroll extent to tell which. Measuring the end also
    /// finishes the background layout, so the extent covers all the text.
    /// </summary>
    public void CalibrateUnits()
    {
        _rectUnits = 1;
        double end = TopOf(_text.Length);
        Scroll?.UpdateLayout();
        double extent = Scroll?.ExtentHeight ?? 0, scale = Box.XamlRoot?.RasterizationScale ?? 1;
        if (scale > 1 && extent > 0 && end > extent * (1 + scale) / 2) _rectUnits = scale;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is T match && node != root) return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    public void Cut() => Box.Document.Selection.Cut();
    public void Copy() => Box.Document.Selection.Copy();
    public void SelectAll() => Select(0, _text.Length, false);
    public async void Paste() => await PasteTextAsync();

    private void Apply(TextEdit edit, bool typing, bool record)
    {
        string text = edit.Apply(_text);
        _suppress = true;
        try { Box.Document.GetRange(edit.Start, edit.Start + edit.Length).SetText(TextSetOptions.None, ToBox(edit.Text)); }
        finally { _suppress = false; }
        Commit(edit, text, typing, record);
        Select(edit.SelectionStart, edit.SelectionLength);
        SelectionMoved?.Invoke();
    }

    private void Box_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_contentChanging) return;
        _contentChanging = false;
        string text = ReadBox();
        if (text == _text) return;
        Commit(TextDiff.Compute(_text, text, Box.Document.Selection.EndPosition), text, true, true);
    }

    private void Commit(TextEdit edit, string text, bool typing, bool record)
    {
        string before = _text;
        int lines = _highlighter.LineCount;
        _text = text;
        if (record) _history.Record(before, edit, typing);
        var (first, last) = _highlighter.Update(text, edit);
        if (first < _styledLines) _styledLines = Math.Clamp(_styledLines + _highlighter.LineCount - lines, first, _highlighter.LineCount);
        _pendingFirst = _pendingFirst < 0 ? first : Math.Min(_pendingFirst, first);
        _pendingLast = Math.Max(_pendingLast, last);
        // Restyling while an input method composes would disturb its candidate text.
        if (!_composing) StylePending();
        Edited?.Invoke(edit);
    }

    private void StylePending()
    {
        if (_pendingFirst < 0) return;
        int first = Math.Min(_pendingFirst, _highlighter.LineCount - 1), last = Math.Min(_pendingLast, _highlighter.LineCount - 1);
        _pendingFirst = _pendingLast = -1;
        StyleLines(first, last);
    }

    private void RestyleAll()
    {
        _styledLines = 0;
        _pendingFirst = _pendingLast = -1;
        // Setting these reapplies them to all text later, over the styling; only when they change.
        if (Box.FontFamily?.Source != _fontFamily) Box.FontFamily = new FontFamily(_fontFamily);
        if (Box.FontSize != _fontSize) Box.FontSize = _fontSize;
        StyleNextBatch();
    }

    private void StyleNextBatch()
    {
        int count = Math.Min(StyleBatch, _highlighter.LineCount - _styledLines);
        if (count > 0)
        {
            StyleLines(_styledLines, _styledLines + count - 1);
            _styledLines += count;
        }
        if (_styledLines < _highlighter.LineCount) { if (!_styleTimer.IsRunning) _styleTimer.Start(); }
        else _styleTimer.Stop();
    }

    private void StyleLines(int first, int last)
    {
        int start = _highlighter.LineStart(first), end = Math.Min(_text.Length, _highlighter.LineEnd(last) + 1);
        _suppress = true;
        Box.Document.BatchDisplayUpdates();
        try
        {
            var plain = Box.Document.GetRange(start, end).CharacterFormat;
            plain.Name = _fontFamily.Split(',')[0].Trim();
            plain.Size = (float)_fontSize;
            plain.Bold = FormatEffect.Off;
            plain.Italic = FormatEffect.Off;
            plain.Underline = UnderlineType.None;
            plain.Strikethrough = FormatEffect.Off;
            plain.ForegroundColor = _palette.Text;
            plain.BackgroundColor = Colors.Transparent;
            for (int line = first; line <= last; line++)
                foreach (var span in _highlighter.GetSpans(line)) StyleSpan(span);
        }
        finally
        {
            Box.Document.ApplyDisplayUpdates();
            _suppress = false;
        }
    }

    private void StyleSpan(HighlightSpan span)
    {
        var format = Box.Document.GetRange(span.Start, span.Start + span.Length).CharacterFormat;
        switch (span.Kind)
        {
            case HighlightKind.Heading:
                format.Size = (float)(_fontSize * span.Level switch { 1 => 1.6, 2 => 1.4, 3 => 1.25, 4 => 1.1, _ => 1.0 });
                format.Bold = FormatEffect.On;
                break;
            case HighlightKind.Syntax:
            case HighlightKind.Quote: format.ForegroundColor = _palette.Syntax; break;
            case HighlightKind.Bold: format.Bold = FormatEffect.On; break;
            case HighlightKind.Italic: format.Italic = FormatEffect.On; break;
            case HighlightKind.Underline: format.Underline = UnderlineType.Single; break;
            case HighlightKind.Strikethrough: format.Strikethrough = FormatEffect.On; break;
            case HighlightKind.Code:
            case HighlightKind.CodeBlock:
                format.Name = MonospaceFont;
                format.BackgroundColor = _palette.CodeBackground;
                break;
            case HighlightKind.LinkText:
            case HighlightKind.ListMarker: format.ForegroundColor = _palette.Accent; break;
        }
    }

    private void Box_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var modifiers = CurrentModifiers();
        bool control = modifiers.HasFlag(VirtualKeyModifiers.Control), shift = modifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (e.Key == VirtualKey.Enter && !control && !modifiers.HasFlag(VirtualKeyModifiers.Menu))
        {
            InsertReturn(shift);
            e.Handled = true;
        }
        else if (control && !modifiers.HasFlag(VirtualKeyModifiers.Menu))
        {
            if (e.Key == VirtualKey.Z) { if (shift) Redo(); else Undo(); e.Handled = true; }
            else if (e.Key == VirtualKey.Y && !shift) { Redo(); e.Handled = true; }
            else if (Shortcut?.Invoke(e.Key, modifiers) == true) e.Handled = true;
            // Rich-text shortcuts: alignment, line spacing, lists, case, sub/superscript.
            else if (e.Key is VirtualKey.E or VirtualKey.L or VirtualKey.R or VirtualKey.J or VirtualKey.Number1 or VirtualKey.Number2
                or VirtualKey.Number5 or (VirtualKey)187 or (VirtualKey)189 or (VirtualKey)190 or (VirtualKey)188 || (shift && e.Key == VirtualKey.A))
                e.Handled = true;
        }
    }

    /// <summary>Return continues a list or quote; Shift+Return is a plain line break (not a vertical tab).</summary>
    internal void InsertReturn(bool plain = false)
    {
        var (start, length) = Selection;
        var edit = plain ? null : SourceFormatter.ContinueOnReturn(_text, start, length, _highlighter);
        Apply(edit ?? new TextEdit(start, length, "\n", start + 1, 0), true);
    }

    private async void Box_Paste(object sender, TextControlPasteEventArgs e)
    {
        // Pasting is always plain text: no fonts, colors or objects in the source.
        e.Handled = true;
        await PasteTextAsync();
    }

    private async Task PasteTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            string text = (await content.GetTextAsync()).Replace("\r\n", "\n").Replace('\r', '\n');
            var (start, length) = Selection;
            Apply(new TextEdit(start, length, text, start + text.Length, 0));
        }
        catch (Exception) { }
    }

    internal string ReadBox()
    {
        // The text box's own final paragraph mark is not part of the text it returns.
        Box.Document.GetText(TextGetOptions.UseLf, out string text);
        return text;
    }

    private static string ToBox(string text) => text.Replace('\n', '\r');

    internal static VirtualKeyModifiers CurrentModifiers()
    {
        var result = VirtualKeyModifiers.None;
        bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        if (Down(VirtualKey.Control)) result |= VirtualKeyModifiers.Control;
        if (Down(VirtualKey.Shift)) result |= VirtualKeyModifiers.Shift;
        if (Down(VirtualKey.Menu)) result |= VirtualKeyModifiers.Menu;
        return result;
    }
}
