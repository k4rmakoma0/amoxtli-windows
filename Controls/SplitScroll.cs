using Amoxtli.Editing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Amoxtli.Controls;

/// <summary>
/// Scrolls Source and Format together in Split (port document, section 7.2). The side scrolled with
/// the wheel, touchpad or scroll bar leads; otherwise the side with the focus. Blocks are paired by
/// where they start in the Markdown, and the other side catches up 250 ms after an edit.
/// </summary>
internal sealed class SplitScroll
{
    public enum Side { Source, Format }

    private static readonly TimeSpan PointerLead = TimeSpan.FromMilliseconds(700);
    private readonly SourceEditor _source;
    private readonly FormatEditor _format;
    private readonly DispatcherQueueTimer _catchUp;
    private ScrollMap? _map;
    private Side _focused = Side.Format, _pointer = Side.Format, _edited;
    private DateTime _pointerTime = DateTime.MinValue;
    private ScrollViewer? _sourceScroll;
    // Where this class last moved each side; any other position means someone else scrolled it.
    private double _expectedSource = double.NaN, _expectedFormat = double.NaN;

    public bool Enabled { get; set; }

    public SplitScroll(SourceEditor source, FormatEditor format)
    {
        _source = source; _format = format;
        _catchUp = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _catchUp.Interval = TimeSpan.FromMilliseconds(250);
        _catchUp.IsRepeating = false;
        _catchUp.Tick += (_, _) => { Invalidate(); Follow(_edited); };
        Track(source, Side.Source);
        Track(format, Side.Format);
        source.Box.GotFocus += (_, _) => _focused = Side.Source;
        format.FocusEntered += () => _focused = Side.Format;
        format.Scroll.ViewChanged += (_, _) => ViewChanged(Side.Format);
        // Fields fill in after their first layout and code restyles in batches: positions move with the content height.
        format.Scroll.RegisterPropertyChangedCallback(ScrollViewer.ExtentHeightProperty, (_, _) => Invalidate());
        format.Rebuilt += Invalidate;
        format.SizeChanged += (_, _) => Invalidate();
        source.SizeChanged += (_, _) => { Invalidate(); HookSource(); };
    }

    /// <summary>The side that drives the other: a recent wheel or scroll-bar drag, else the focus.</summary>
    public Side Leader => DateTime.UtcNow - _pointerTime < PointerLead ? _pointer : _focused;

    public void Invalidate() => _map = null;

    /// <summary>After an edit on <paramref name="side"/>, the other side follows once layout settles.</summary>
    public void Edited(Side side)
    {
        if (!Enabled) return;
        _edited = side;
        _catchUp.Stop();
        _catchUp.Start();
    }

    private void Track(UIElement element, Side side)
    {
        void Stamp() { _pointer = side; _pointerTime = DateTime.UtcNow; }
        element.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => Stamp()), true);
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Stamp()), true);
        element.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((sender, e) =>
        {
            if (e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed) Stamp();
        }), true);
    }

    private void HookSource()
    {
        if (_sourceScroll is not null || _source.Scroll is not { } scroll) return;
        _sourceScroll = scroll;
        scroll.ViewChanged += (_, _) => ViewChanged(Side.Source);
        scroll.RegisterPropertyChangedCallback(ScrollViewer.ExtentHeightProperty, (_, _) => Invalidate());
    }

    private void ViewChanged(Side side)
    {
        if (!Enabled) return;
        if (side != Leader)
        {
            // The follower arriving where it was sent is ignored, so the two never drive each other.
            var scroll = side == Side.Source ? _sourceScroll : _format.Scroll;
            double expected = side == Side.Source ? _expectedSource : _expectedFormat;
            if (scroll is null || Math.Abs(scroll.VerticalOffset - expected) <= 1) return;
            // Scrolled by keyboard, accessibility tools or layout: that side leads now.
            _pointer = side;
            _pointerTime = DateTime.UtcNow;
        }
        Follow(side);
    }

    /// <summary>Moves the other side to match <paramref name="leader"/>.</summary>
    public void Follow(Side leader)
    {
        HookSource();
        if (!Enabled || _sourceScroll is not { } source) return;
        var map = _map ??= Build(source);
        if (leader == Side.Source)
        {
            _expectedFormat = Math.Clamp(map.Forward(source.VerticalOffset), 0, _format.Scroll.ScrollableHeight);
            _format.Scroll.ChangeView(null, _expectedFormat, null, true);
        }
        else
        {
            _expectedSource = Math.Clamp(map.Backward(_format.Scroll.VerticalOffset), 0, source.ScrollableHeight);
            source.ChangeView(null, _expectedSource, null, true);
        }
    }

    /// <summary>Source is view A and Format view B; each block's offset in the Markdown gives its line in Source.</summary>
    private ScrollMap Build(ScrollViewer source)
    {
        _source.CalibrateUnits();
        var blocks = new List<(double A, double B)>();
        bool aligned = _source.Text == _format.Markdown;
        if (aligned)
            foreach (var (offset, top) in _format.BlockTops()) blocks.Add((_source.TopOf(offset), top));
        return ScrollMap.Create(blocks, source.ScrollableHeight, _format.Scroll.ScrollableHeight, aligned);
    }

    internal ScrollMap Map
    {
        get
        {
            HookSource();
            return _map ??= _sourceScroll is { } source ? Build(source) : ScrollMap.Create([], 0, 0);
        }
    }
}
