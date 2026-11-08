using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Text;

namespace Amoxtli.Services;

public sealed record HeadingInfo(string Title, string Id, int Level, int Line = 0)
{
    public Microsoft.UI.Xaml.Thickness Indent => new((Level - 1) * 12, 6, 0, 6);
}

public static class MarkdownService
{
    public static readonly string[] Extensions = [".md", ".markdown", ".mdown", ".mkd", ".mkdn"];

    // Enable only extensions for which the native renderer has a presentation.
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseAutoLinks().UsePipeTables().UseGridTables().UseTaskLists()
        .UseEmphasisExtras().UseFootnotes().UseDefinitionLists()
        .UseAbbreviations().UseCustomContainers().UseYamlFrontMatter()
        .Build();

    public static bool IsMarkdown(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static MarkdownDocument Parse(string source) => Markdown.Parse(source, Pipeline);

    public static List<HeadingInfo> GetHeadings(MarkdownDocument document) => document.Descendants<HeadingBlock>()
        .Select(h => new HeadingInfo(PlainText(h.Inline), h.GetAttributes().Id ?? "", h.Level, h.Line)).ToList();

    /// <summary>Words of headings, paragraphs, lists, quotes and tables: no code and no images.</summary>
    public static int CountWords(MarkdownDocument document)
    {
        int words = 0;
        foreach (var leaf in document.Descendants<LeafBlock>())
            if (leaf is not CodeBlock && leaf.Inline is { } inline) words += CountWords(inline);
        return words;
    }

    private static int CountWords(ContainerInline inline)
    {
        int words = 0;
        bool inWord = false;
        void Text(ReadOnlySpan<char> text)
        {
            foreach (char c in text)
            {
                bool letter = !char.IsWhiteSpace(c);
                if (letter && !inWord) words++;
                inWord = letter;
            }
        }
        void Walk(ContainerInline container)
        {
            foreach (var child in container)
            {
                switch (child)
                {
                    case LiteralInline literal: Text(literal.Content.AsSpan()); break;
                    case LineBreakInline: inWord = false; break;
                    case LinkInline { IsImage: true }: break;
                    case CodeInline: inWord = false; break;
                    case ContainerInline nested: Walk(nested); break;
                }
            }
        }
        Walk(inline);
        return words;
    }

    public static string PlainText(ContainerInline? inline)
    {
        var text = new StringBuilder();
        if (inline is null) return "";
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal: text.Append(literal.Content); break;
                case CodeInline code: text.Append(code.Content); break;
                case LineBreakInline: text.Append(' '); break;
                case HtmlEntityInline entity: text.Append(entity.Transcoded); break;
                case AutolinkInline link: text.Append(link.Url); break;
                case ContainerInline container: text.Append(PlainText(container)); break;
            }
        }
        return text.ToString();
    }
}
