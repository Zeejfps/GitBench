using GitBench.Features.Markdown.Parsing;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Markdown;

/// <summary>
/// The streaming model of Step 7 (docs/plans/markdown-renderer.md): a stable-identity block list
/// that a streamed markdown text feeds and the transcript binds with <c>Each</c>. Contract, pinned
/// by <c>MarkdownBlockListTests</c>:
/// <list type="bullet">
/// <item><description><b>Result:</b> after <see cref="SetText"/>, <see cref="Blocks"/> is
/// element-wise structurally equal to <c>parser.Parse(text).Blocks</c> — streaming never shows a
/// different document than a one-shot parse would.</description></item>
/// <item><description><b>Retention:</b> a slot whose new parse value equals its current value
/// keeps its current <see cref="MarkdownBlock"/> <em>instance</em> and fires no event. The parser
/// allocates fresh (equal-by-value) records on every parse, so the diff must keep the old instance
/// rather than adopt the new one — that reference stability is what lets <c>Each</c>-bound views
/// and their layout caches survive untouched. (<see cref="ObservableList{T}.Replace"/> already
/// no-ops on equal values, keeping the old instance; the diff must not bypass that with
/// remove/re-add.)</description></item>
/// <item><description><b>Minimal tail:</b> every mutation event lands at or after the first index
/// where the old and new block sequences diverge — the common streaming delta is exactly one
/// <c>Replaced</c> at the last index (block still growing) or one <c>Added</c> (new block opened);
/// retracted text produces <c>Removed</c> for the dropped tail slots only. Never <c>Cleared</c>
/// while an equal prefix exists.</description></item>
/// <item><description><b>Throttle:</b> <see cref="SetTextThrottled"/> only records the pending
/// text (latest call wins, nothing parses); the next <see cref="Tick"/> applies it with at most
/// one parse, exactly as an immediate <see cref="SetText"/> of that text would. <see cref="SetText"/>
/// bypasses and clears any pending text. When a ticker is supplied, pending text registers a
/// frame tick that applies it and then unregisters — the frame loop wakes only while an update is
/// pending (the <c>Pulse</c>/<c>Tween</c> convention) and <see cref="Tick"/> stays manually
/// drivable in tests without a ticker.</description></item>
/// </list>
/// Single-threaded, like the <see cref="ObservableList{T}"/> it owns: call from the UI thread.
/// Disposing drops any pending text and leaves the ticker, so a surface that goes away takes its
/// registration with it.
/// </summary>
internal sealed class MarkdownBlockList : IDisposable
{
    private readonly IMarkdownParser _parser;
    private readonly IFrameTicker? _ticker;
    private readonly Action<float> _tick;

    private string _text = string.Empty;
    private string? _pendingText;

    /// <param name="parser">The parser used for every re-parse. Never throws per its seam.</param>
    /// <param name="ticker">Optional frame ticker for the throttled path: pending text registers
    /// a tick to apply itself next frame and unregisters afterwards. Null keeps the throttle
    /// manual — callers drive <see cref="Tick"/> themselves (tests do).</param>
    public MarkdownBlockList(IMarkdownParser parser, IFrameTicker? ticker = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
        _ticker = ticker;
        _tick = _ => Tick();
    }

    /// <summary>The live block rows, mutated minimally on each applied text. Bind with
    /// <c>Each.Of(Blocks, …)</c>; nothing else may mutate it.</summary>
    public ObservableList<MarkdownBlock> Blocks { get; } = new();

    /// <summary>The last applied (parsed) text; empty for a fresh list. Pending throttled text is
    /// not reflected here until it applies.</summary>
    public string Text => _text;

    /// <summary>True while a <see cref="SetTextThrottled"/> update awaits its <see cref="Tick"/>.</summary>
    public bool HasPendingText => _pendingText is not null;

    /// <summary>Parses <paramref name="text"/> now and applies the minimal-tail diff to
    /// <see cref="Blocks"/> (exactly one parse). Clears any pending throttled text.</summary>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ClearPending();
        Apply(text);
    }

    /// <summary>Records <paramref name="text"/> as the pending update without parsing; successive
    /// calls coalesce (latest wins) and the next <see cref="Tick"/> — driven by the ctor's ticker
    /// when present — applies it.</summary>
    public void SetTextThrottled(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Wake the frame loop on the pending edge only — coalesced updates keep the one tick.
        if (_pendingText is null)
            _ticker?.Add(_tick);
        _pendingText = text;
    }

    /// <summary>Applies the pending throttled text, if any, with at most one parse; a no-op
    /// otherwise. The manual drive for the throttle — tests call it in place of a frame.</summary>
    public void Tick()
    {
        if (_pendingText is not { } text) return;
        ClearPending();
        Apply(text);
    }

    /// <summary>Abandons the pending update and the ticker registration behind it: the owner calls
    /// this when the surface bound to these blocks is gone, so no parse runs for a view that no
    /// longer exists.</summary>
    public void Dispose() => ClearPending();

    /// <summary>Drops any pending throttled text and parks the frame tick, so the loop only stays
    /// awake while an update is actually pending.</summary>
    private void ClearPending()
    {
        if (_pendingText is null) return;
        _pendingText = null;
        _ticker?.Remove(_tick);
    }

    /// <summary>One parse, one pass: re-parses <paramref name="text"/> and mutates
    /// <see cref="Blocks"/> minimally. Per-index <see cref="ObservableList{T}.Replace"/> over the
    /// shared range no-ops on structurally equal slots (retaining the old instance and firing
    /// nothing), then the tail shrinks from the end or grows by appending — so every event lands
    /// at or after the first divergent index.</summary>
    private void Apply(string text)
    {
        _text = text;
        var parsed = _parser.Parse(text).Blocks;

        if (parsed.Count == 0)
        {
            // Nothing survives an empty document; Clear itself no-ops on an already-empty list.
            Blocks.Clear();
            return;
        }

        var oldCount = Blocks.Count;
        var shared = Math.Min(oldCount, parsed.Count);
        for (var i = 0; i < shared; i++)
            Blocks.Replace(i, parsed[i]);

        // Retraction: drop dropped tail slots end-first, keeping every event index in the tail.
        for (var i = oldCount - 1; i >= parsed.Count; i--)
            Blocks.RemoveAt(i);

        // Growth: newly opened blocks append after the shared range.
        for (var i = oldCount; i < parsed.Count; i++)
            Blocks.Add(parsed[i]);
    }
}
