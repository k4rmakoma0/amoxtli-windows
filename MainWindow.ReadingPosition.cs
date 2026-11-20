using Amoxtli.Controls;
using Amoxtli.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amoxtli;

public sealed partial class MainWindow
{
    private ReadingDocument? _restoringPosition;

    private (ReadingSurface Surface, ScrollViewer Scroll)? CurrentReadingView()
    {
        if (ReaderScroll.Visibility == Visibility.Visible) return (ReadingSurface.Reader, ReaderScroll);
        if (FormatEditing) return (_mode == EditorMode.Split ? ReadingSurface.Split : ReadingSurface.Format, _formatEditor.Scroll);
        if (SourceHost.Visibility == Visibility.Visible && _sourceEditor.Scroll is { } source) return (ReadingSurface.Source, source);
        return null;
    }

    private void SaveRecentPosition()
    {
        if (_document is not { Kind: DocumentKind.File, Path: { } path }) return;

        try
        {
            if (CaptureReadingPosition() is { } position) _recents.SavePosition(RecentDocument(path), position);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(Localization.Format("SaveSettingsError", ex.Message), InfoBarSeverity.Warning);
        }
    }

    private ReadingPosition? CaptureReadingPosition()
    {
        if (_closed || ReferenceEquals(_document, _restoringPosition) || CurrentReadingView() is not { } view || view.Scroll.ViewportHeight <= 0)
            return null;

        double offset = view.Scroll.VerticalOffset;
        (int Offset, double Top)? anchor = offset <= 0 ? null : view.Surface switch
        {
            ReadingSurface.Source => _sourceEditor.LineAt(offset),
            ReadingSurface.Reader => _renderer?.PositionAt(offset),
            _ => FormatBlockAt(offset)
        };
        return new ReadingPosition(view.Surface, offset, view.Scroll.ScrollableHeight,
            anchor?.Offset, anchor is { } block ? Math.Max(0, offset - block.Top) : 0);
    }

    private (int Offset, double Top)? FormatBlockAt(double offset)
    {
        (int Offset, double Top)? result = null;
        foreach (var block in _formatEditor.BlockTops())
        {
            if (block.Top > offset) break;
            result = block;
        }
        return result;
    }

    private async Task RestoreReadingPositionAsync(ReadingPosition? position)
    {
        if (_closed || _document is not { } document) return;
        int revision = _openRevision;
        _restoringPosition = document;
        try
        {
            if (position is not null) SetReadingSurface(position.Surface);
            var mode = _mode;
            bool IsCurrent() => !_closed && revision == _openRevision && _mode == mode && ReferenceEquals(document, _document);
            // Rich fields populate on their first layout. Apply the bookmark after those fields have
            // supplied their final height and the editors have run their deferred scroll resets.
            var ready = new TaskCompletionSource();
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (IsCurrent()) Root.UpdateLayout();
                if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ready.SetResult())) ready.SetResult();
            })) return;
            await ready.Task;
            if (!IsCurrent()) return;

            Root.UpdateLayout();
            if (CurrentReadingView() is not { } view) return;
            if (view.Surface == ReadingSurface.Source && position is { VerticalOffset: > 0 }) _sourceEditor.CalibrateUnits();
            double? anchoredOffset = position?.Surface == view.Surface && position.TextOffset is { } textOffset
                ? OffsetAt(view.Surface, textOffset, position.OffsetInText)
                : null;
            double height = view.Scroll.ScrollableHeight;
            double offset = position is not { VerticalOffset: > 0 } ? 0
                : anchoredOffset is { } anchored && double.IsFinite(anchored) ? anchored
                : position.Surface == view.Surface && Math.Abs(height - position.ScrollableHeight) <= 1 ? position.VerticalOffset
                : height * (position.VerticalOffset / position.ScrollableHeight);
            offset = Math.Clamp(offset, 0, height);
            if (view.Surface == ReadingSurface.Split) _splitScroll.RestoreFormatOffset(offset);
            else view.Scroll.ChangeView(null, offset, null, true);
        }
        finally
        {
            if (ReferenceEquals(document, _restoringPosition)) _restoringPosition = null;
        }
    }

    private void SetReadingSurface(ReadingSurface surface)
    {
        if (surface == ReadingSurface.Source || (TooLarge(_document) && surface != ReadingSurface.Reader))
        {
            SetMode(EditorMode.Source, focusSource: false);
            return;
        }

        SetMode(surface == ReadingSurface.Split ? EditorMode.Split : EditorMode.Format, focusSource: false);
        if (surface == ReadingSurface.Reader && TooLarge(_document)) ShowLargeReader();
    }

    private double? TopAt(ReadingSurface surface, int textOffset)
    {
        if (surface == ReadingSurface.Reader) return _renderer?.TopAt(textOffset);
        if (surface == ReadingSurface.Source) return textOffset <= _sourceEditor.Text.Length ? _sourceEditor.TopOf(textOffset) : null;
        foreach (var block in _formatEditor.BlockTops())
            if (block.Offset == textOffset) return block.Top;
        return null;
    }

    private double? OffsetAt(ReadingSurface surface, int textOffset, double offsetInBlock)
    {
        if (TopAt(surface, textOffset) is not { } top) return null;
        double offset = top + offsetInBlock;
        if (surface is ReadingSurface.Format or ReadingSurface.Split)
        {
            // A paragraph can become shorter when the available width changes. Keep the bookmark inside it.
            foreach (var next in _formatEditor.BlockTops())
                if (next.Top > top) return Math.Min(offset, Math.Max(top, next.Top - 1));
        }
        return offset;
    }
}
