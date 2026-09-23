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

    /// <summary>Opened by a character the server asked to be told about — a <c>.</c> — and filled
    /// only by what the server answers.</summary>
    Member,
}

/// <summary>Where the server stands on the open list.</summary>
internal enum ServerCompletionState
{
    /// <summary>Nobody to ask: the list is the file's own words.</summary>
    None,

    /// <summary>A question is out, and its answer will replace what the file offered.</summary>
    Asked,

    /// <summary>Answered, and typing more only narrows the answer.</summary>
    Answered,

    /// <summary>Answered, but the server said typing more may bring others: ask again as it does.</summary>
    AnsweredIncomplete,
}

/// <summary>An open completion list: the identifier it completes, the matches for what has been typed
/// of it so far, and which one Enter would take.</summary>
/// <param name="Start">Where the identifier being completed begins on <paramref name="Line"/>.</param>
/// <param name="Items">The matches, best first. Empty only while a server's answer is awaited.</param>
/// <param name="Pool">Everything on offer; typing narrows this, and never re-reads the file.</param>
internal sealed record CompletionList(
    FileLine Line,
    int Start,
    string Prefix,
    IReadOnlyList<RankedCompletion> Items,
    int Selected,
    CompletionTrigger Trigger,
    IReadOnlyList<CompletionItem> Pool,
    ServerCompletionState Server)
{
    public RankedCompletion? SelectedItem => Items.Count == 0 ? null : Items[Selected];
}

/// <summary>An accepted completion: the text it replaces, what replaces it, and the edits elsewhere
/// that come with it.</summary>
internal readonly record struct CompletionEdit(TextRange Range, string Text, IReadOnlyList<TextEdit> Additional);

/// <summary>
/// The completion list's life over one document: when it opens, how typing and the caret narrow
/// or close it, how a server's answer replaces what the file offered, which entry is selected, and
/// what accepting one replaces. Holds no view — the surface draws <see cref="Current"/>.
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
    /// <param name="asking">Whether a server has been asked too, which keeps the list open while
    /// the file alone offers nothing.</param>
    public bool Invoke(TextDocument document, TextPosition caret, Func<IReadOnlyList<CompletionItem>> pool, bool asking) =>
        Open(document, caret, pool(), CompletionTrigger.Invoked, asking);

    /// <summary>Follows a character just typed: narrows an open list, or opens one when an identifier
    /// has just been started. Returns whether this opened a list.</summary>
    public bool Typed(
        TextDocument document, TextPosition caret, Func<IReadOnlyList<CompletionItem>> pool, bool asking)
    {
        if (Current is not null)
        {
            Follow(document, caret);
            return false;
        }

        var line = document.Line(caret.Line);
        var start = LocalCompletions.PrefixStart(line, caret.Column.Value);
        if (start + 1 != caret.Column.Value) return false;

        return Open(document, caret, pool(), CompletionTrigger.Typed, asking);
    }

    /// <summary>Opens an empty list at the caret for a server to fill, after a character it asked
    /// to be told about.</summary>
    public void OpenForMembers(TextPosition caret) =>
        Current = new CompletionList(
            caret.Line, caret.Column.Value, string.Empty, [], 0, CompletionTrigger.Member, [],
            ServerCompletionState.Asked);

    /// <summary>Takes a server's answer for the list it was asked for. Its items replace what the file
    /// offered; no answer at all leaves the file's own words, except on a member list, which has
    /// nothing else to show.</summary>
    /// <param name="items">What the server offered, or null when it could not be asked.</param>
    public void Answered(
        FileLine line, int start, IReadOnlyList<CompletionItem>? items, bool incomplete,
        TextDocument document, TextPosition caret)
    {
        if (Current is not { } list || list.Line != line || list.Start != start) return;

        var server = items is null
            ? ServerCompletionState.None
            : incomplete ? ServerCompletionState.AnsweredIncomplete : ServerCompletionState.Answered;
        var pool = items is { Count: > 0 } ? items : list.Pool;
        Current = list with { Pool = pool, Server = server };
        Follow(document, caret);
    }

    /// <summary>Records that the server has been asked again for the open list.</summary>
    public void Asked()
    {
        if (Current is { } list) Current = list with { Server = ServerCompletionState.Asked };
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
        if (Current is not { Items.Count: > 0 } list) return;
        var count = list.Items.Count;
        Current = list with { Selected = ((list.Selected + delta) % count + count) % count };
    }

    /// <summary>Moves the selection a page, stopping at either end rather than wrapping.</summary>
    public void Page(int delta)
    {
        if (Current is not { Items.Count: > 0 } list) return;
        Current = list with { Selected = Math.Clamp(list.Selected + delta, 0, list.Items.Count - 1) };
    }

    /// <summary>Takes the selected entry and closes. Enter replaces what has been typed; Tab
    /// (<paramref name="wholeWord"/>) replaces the rest of the identifier right of the caret too.
    /// Null when there was nothing to take.</summary>
    public CompletionEdit? Accept(TextDocument document, TextPosition caret, bool wholeWord)
    {
        if (Current is not { SelectedItem: { } selected } list)
        {
            Close();
            return null;
        }

        Close();
        var line = document.Line(list.Line);
        var wordEnd = LocalCompletions.WordEnd(line, caret.Column.Value);

        switch (selected.Item.Insert)
        {
            case CompletionInsert.TheLabel:
            {
                var end = wholeWord ? wordEnd : caret.Column.Value;
                return new CompletionEdit(OnLine(list.Line, list.Start, end), selected.Item.Label, []);
            }

            case CompletionInsert.ServerEdit edit:
            {
                var start = edit.InsertStart is { } at && at.Line == list.Line && at.Column.Value <= caret.Column.Value
                    ? at.Column.Value
                    : list.Start;
                var end = caret.Column.Value;
                if (wholeWord)
                {
                    end = Math.Max(end, wordEnd);
                    if (edit.ReplaceEnd is { } reach && reach.Line == list.Line)
                        end = Math.Max(end, Math.Min(reach.Column.Value, line.Length));
                }

                return new CompletionEdit(OnLine(list.Line, start, end), edit.Text, edit.Additional);
            }

            default:
                throw new InvalidOperationException($"Unhandled completion insert {selected.Item.Insert}.");
        }
    }

    public void Close() => Current = null;

    private bool Open(
        TextDocument document, TextPosition caret, IReadOnlyList<CompletionItem> pool, CompletionTrigger trigger,
        bool asking)
    {
        var line = document.Line(caret.Line);
        var start = LocalCompletions.PrefixStart(line, caret.Column.Value);
        var prefix = line[start..Math.Min(caret.Column.Value, line.Length)];
        var server = asking ? ServerCompletionState.Asked : ServerCompletionState.None;
        var opened = Narrowed(new CompletionList(caret.Line, start, prefix, [], 0, trigger, pool, server), prefix);
        Current = opened;
        return opened is not null;
    }

    /// <summary>The list re-ranked for a prefix, with the best match selected again, or null when
    /// nothing is left worth showing and nothing more is coming.</summary>
    private static CompletionList? Narrowed(CompletionList list, string prefix)
    {
        var ranked = CompletionMatcher.Rank(prefix, list.Pool);
        if (ranked.Count > MaxItems) ranked = ranked.Take(MaxItems).ToList();

        var waiting = list.Server == ServerCompletionState.Asked;
        var nothingNew = ranked.Count == 0
            || (list.Trigger == CompletionTrigger.Typed && ranked.All(r => r.Item.Label == prefix));
        if (nothingNew && !waiting) return null;

        return list with { Prefix = prefix, Items = nothingNew ? [] : ranked, Selected = 0 };
    }

    private static TextRange OnLine(FileLine line, int start, int end) =>
        new(new TextPosition(line, new RawColumn(start)), new TextPosition(line, new RawColumn(end)));
}
