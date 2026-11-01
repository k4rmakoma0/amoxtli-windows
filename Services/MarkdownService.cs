using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Text;

namespace Amoxtli.Services;

public sealed record HeadingInfo(string Title, string Id, int Level)
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
        .Select(h => new HeadingInfo(PlainText(h.Inline), h.GetAttributes().Id ?? "", h.Level)).ToList();

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
