namespace GitBench.Features.Diff;

/// <summary>
/// What is known about one declaration's usages. Three states and no fourth: the question is out
/// and unanswered, an answer came back with a number, or the server behind this file does not
/// answer the question at all — which is not zero usages and must not read as it.
/// </summary>
internal abstract record UsageLensState
{
    private UsageLensState() { }

    public sealed record Asking : UsageLensState;
    public sealed record Count(int Value) : UsageLensState;
    public sealed record Unsupported : UsageLensState;
}

/// <summary>
/// The declaration a lens stands for: the id folds and lenses share, the line the lens is keyed and
/// drawn by, and where the declaration's name is written — which is the position a server is asked
/// about, and not the same place the lens sits.
/// </summary>
internal readonly record struct UsageLensTarget(
    string Id, FileLine At, FileLine NameLine, RawColumn NameColumn);

/// <summary>
/// How the file on screen is used, addressed the way the painter asks: by the line each
/// declaration starts on. Held beside the rendered rows rather than inside them, because counts
/// come back one declaration at a time over the seconds after a file opens, and folding them in
/// would re-flatten the file — losing the reader's selection and their place in it — once per
/// answer. The rows themselves are emitted up front and stay put; only what they say changes.
/// </summary>
internal sealed class UsageLensOverlay
{
    public static readonly UsageLensOverlay Empty = new(string.Empty, new Dictionary<FileLine, UsageLensState>());

    private readonly IReadOnlyDictionary<FileLine, UsageLensState> _byLine;

    /// <summary>
    /// Takes a copy of <paramref name="states"/> rather than holding it. What collects the counts
    /// is a long-lived thing filling a map in as answers arrive, and sharing that map would let it
    /// change what is on screen without the view being told to repaint — so a count would surface
    /// whenever something unrelated happened to redraw, or not at all.
    /// </summary>
    public UsageLensOverlay(string path, IReadOnlyDictionary<FileLine, UsageLensState> states)
    {
        Path = path;
        _byLine = new Dictionary<FileLine, UsageLensState>(states);
    }

    public string Path { get; }

    public int Count => _byLine.Count;

    public bool IsEmpty => _byLine.Count == 0;

    /// <summary>What is known about the declaration starting on a line, or null when nothing has
    /// been asked about it yet — a lens with nothing to say draws nothing.</summary>
    public UsageLensState? On(FileLine line) => _byLine.TryGetValue(line, out var state) ? state : null;
}
