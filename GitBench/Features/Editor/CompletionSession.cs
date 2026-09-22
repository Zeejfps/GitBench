using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>What opened a completion list, which decides what it does with an empty prefix and a
/// single match.</summary>
internal enum CompletionTrigger
{
    /// <summary>Opened on its own as an identifier was typed. Closes rather than show nothing new.</summary>
    Typed,

    /// <summary>Asked for with the shortcut. Stays open on an empty prefix, and inserts a lone
    /// match outright.</summary>
    Invoked,
}

/// <summary>An open completion list: the identifier it completes, the matches for what has been typed
/// of it so far, and which one Enter would take.</summary>
/// <param name="Start">Where the identifier being completed begins on <paramref name="Line"/>.</param>
/// <param name="Pool">Everything that was on offer when the list opened; typing narrows this, and
/// never re-reads the file.</param>
internal sealed record CompletionList(
    FileLine Line,
    int Start,
    string Prefix,
    IReadOnlyList<RankedCompletion> Items,
    int Selected,
    CompletionTrigger Trigger,
    IReadOnlyList<CompletionItem> Pool)
{
    public RankedCompletion SelectedItem => Items[Selected];
}

/// <summary>An accepted completion: the text it replaces and what replaces it.</summary>
internal readonly record struct CompletionEdit(TextRange Range, string Text);

/// <summary>
/// The completion list's life over one document: when it opens, how typing and the caret narrow
/// or close it, which entry is selected, and what accepting one replaces. Holds no view — the
/// surface draws <see cref="Current"/>.
/// </summary>
internal sealed class CompletionSession
{
    /// <summary>How many matches are kept. Past this a reader types more rather than scrolls.</summary>
    public const int MaxItems = 200;

    /// <summary>How many rows the list shows at once, which is also what a page key moves by.</summary>
    public const int VisibleRows = 10;

    /// <summary>The open list, or null when none is.</summary>
    public CompletionList? Current { get; private set; }

    public bool IsOpen => Current is not null;

    /// <summary>Opens the list at the caret on request. Returns whether one opened.</summary>
    public bool Invoke(TextDocument document, TextPosition caret, Func<IReadOnlyList<CompletionItem>> pool) =>
        Open(document, caret, pool(), CompletionTrigger.Invoked);

    /// <summary>Follows a character just typed: narrows an open list, or opens one when an identifier
    /// has just been started and something other than itself matches it.</summary>
    public void Typed(TextDocument document, TextPosition caret, Func<IReadOnlyList<CompletionItem>> pool)
    {
        if (Current is not null)
        {
            Follow(document, caret);
            return;
        }

        var line = document.Line(caret.Line);
        var start = LocalCompletions.PrefixStart(line, caret.Column.Value);
        if (start + 1 != caret.Column.Value) return;

        Open(document, caret, pool(), CompletionTrigger.Typed);
    }

    /// <summary>Re-reads the identifier after anything that may have changed it or moved the caret,
    /// closing the list once the caret has left it.</summary>
    public void Follow(TextDocument document, TextPosition caret)
    {
        if (Current is not { } list) return;
        if (caret.Line != list.Line || caret.Column.Value < list.Start)
        {
            Close();
            return;
        }

        var line = document.Line(caret.Line);
        if (caret.Column.Value > line.Length)
        {
            Close();
            return;
        }

        var prefix = line[list.Start..caret.Column.Value];
        var stillAWord = prefix.All(LocalCompletions.IsIdentifierPart);
        var emptied = prefix.Length == 0 && list.Trigger == CompletionTrigger.Typed;
        Current = stillAWord && !emptied ? Narrowed(list, prefix) : null;
    }

    /// <summary>Moves the selection, wrapping past either end.</summary>
    public void Move(int delta)
    {
        if (Current is not { } list) return;
        var count = list.Items.Count;
        Current = list with { Selected = ((list.Selected + delta) % count + count) % count };
    }

    /// <summary>Moves the selection a page, stopping at either end rather than wrapping.</summary>
    public void Page(int delta)
    {
        if (Current is not { } list) return;
        Current = list with { Selected = Math.Clamp(list.Selected + delta, 0, list.Items.Count - 1) };
    }

    /// <summary>Takes the selected entry and closes. Enter replaces what has been typed; Tab
    /// (<paramref name="wholeWord"/>) replaces the rest of the identifier right of the caret too.</summary>
    public CompletionEdit? Accept(TextDocument document, TextPosition caret, bool wholeWord)
    {
        if (Current is not { } list) return null;
        Close();

        var line = document.Line(list.Line);
        var end = wholeWord ? LocalCompletions.WordEnd(line, caret.Column.Value) : caret.Column.Value;
        return new CompletionEdit(
            new TextRange(new TextPosition(list.Line, new RawColumn(list.Start)),
                new TextPosition(list.Line, new RawColumn(end))),
            list.SelectedItem.Item.Label);
    }

    public void Close() => Current = null;

    private bool Open(TextDocument document, TextPosition caret, IReadOnlyList<CompletionItem> pool, CompletionTrigger trigger)
    {
        var line = document.Line(caret.Line);
        var start = LocalCompletions.PrefixStart(line, caret.Column.Value);
        var prefix = line[start..Math.Min(caret.Column.Value, line.Length)];
        var opened = Narrowed(new CompletionList(caret.Line, start, prefix, [], 0, trigger, pool), prefix);
        Current = opened;
        return opened is not null;
    }

    /// <summary>The list re-ranked for a prefix, with the best match selected again, or null when
    /// nothing is left worth showing.</summary>
    private static CompletionList? Narrowed(CompletionList list, string prefix)
    {
        var ranked = CompletionMatcher.Rank(prefix, list.Pool);
        if (ranked.Count > MaxItems) ranked = ranked.Take(MaxItems).ToList();
        if (ranked.Count == 0) return null;
        if (list.Trigger == CompletionTrigger.Typed && ranked.All(r => r.Item.Label == prefix)) return null;

        return list with { Prefix = prefix, Items = ranked, Selected = 0 };
    }
}
