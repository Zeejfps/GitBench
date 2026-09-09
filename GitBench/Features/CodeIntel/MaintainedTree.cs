using TreeSitter;
using TreeSitter.Bindings;

namespace GitBench.Features.CodeIntel;

/// <summary>
/// One parse tree kept across the edits of the buffer it describes, so a keystroke costs a re-parse
/// of what it touched rather than of the file.
/// </summary>
/// <remarks>
/// <para>
/// The buffer is authoritative and the tree is a cache of it. A wrong edit does not throw — it
/// produces a plausible tree of a file nobody has — so every incremental re-parse is checked against
/// the one invariant that is cheap to state: the root node covers the whole buffer. A tree that
/// fails it, or a re-parse that throws, is thrown away and the buffer parsed whole, which costs
/// about a millisecond. That turns the design's one silent failure into a hiccup.
/// </para>
/// <para>
/// Not thread-safe, and not meant to be: one of these belongs to one document on one worker. It may
/// be re-parsed by a different pooled parser each time — that is the normal case here, since a pool
/// hands out whichever session is idle — because a tree is not bound to the parser that made it.
/// </para>
/// </remarks>
internal sealed class MaintainedTree : IDisposable
{
    private readonly ParseSessionPool _pool;
    private SyntaxTree? _tree;

    internal MaintainedTree(ParseSessionPool pool) => _pool = pool;

    /// <summary>How many times an edit produced a tree that did not describe the buffer, and the
    /// buffer had to be parsed whole instead.</summary>
    public int Fallbacks { get; private set; }

    /// <summary>Follows one edit into the tree. Does nothing where there is no tree yet: the buffer
    /// is already past that edit, so the next <see cref="Current"/> parses it as it now stands.</summary>
    public void Advance(byte[] utf8, in TSInputEdit edit)
    {
        if (_tree is not { } old) return;

        _tree = null;
        SyntaxTree next;
        try
        {
            // Reparse consumes the old tree on every path, including a throwing one, so nothing
            // here can leave an edited-but-unparsed tree reachable.
            next = _pool.Use((old, edit, utf8), static (session, s) => s.old.Reparse(session.Parser, in s.edit, s.utf8));
        }
        catch (Exception)
        {
            Fallbacks++;
            return;
        }

        if (next.RootNode.EndByte == (uint)utf8.Length)
        {
            _tree = next;
            return;
        }

        next.Dispose();
        Fallbacks++;
    }

    /// <summary>The tree of <paramref name="utf8"/>, parsing it whole where no tree has survived the
    /// edits that led here.</summary>
    public SyntaxTree Current(byte[] utf8) =>
        _tree ??= _pool.Use(utf8, static (session, bytes) => session.Parser.Parse(bytes));

    /// <summary>Throws the tree away, for a buffer that was replaced rather than edited.</summary>
    public void Discard()
    {
        _tree?.Dispose();
        _tree = null;
    }

    public void Dispose() => Discard();
}
