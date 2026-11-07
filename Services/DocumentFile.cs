using System.Text;

namespace Amoxtli.Services;

/// <summary>How a file was stored, so saving writes it back the same way.</summary>
public sealed record FileFormat(Encoding Encoding, bool ByteOrderMark, bool CrLf)
{
    public static readonly FileFormat Default = new(new UTF8Encoding(false, true), false, false);
}

/// <summary>
/// Markdown files on disk. Text is decoded strictly: a file that is not valid UTF-8 (or UTF-16
/// with a byte order mark) is refused, so it is never shown, edited or overwritten.
/// In memory, line endings are always \n.
/// </summary>
public static class DocumentFile
{
    public const long MaxBytes = 8 * 1024 * 1024;

    public static async Task<(string Text, FileFormat Format)> ReadAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        if (file.Length > MaxBytes) throw new InvalidOperationException(Localization.Get("FileTooLarge"));
        var bytes = new byte[file.Length];
        await file.ReadExactlyAsync(bytes);
        return Decode(bytes);
    }

    public static (string Text, FileFormat Format) Decode(byte[] bytes)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        int skip = 0;
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) skip = 3;
        else if (bytes is [0xFF, 0xFE, ..]) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes is [0xFE, 0xFF, ..]) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        string text;
        try { text = encoding.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) { throw new InvalidDataException(Localization.Get("InvalidEncoding")); }
        return (NormalizeLineEndings(text), new FileFormat(encoding, skip > 0, text.Contains("\r\n")));
    }

    public static string NormalizeLineEndings(string text) => text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    public static byte[] Encode(string text, FileFormat format)
    {
        string content = format.CrLf ? text.Replace("\n", "\r\n") : text;
        byte[] preamble = !format.ByteOrderMark ? [] : format.Encoding.CodePage switch
        {
            1200 => [0xFF, 0xFE],
            1201 => [0xFE, 0xFF],
            _ => [0xEF, 0xBB, 0xBF]
        };
        return [.. preamble, .. format.Encoding.GetBytes(content)];
    }

    /// <summary>Writes a temporary file beside the target and swaps it in, so a failed save never truncates the original.</summary>
    public static void Write(string path, string text, FileFormat format)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encode(text, format));
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null, true);
            else File.Move(temporary, path);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
