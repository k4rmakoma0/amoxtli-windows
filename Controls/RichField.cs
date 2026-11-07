using Amoxtli.Editing;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Amoxtli.Controls;

/// <summary>
/// One editable paragraph, code block or table cell of the Format editor: a borderless
/// <see cref="RichEditBox"/> whose character formats are the inline styles. Links and raw
/// Markdown are told apart by a kerning threshold no text reaches, which typing inherits.
/// </summary>
internal sealed class RichField
{
    // Kerning is kept in twips, so it tops out near 1638 points; real text sizes are far below both marks.
    private const float LinkBase = 500, RawMark = 1600;
    private readonly List<string> _urls = [];
    private readonly List<Action> _whenRealized = [];
    private IReadOnlyList<InlineRun> _runs = [];
    private bool _realized, _suppress, _contentChanging;
    private EditorPalette _palette;

    public RichEditBox Box { get; }
    /// <summary>A code block keeps paragraph marks as lines; elsewhere a forced line break is a vertical tab.</summary>
    public bool Multiline { get; }
    public bool Heading { get; }
    /// <summary>The user changed the text (typing, deleting, pasting), not a programmatic reset.</summary>
    public event Action<RichField>? Changed;

    public RichField(bool multiline, bool heading)
    {
        Multiline = multiline;
        Heading = heading;
        Box = new RichEditBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = !multiline,
            IsTextPredictionEnabled = false,
            DisabledFormattingAccelerators = DisabledFormattingAccelerators.All,
            ClipboardCopyFormat = RichEditClipboardFormat.PlainText,
            SelectionFlyout = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinHeight = 0,
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            Box.Resources[key] = clear;
        try { Box.Document.UndoLimit = 0; } catch (ArgumentException) { }
        Box.TextChanging += (_, args) => { if (!_suppress && args.IsContentChanging) _contentChanging = true; };
        Box.TextChanged += (_, _) =>
        {
            if (_suppress || !_contentChanging) return;
            _contentChanging = false;
            Changed?.Invoke(this);
        };
        Box.Paste += async (_, args) =>
        {
            args.Handled = true;
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text)) return;
                string text = (await content.GetTextAsync()).Replace("\r\n", "\n").Replace('\r', '\n');
                Box.Document.Selection.TypeText(text.Replace('\n', Multiline ? '\r' : '\v'));
            }
            catch (Exception) { }
        };
        // Font names set before the first layout are dropped, so content waits for a size.
        Box.SizeChanged += (_, args) =>
        {
            if (_realized || args.NewSize.Width <= 0) return;
            _realized = true;
            Apply();
            foreach (var action in _whenRealized) action();
            _whenRealized.Clear();
        };
        _palette = EditorPalette.For(ElementTheme.Default);
    }

    public bool IsRealized => _realized;
    public int Length => ReadText().Length;

    public void SetContent(IReadOnlyList<InlineRun> runs, EditorPalette palette)
    {
        _runs = runs;
        _palette = palette;
        if (_realized) Apply();
    }

    /// <summary>Runs <paramref name="action"/> once the field has been laid out and filled.</summary>
    public void WhenRealized(Action action)
    {
        if (_realized) action();
        else _whenRealized.Add(action);
    }

    private void Apply()
    {
        _suppress = true;
        try
        {
            _urls.Clear();
            string text = string.Concat(_runs.Select(run => run.Text)).Replace('\n', Multiline ? '\r' : '\v');
            Box.Document.SetText(TextSetOptions.None, text);
            int offset = 0;
            foreach (var run in _runs)
            {
                if (run.Style != InlineStyle.None) Format(offset, run.Text.Length, run.Style, run.Url);
                offset += run.Text.Length;
            }
        }
        finally { _suppress = false; _contentChanging = false; }
    }

    private void Format(int start, int length, InlineStyle style, string? url)
    {
        if (length <= 0) return;
        var format = Box.Document.GetRange(start, start + length).CharacterFormat;
        if (style.HasFlag(InlineStyle.Bold)) format.Bold = FormatEffect.On;
        if (style.HasFlag(InlineStyle.Italic)) format.Italic = FormatEffect.On;
        if (style.HasFlag(InlineStyle.Underline)) format.Underline = UnderlineType.Single;
        if (style.HasFlag(InlineStyle.Strikethrough)) format.Strikethrough = FormatEffect.On;
        if (style.HasFlag(InlineStyle.Code))
        {
            format.Name = SourceEditor.MonospaceFont;
            format.BackgroundColor = _palette.CodeBackground;
        }
        if (style.HasFlag(InlineStyle.Link))
        {
            _urls.Add(url ?? "");
            format.ForegroundColor = _palette.Accent;
            format.Underline = UnderlineType.Single;
            format.Kerning = LinkBase + _urls.Count - 1;
        }
        if (style.HasFlag(InlineStyle.Raw))
        {
            format.ForegroundColor = _palette.Syntax;
            format.Kerning = RawMark;
            // Markdown the editor does not model is shown as written and cannot be typed into.
            format.ProtectedText = FormatEffect.On;
        }
    }

    public string ReadText()
    {
        Box.Document.GetText(TextGetOptions.UseLf, out string text);
        return text.Replace('\v', '\n');
    }

    /// <summary>The text as styled runs, read back from the character formats.</summary>
    public List<InlineRun> ReadRuns() => ReadRuns(0, int.MaxValue);

    public List<InlineRun> ReadRuns(int from, int to)
    {
        string text = ReadText();
        to = Math.Min(to, text.Length);
        var runs = new List<InlineRun>();
        var range = Box.Document.GetRange(0, 0);
        for (int position = Math.Max(0, from); position < to;)
        {
            range.SetRange(position, position + 1);
            range.EndOf(TextRangeUnit.CharacterFormat, true);
            int end = Math.Clamp(range.EndPosition, position + 1, to);
            var format = range.CharacterFormat;
            var style = InlineStyle.None;
            string? url = null;
            float kerning = format.Kerning;
            if (kerning >= RawMark - 0.5f) style |= InlineStyle.Raw;
            else if (kerning >= LinkBase - 0.5f)
            {
                int index = (int)Math.Round(kerning - LinkBase);
                style |= InlineStyle.Link;
                url = index >= 0 && index < _urls.Count ? _urls[index] : "";
            }
            if (format.Bold == FormatEffect.On && !Heading) style |= InlineStyle.Bold;
            if (format.Italic == FormatEffect.On) style |= InlineStyle.Italic;
            if (format.Underline != UnderlineType.None && !style.HasFlag(InlineStyle.Link)) style |= InlineStyle.Underline;
            if (format.Strikethrough == FormatEffect.On) style |= InlineStyle.Strikethrough;
            if (format.Name == SourceEditor.MonospaceFont) style |= InlineStyle.Code;
            runs.Add(new InlineRun(text[position..end], style, url));
            position = end;
        }
        return FormatRuns.Normalize(runs);
    }

    public (int Start, int Length) Selection
    {
        get
        {
            var selection = Box.Document.Selection;
            int length = Length, start = Math.Clamp(selection.StartPosition, 0, length), end = Math.Clamp(selection.EndPosition, start, length);
            return (start, end - start);
        }
    }

    public void Select(int start, int length = 0)
    {
        int total = Length;
        start = Math.Clamp(start, 0, total);
        Box.Document.Selection.SetRange(start, Math.Clamp(start + length, start, total));
    }

    public bool CaretOnFirstLine() => Box.Document.Selection.GetIndex(TextRangeUnit.Line) <= 1;

    public bool CaretOnLastLine()
    {
        int length = Length;
        return Box.Document.Selection.GetIndex(TextRangeUnit.Line) >= Box.Document.GetRange(length, length).GetIndex(TextRangeUnit.Line);
    }

    /// <summary>Turns a style on or off for the selection: on unless all of it already has the style.</summary>
    public void ToggleStyle(InlineStyle style, string? url = null)
    {
        var (start, length) = Selection;
        if (length == 0) return;
        bool all = ReadRuns(start, start + length).All(run => run.Style.HasFlag(style));
        _suppress = true;
        try
        {
            var format = Box.Document.GetRange(start, start + length).CharacterFormat;
            switch (style)
            {
                case InlineStyle.Bold: format.Bold = all ? FormatEffect.Off : FormatEffect.On; break;
                case InlineStyle.Italic: format.Italic = all ? FormatEffect.Off : FormatEffect.On; break;
                case InlineStyle.Underline: format.Underline = all ? UnderlineType.None : UnderlineType.Single; break;
                case InlineStyle.Strikethrough: format.Strikethrough = all ? FormatEffect.Off : FormatEffect.On; break;
                case InlineStyle.Code:
                    format.Name = all ? Box.FontFamily.Source.Split(',')[0].Trim() : SourceEditor.MonospaceFont;
                    format.BackgroundColor = all ? Colors.Transparent : _palette.CodeBackground;
                    break;
                case InlineStyle.Link:
                    if (all) { format.Kerning = 0; format.Underline = UnderlineType.None; format.ForegroundColor = _palette.Text; }
                    else Format(start, length, InlineStyle.Link, url);
                    break;
            }
        }
        finally { _suppress = false; }
        Changed?.Invoke(this);
    }

    /// <summary>Replaces the selection with styled text, as one change.</summary>
    public void Insert(string text, InlineStyle style, string? url)
    {
        var (start, length) = Selection;
        _suppress = true;
        try
        {
            Box.Document.GetRange(start, start + length).SetText(TextSetOptions.None, text.Replace('\n', Multiline ? '\r' : '\v'));
            Format(start, text.Length, style, url);
            Select(start + text.Length);
        }
        finally { _suppress = false; }
        Changed?.Invoke(this);
    }

    /// <summary>What applies at the caret or across the selection.</summary>
    public InlineStyle ActiveStyles()
    {
        var (start, length) = Selection;
        var runs = length > 0 ? ReadRuns(start, start + length) : ReadRuns(Math.Max(0, start - 1), Math.Max(1, start));
        if (runs.Count == 0) return InlineStyle.None;
        return runs.Aggregate((InlineStyle)~0, (all, run) => all & run.Style) & ~InlineStyle.Raw;
    }
}
