#if DEBUG
using Amoxtli.Services;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace Amoxtli;

public sealed partial class MainWindow
{
    // GC is forced only in this diagnostic to distinguish live objects from reusable heap space.
    internal async Task RunMemoryTestsAsync(string reportPath, string documentPath)
    {
        var samples = new List<object>();
        string? error = null;
        void Sample(string stage, WeakReference[]? previous = null)
        {
            using var process = Process.GetCurrentProcess();
            var heap = GC.GetGCMemoryInfo();
            samples.Add(new
            {
                stage,
                workingSetMiB = process.WorkingSet64 / 1048576d,
                privateMiB = process.PrivateMemorySize64 / 1048576d,
                managedMiB = GC.GetTotalMemory(false) / 1048576d,
                heapMiB = heap.HeapSizeBytes / 1048576d,
                allocatedMiB = GC.GetTotalAllocatedBytes() / 1048576d,
                previousRendererAlive = previous?[0].IsAlive,
                previousFirstBlockAlive = previous?[1].IsAlive,
                previousLastBlockAlive = previous?[2].IsAlive,
                previousNativeContentReleased = previous is null ? (bool?)null : !HasDocumentContent(previous[1]) && !HasDocumentContent(previous[2]),
                sourceViewCharacters = SourceText?.Text.Length ?? 0,
                notice = Notice.IsOpen ? Notice.Message : null
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { samples, error }, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            _settings.RemoteImages = false;
            ShowSmallMemoryDocument();
            await Task.Delay(500);
            Sample("baseline");
            for (int iteration = 1; iteration <= 3; iteration++)
            {
                var previous = await ShowLargeMemoryDocumentAsync(documentPath);
                await Task.Delay(1000);
                Sample($"large-{iteration}");
                ShowSmallMemoryDocument();
                await Task.Delay(1000);
                Sample($"small-{iteration}", previous);
                for (int pass = 0; pass < 3; pass++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(100);
                }
                Sample($"collected-{iteration}", previous);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { Sample("finished"); }
    }

    private static bool HasDocumentContent(WeakReference reference) => reference.Target switch
    {
        ItemsRepeater repeater => repeater.ItemsSource is not null,
        RichTextBlock text => text.Blocks.Count > 0,
        TextBlock text => text.Text.Length > 0,
        _ => false
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ShowSmallMemoryDocument()
    {
        const string source = "# Documento pequeño\n\nUna lectura breve para comprobar la liberación del documento anterior.\n";
        Navigate(new ReadingDocument(source, null, "Memoria: pequeño"), MarkdownService.Parse(source));
        if (Notice.IsOpen && Notice.Severity == InfoBarSeverity.Error) throw new InvalidOperationException(Notice.Message);
        DocumentPanel.UpdateLayout();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference[]> ShowLargeMemoryDocumentAsync(string path)
    {
        string source = await ReadMarkdownAsync(path);
        var parsed = await Task.Run(() => MarkdownService.Parse(source));
        Navigate(new ReadingDocument(source, path, "Memoria: grande"), parsed);
        DocumentPanel.UpdateLayout();
        return [new(_renderer), new(DocumentPanel.Children.First()), new(DocumentPanel.Children.Last())];
    }
}
#endif
