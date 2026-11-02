using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Amoxtli.Services;

namespace Amoxtli.Rendering;

// Keep the complete Markdown model/search index, but materialize native text only near the viewport.
internal sealed class VirtualMarkdownDocument : IDisposable
{
    private sealed class Entry(Block block, int index)
    {
        public Block? Block { get; set; } = block;
        public int Index { get; } = index;
        public Border Element { get; } = new();
        public List<string> SearchText { get; } = [];
        public NativeMarkdownRenderer? Renderer { get; set; }
    }

    private sealed class BlockFactory(VirtualMarkdownDocument owner) : IElementFactory
    {
        // Native ItemsRepeater holds this COM callback. A strong owner would form a
        // managed/native cycle through the repeater and retain the previous document.
        private readonly WeakReference<VirtualMarkdownDocument> _owner = new(owner);
        public UIElement GetElement(ElementFactoryGetArgs args) => _owner.TryGetTarget(out var target)
            ? target.Realize((Entry)args.Data) : new Border();
        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (_owner.TryGetTarget(out var target) && args.Element is Border border && target._byElement.TryGetValue(border, out var entry)) Release(entry);
        }
    }

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<FrameworkElement, Entry> _byElement = [];
    private readonly Dictionary<FrameworkElement, (Entry Entry, int Match)> _searchTargets = [];
    private readonly Dictionary<string, FrameworkElement> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private Func<StackPanel, NativeMarkdownRenderer>? _createRenderer;
    private readonly ItemsRepeater _repeater;
    private string _query = "";
    private bool _disposed;
    public IReadOnlyDictionary<string, FrameworkElement> Anchors => _anchors;

    public VirtualMarkdownDocument(StackPanel host, MarkdownDocument document, Func<StackPanel, NativeMarkdownRenderer> createRenderer)
    {
        _createRenderer = createRenderer;
        foreach (var block in document)
        {
            var entry = new Entry(block, _entries.Count);
            IndexBlock(block, entry, 0);
            _entries.Add(entry);
            _byElement.Add(entry.Element, entry);
        }
        _repeater = new ItemsRepeater
        {
            Layout = new StackLayout { Spacing = host.Spacing },
            VerticalCacheLength = 1,
            ItemTemplate = new BlockFactory(this),
            ItemsSource = _entries
        };
        host.Children.Add(_repeater);
    }

    private void IndexBlock(Block block, Entry entry, int depth)
    {
        if (depth > 40) { entry.SearchText.Add(Localization.Get("TooNested")); return; }
        if (block is HeadingBlock heading && heading.GetAttributes().Id is { Length: > 0 } id) _anchors[id] = entry.Element;
        if (block is Footnote note) _anchors[$"fn:{note.Label?.TrimStart('^') ?? note.Order.ToString()}"] = entry.Element;
        if (block is LeafBlock leaf)
        {
            if (leaf.Inline is not null)
            {
                entry.SearchText.Add(MarkdownService.PlainText(leaf.Inline));
                IndexInlines(leaf.Inline, entry, 0);
            }
            else if (block is CodeBlock or HtmlBlock) entry.SearchText.Add(leaf.Lines.ToString());
        }
        if (block is ContainerBlock container)
            foreach (var child in container) IndexBlock(child, entry, depth + 1);
    }

    private void IndexInlines(ContainerInline container, Entry entry, int depth)
    {
        if (depth > 40) return;
        foreach (var inline in container)
        {
            if (inline is FootnoteLink { IsBackLink: false } note)
            {
                string label = note.Footnote.Label?.TrimStart('^') ?? note.Footnote.Order.ToString();
                _anchors.TryAdd($"fnref:{label}", entry.Element);
            }
            if (inline is ContainerInline child) IndexInlines(child, entry, depth + 1);
        }
    }

    private UIElement Realize(Entry entry)
    {
        if (_disposed || entry.Block is null || _createRenderer is null) return new Border();
        if (entry.Renderer is null)
        {
            var panel = new StackPanel();
            entry.Renderer = _createRenderer(panel);
            entry.Renderer.RenderBlock(entry.Block);
            entry.Renderer.Search(_query);
            entry.Element.Child = panel;
        }
        return entry.Element;
    }

    private static void Release(Entry entry)
    {
        entry.Renderer?.Dispose();
        entry.Renderer = null;
        entry.Element.Child = null;
    }

    public List<FrameworkElement> Search(string query)
    {
        _query = query;
        _searchTargets.Clear();
        var matches = new List<FrameworkElement>();
        foreach (var entry in _entries)
        {
            entry.Renderer?.Search(query);
            if (string.IsNullOrWhiteSpace(query)) continue;
            int match = 0;
            foreach (var text in entry.SearchText)
            {
                if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var target = new Border();
                _searchTargets.Add(target, (entry, match++));
                matches.Add(target);
            }
        }
        return matches;
    }

    public FrameworkElement PrepareForNavigation(FrameworkElement element)
    {
        if (_searchTargets.TryGetValue(element, out var target))
        {
            _repeater.GetOrCreateElement(target.Entry.Index);
            var matches = target.Entry.Renderer?.Search(_query);
            return matches is not null && target.Match < matches.Count ? matches[target.Match] : target.Entry.Element;
        }
        if (_byElement.TryGetValue(element, out var entry)) _repeater.GetOrCreateElement(entry.Index);
        return element;
    }

    public FrameworkElement? ResolveAnchor(string id)
    {
        if (!_anchors.TryGetValue(id, out var element)) return null;
        PrepareForNavigation(element);
        return _byElement[element].Renderer?.ResolveAnchor(id) ?? element;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _repeater.ItemsSource = null;
        foreach (var entry in _entries)
        {
            Release(entry);
            entry.Block = null;
            entry.SearchText.Clear();
        }
        _createRenderer = null;
        _entries.Clear();
        _byElement.Clear();
        _anchors.Clear();
        _searchTargets.Clear();
    }
}
