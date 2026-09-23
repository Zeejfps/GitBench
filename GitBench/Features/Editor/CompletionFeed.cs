using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>What a server offered for one list, in the editor's terms: its items, whether typing
/// more may bring others — or null items when it could not be asked at all.</summary>
internal sealed record ServerCompletions(IReadOnlyList<CompletionItem>? Items, bool Incomplete)
{
    public static readonly ServerCompletions Unavailable = new(null, false);
}

/// <summary>
/// The language server's side of completion: one question out at a time, asked off the UI thread,
/// the answer handed back on it in the editor's own positions and kinds — or not at all once
/// another question replaced it.
/// </summary>
internal sealed class CompletionFeed : IDisposable
{
    private readonly ICompletionSource _source;
    private readonly ProbeSlot _slot;

    public CompletionFeed(ICompletionSource source, IUiDispatcher dispatcher)
    {
        _source = source;
        _slot = new ProbeSlot(dispatcher);
    }

    public bool Serves(string path) => _source.CanComplete(path);

    /// <summary>Whether typing <paramref name="typed"/> should open a list by itself.</summary>
    public bool TriggersOn(string path, char typed) => _source.CompletionTriggers(path).Contains(typed);

    public void Ask(string path, TextPosition caret, CompletionAsk ask, Action<ServerCompletions> then) =>
        _slot.Ask(
            TimeSpan.Zero,
            async cancel => Translate(
                await _source.CompletionsAsync(path, caret.Line, caret.Column, ask, cancel).ConfigureAwait(false)),
            then);

    public void Cancel() => _slot.Cancel();

    public void Dispose() => _slot.Dispose();

    /// <summary>Off the UI thread: a server can offer thousands of items.</summary>
    internal static ServerCompletions Translate(CompletionReply reply) => reply switch
    {
        CompletionReply.Answered(var completions) => new ServerCompletions(
            completions.Items.Select(ItemOf).ToArray(), completions.IsIncomplete),
        CompletionReply.Unavailable => ServerCompletions.Unavailable,
        _ => throw new NotSupportedException($"unhandled completion reply {reply.GetType().Name}"),
    };

    private static CompletionItem ItemOf(LspCompletionItem item) =>
        new(item.Label, KindOf(item.Kind))
        {
            Detail = item.Detail,
            SortText = item.SortText,
            FilterText = item.FilterText,
            Insert = new CompletionInsert.ServerEdit(
                item.InsertText,
                item.InsertRange?.Start is { } start ? PositionOf(start) : null,
                item.ReplaceRange?.End is { } end ? PositionOf(end) : null,
                item.AdditionalEdits.Select(edit => new TextEdit(RangeOf(edit.Range), edit.NewText)).ToArray()),
        };

    // Mapped onto the outline's four glyphs rather than one per kind: a list scanned at typing speed
    // separates what runs from what holds a value from what contains either, the way the outline does.
    private static CompletionKind KindOf(LspCompletionKind? kind) => kind switch
    {
        LspCompletionKind.Method or LspCompletionKind.Function or LspCompletionKind.Constructor =>
            new CompletionKind.Symbol(SymbolKind.Method),
        LspCompletionKind.Field or LspCompletionKind.Variable or LspCompletionKind.Property
            or LspCompletionKind.Constant or LspCompletionKind.EnumMember or LspCompletionKind.Event
            or LspCompletionKind.Value or LspCompletionKind.Reference =>
            new CompletionKind.Symbol(SymbolKind.Field),
        LspCompletionKind.Class or LspCompletionKind.Interface or LspCompletionKind.Struct
            or LspCompletionKind.Enum or LspCompletionKind.Module or LspCompletionKind.TypeParameter
            or LspCompletionKind.Unit =>
            new CompletionKind.Symbol(SymbolKind.Class),
        LspCompletionKind.Keyword => CompletionKind.AKeyword,
        _ => CompletionKind.AWord,
    };

    private static TextPosition PositionOf(LspPosition position) =>
        new(new FileLine(position.Line.ToOneBased()), new RawColumn(position.Character.Value));

    private static TextRange RangeOf(LspRange range) => new(PositionOf(range.Start), PositionOf(range.End));
}
