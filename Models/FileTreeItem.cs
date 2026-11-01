namespace Amoxtli.Models;

public sealed record FileTreeItem(string Path, bool IsDirectory)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
}
