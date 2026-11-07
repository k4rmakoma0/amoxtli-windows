namespace Amoxtli.Editing;

/// <summary>
/// Text-only undo. Formatting commands are one step each; consecutive typing within a word,
/// and consecutive Backspace or Delete, merge into one step.
/// </summary>
public sealed class EditHistory
{
    private sealed record Step(TextEdit Redo, TextEdit Undo, bool Typing, DateTime Time);
    private const int Limit = 1000;
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(2);
    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear() { _undo.Clear(); _redo.Clear(); }

    /// <param name="before">The text before <paramref name="edit"/>.</param>
    public void Record(string before, TextEdit edit, bool typing, DateTime? now = null)
    {
        var time = now ?? DateTime.UtcNow;
        _redo.Clear();
        var undo = new TextEdit(edit.Start, edit.Text.Length, before.Substring(edit.Start, edit.Length), edit.Start, edit.Length);
        if (typing && _undo.Count > 0 && _undo[^1] is { Typing: true } last && time - last.Time < MergeWindow
            && Merge(last, edit, undo, time) is { } merged)
        {
            _undo[^1] = merged;
            return;
        }
        _undo.Add(new Step(edit, undo, typing, time));
        if (_undo.Count > Limit) _undo.RemoveAt(0);
    }

    public TextEdit? Undo()
    {
        if (_undo.Count == 0) return null;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(step with { Typing = false });
        return step.Undo;
    }

    public TextEdit? Redo()
    {
        if (_redo.Count == 0) return null;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(step);
        return step.Redo;
    }

    private static Step? Merge(Step last, TextEdit edit, TextEdit undo, DateTime time)
    {
        var previous = last.Redo;
        if (previous.Length == 0 && edit.Length == 0 && edit.Text.Length > 0 && edit.Start == previous.Start + previous.Text.Length)
        {
            // A new word after a space starts a new step, like typing in Notepad.
            if (edit.Text.Contains('\n') || (char.IsWhiteSpace(previous.Text[^1]) && !char.IsWhiteSpace(edit.Text[0]))) return null;
            string text = previous.Text + edit.Text;
            return new Step(new TextEdit(previous.Start, 0, text, previous.Start + text.Length, 0),
                new TextEdit(previous.Start, text.Length, "", previous.Start, 0), true, time);
        }
        if (previous.Text.Length == 0 && edit.Text.Length == 0 && edit.Length > 0)
        {
            string removed;
            int start;
            if (edit.Start + edit.Length == previous.Start) { removed = undo.Text + last.Undo.Text; start = edit.Start; }
            else if (edit.Start == previous.Start) { removed = last.Undo.Text + undo.Text; start = previous.Start; }
            else return null;
            return new Step(new TextEdit(start, removed.Length, "", start, 0), new TextEdit(start, 0, removed, start, removed.Length), true, time);
        }
        return null;
    }
}
