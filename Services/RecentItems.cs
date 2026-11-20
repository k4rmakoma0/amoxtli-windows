using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amoxtli.Services;

internal enum ReadingSurface { Format, Source, Split, Reader }

internal sealed record ReadingPosition(ReadingSurface Surface, double VerticalOffset, double ScrollableHeight,
    int? TextOffset = null, double OffsetInText = 0);

internal sealed record RecentItem(string Path, bool IsFolder, string? FilePath = null, ReadingPosition? Position = null);

internal sealed class RecentItems(string? storagePath = null)
{
    private sealed record StoredLocation(string Path, bool IsFolder, string? FilePath);

    private const int Capacity = 8;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly string? _storagePath = storagePath is null ? null : Path.GetFullPath(storagePath);
    private readonly object _memoryLock = new();
    private RecentItem[] _memory = [];

    public static RecentItems ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amoxtli", "recent.json"));

    public IReadOnlyList<RecentItem> Read()
    {
        if (_storagePath is null)
        {
            lock (_memoryLock) return [.. _memory];
        }

        try { return ReadFile(_storagePath); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public void Remember(RecentItem item)
    {
        if (Normalize(item) is { } recent) Update(current => WithRecentItem(current, recent));
    }

    public void SavePosition(RecentItem item, ReadingPosition position)
    {
        if (Normalize(item) is not { } recent || (recent.IsFolder && recent.FilePath is null)
            || NormalizePosition(position) is not { } valid) return;

        var bookmark = valid.VerticalOffset > 0 ? valid : null;
        Update(current => current
            .Select(saved => SameDocument(saved, recent) ? saved with { Position = bookmark } : saved)
            .ToArray());
    }

    public void Clear() => Update(_ => []);

    private void Update(Func<RecentItem[], RecentItem[]> update)
    {
        if (_storagePath is null)
        {
            lock (_memoryLock) _memory = update(_memory);
            return;
        }

        // Several windows can be open in different processes. Merge into the latest snapshot.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_storagePath.ToUpperInvariant())));
        using var mutex = new Mutex(false, $@"Local\Amoxtli.RecentItems.{key}");
        try
        {
            if (!mutex.WaitOne(500)) throw new IOException("Timed out waiting to save recent items.");
        }
        catch (AbandonedMutexException) { /* Ownership was acquired. The last snapshot is still intact. */ }

        try
        {
            var current = ReadFile(_storagePath);
            var updated = update(current);
            if (current.SequenceEqual(updated)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            DocumentFile.Write(_storagePath, JsonSerializer.Serialize(updated), FileFormat.Default);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static RecentItem[] WithRecentItem(RecentItem[] current, RecentItem recent)
    {
        var previous = current.FirstOrDefault(item => PathComparer.Equals(item.Path, recent.Path));
        if (recent.IsFolder && recent.FilePath is null && previous is { IsFolder: true })
            recent = recent with { FilePath = previous.FilePath };
        if (recent.Position is null && previous is not null && SameDocument(previous, recent))
            recent = recent with { Position = previous.Position };

        return [recent, .. current.Where(item => !PathComparer.Equals(item.Path, recent.Path)).Take(Capacity - 1)];
    }

    private static bool SameDocument(RecentItem first, RecentItem second) => first.IsFolder == second.IsFolder
        && PathComparer.Equals(first.Path, second.Path) && PathComparer.Equals(first.FilePath, second.FilePath);

    private static RecentItem[] ReadFile(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var json = JsonDocument.Parse(file);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return [];

            var items = new List<RecentItem>(Capacity);
            var seen = new HashSet<string>(PathComparer);
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                // Read the location independently so a malformed position does not hide the item.
                StoredLocation? location;
                try { location = entry.Deserialize<StoredLocation>(); }
                catch (JsonException) { continue; }
                if (location is null) continue;

                var item = new RecentItem(location.Path, location.IsFolder, location.FilePath, ReadPosition(entry));
                if (Normalize(item) is not { } recent || !seen.Add(recent.Path)) continue;
                items.Add(recent);
                if (items.Count == Capacity) break;
            }
            return [.. items];
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException) { return []; }
    }

    private static ReadingPosition? ReadPosition(JsonElement entry)
    {
        if (!entry.TryGetProperty(nameof(RecentItem.Position), out var position)) return null;
        try { return position.Deserialize<ReadingPosition>(); }
        catch (JsonException) { return null; }
    }

    private static RecentItem? Normalize(RecentItem item)
    {
        if (NormalizePath(item.Path) is not { } path) return null;
        var file = item.IsFolder ? NormalizePath(item.FilePath) : null;
        var prefix = Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        if (file is not null && (file.Length <= prefix.Length || !file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            file = null;
        var position = item.IsFolder && file is null ? null : NormalizePosition(item.Position);
        return new RecentItem(path, item.IsFolder, file, position is { VerticalOffset: > 0 } ? position : null);
    }

    private static ReadingPosition? NormalizePosition(ReadingPosition? position)
    {
        if (position is null || !Enum.IsDefined(position.Surface) || position.TextOffset is < 0
            || !double.IsFinite(position.VerticalOffset) || position.VerticalOffset < 0
            || !double.IsFinite(position.ScrollableHeight) || position.ScrollableHeight < 0
            || (position.VerticalOffset > 0 && position.ScrollableHeight == 0)
            || !double.IsFinite(position.OffsetInText) || position.OffsetInText < 0) return null;

        return position.VerticalOffset <= position.ScrollableHeight ? position
            : position with { VerticalOffset = position.ScrollableHeight };
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
