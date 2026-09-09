namespace GitBench.Features.Editor;

/// <summary>What kind of change an edit was, for the purpose of grouping a run of them into one
/// undo step.</summary>
internal enum EditKind
{
    None,
    Insert,
    Delete,

    /// <summary>A typed character that replaced a selection: it opens a step of its own, but later
    /// typing merges into it.</summary>
    Replace,

    /// <summary>A change that is its own step whatever surrounds it: a paste, a newline, a word
    /// delete.</summary>
    Boundary,
}

/// <summary>One undoable change, however many edits it is made of, together with the selection it
/// was made with. Edits apply in the order given, each against the document the one before it left.</summary>
internal readonly record struct EditTransaction(
    EditKind Kind,
    IReadOnlyList<TextEdit> Edits,
    SelectionRange Selection,
    AnchorBias Bias);

/// <summary>Undo and redo for one document. Steps hold the edits that reverse them, not copies of
/// the text; a run of same-kind, contiguous, unselected edits coalesces into one step.</summary>
internal sealed class EditJournal
{
    /// <summary>How many steps are kept before the oldest falls off.</summary>
    public const int MaxDepth = 256;

    private sealed class Step
    {
        public Step(List<TextEdit> edits, TextRange selectionBefore, TextRange selectionAfter)
        {
            Edits = edits;
            SelectionBefore = selectionBefore;
            SelectionAfter = selectionAfter;
        }

        /// <summary>Applied back to front, these move the document to the other side of this step.</summary>
        public readonly List<TextEdit> Edits;
        public readonly TextRange SelectionBefore;
        public TextRange SelectionAfter;
    }

    private readonly TextDocument _document;
    private readonly Action<TextEdit>? _applied;
    private readonly List<Step> _undo = new();
    private readonly List<Step> _redo = new();
    private EditKind _lastKind = EditKind.None;
    private TextPosition _lastCaret;

    /// <param name="applied">Told about each edit as it lands, as the inverse the document handed
    /// back — one call per edit, through undo and redo as much as on the way forward.</param>
    public EditJournal(TextDocument document, Action<TextEdit>? applied = null)
    {
        _document = document;
        _applied = applied;
        _lastCaret = TextPosition.At(1, 0);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoDepth => _undo.Count;

    /// <summary>Applies a transaction to the document and records the step that reverses it. Returns
    /// the selection to show, carried across the change. A transaction that changes no text records
    /// nothing.</summary>
    public SelectionRange Apply(in EditTransaction transaction)
    {
        var before = transaction.Selection;
        var continues = transaction.Kind switch
        {
            EditKind.Insert => _lastKind is EditKind.Insert or EditKind.Replace,
            EditKind.Delete => _lastKind is EditKind.Delete,
            _ => false,
        };

        var coalesce = _undo.Count > 0
            && continues
            && before.IsEmpty
            && before.Caret == _lastCaret;

        var carried = Clamp(before);
        var revision = _document.Revision;
        var reversal = ApplyAll(transaction.Edits, transaction.Bias, ref carried);

        var after = Clamp(carried);
        if (_document.Revision == revision) return after;

        if (coalesce)
        {
            var step = _undo[^1];
            step.Edits.AddRange(reversal);
            step.SelectionAfter = after.Range;
        }
        else
        {
            Push(_undo, new Step(reversal, before.Range, after.Range));
            _redo.Clear();
        }

        _lastKind = transaction.Kind;
        _lastCaret = after.Caret;
        return after;
    }

    /// <summary>Reverses the newest step and returns the selection it was made with, or null when
    /// there is nothing left to undo.</summary>
    public TextRange? Undo()
    {
        var step = Move(_undo, _redo);
        return step == null ? null : EndRun(step.SelectionBefore);
    }

    /// <summary>Re-applies the step undo last reversed and returns the selection it ended with, or
    /// null when nothing has been undone.</summary>
    public TextRange? Redo()
    {
        var step = Move(_redo, _undo);
        return step == null ? null : EndRun(step.SelectionAfter);
    }

    private Step? Move(List<Step> from, List<Step> to)
    {
        if (from.Count == 0) return null;

        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        Push(to, new Step(ApplyReversal(step.Edits), step.SelectionBefore, step.SelectionAfter));
        return step;
    }

    private TextRange EndRun(TextRange selection)
    {
        var restored = _document.Clamp(selection);
        _lastKind = EditKind.None;
        _lastCaret = restored.End;
        return restored;
    }

    /// <summary>Applies a transaction's edits front to back — the order it declares them in —
    /// carrying <paramref name="selection"/> across each, and returns the list that undoes them, in
    /// this journal's back-to-front order.</summary>
    private List<TextEdit> ApplyAll(
        IReadOnlyList<TextEdit> edits, AnchorBias bias, ref SelectionRange selection)
    {
        var reversal = new List<TextEdit>(edits.Count);
        foreach (var edit in edits)
        {
            var inverse = _document.Apply(edit, out var applied);
            _applied?.Invoke(inverse);
            reversal.Add(inverse);
            selection = new SelectionRange(
                TextEdit.Shift(applied, selection.Anchor, bias),
                TextEdit.Shift(applied, selection.Caret, bias));
        }

        return reversal;
    }

    /// <summary>Applies a stored reversal back to front and returns the list that undoes <em>it</em>,
    /// read the same way.</summary>
    private List<TextEdit> ApplyReversal(List<TextEdit> reversal)
    {
        var forward = new List<TextEdit>(reversal.Count);
        for (var i = reversal.Count - 1; i >= 0; i--)
            forward.Add(ApplyOne(reversal[i]));
        return forward;
    }

    private TextEdit ApplyOne(TextEdit edit)
    {
        var inverse = _document.Apply(edit);
        _applied?.Invoke(inverse);
        return inverse;
    }

    private SelectionRange Clamp(SelectionRange selection) =>
        new(_document.Clamp(selection.Anchor), _document.Clamp(selection.Caret));

    private static void Push(List<Step> stack, Step step)
    {
        stack.Add(step);
        if (stack.Count > MaxDepth) stack.RemoveAt(0);
    }
}
