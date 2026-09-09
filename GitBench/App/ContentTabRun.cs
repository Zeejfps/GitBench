using GitBench.Features.FileBrowser;
using GitBench.Features.Terminal;
using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>One tab in the content panel's run: a shell, or a file.</summary>
/// <remarks>
/// A closed union rather than an interface either side implements, because neither a terminal nor
/// an open file should have to know it is drawn in a strip beside the other. This is the strip's
/// vocabulary, and it lives with the strip.
/// </remarks>
internal abstract record ContentTab
{
    /// <summary>Where this tab sits in the run. Read through to whatever the tab is of, so a
    /// wrapper made now and one made a moment ago answer the same.</summary>
    internal abstract long OpenedAt { get; }

    /// <summary>Puts this tab at a new place in the run. Written through for the same reason it is
    /// read through: the order has to outlive these wrappers, which are made and dropped on every
    /// change to either source list.</summary>
    internal abstract void Reorder(long at);

    internal sealed record Shell(TerminalInstance Instance) : ContentTab
    {
        internal override long OpenedAt => Instance.OpenedAt;

        internal override void Reorder(long at) => Instance.OpenedAt = at;
    }

    internal sealed record File(FileBrowserTab Tab) : ContentTab
    {
        internal override long OpenedAt => Tab.OpenedAt;

        internal override void Reorder(long at) => Tab.OpenedAt = at;
    }
}

/// <summary>
/// One repository's open tabs in the order they were opened, mirrored from the two lists that own
/// them.
/// </summary>
/// <remarks>
/// A new tab goes on the end, whatever kind it is and whatever kind is already there — asking for a
/// shell with four files open should not slot the shell in front of them, and opening a file with
/// two shells running should not slot it in front of those. Neither list can answer that alone, so
/// this one merges them on the open order both sides stamp their tabs with.
/// <para>
/// Mirrored incrementally rather than rebuilt: a transient tab being replaced is one removal and one
/// insertion, and it happens on every arrow key down the tree.
/// </para>
/// </remarks>
internal sealed class ContentTabRun : IDisposable
{
    private readonly ObservableList<ContentTab> _tabs = new();
    private readonly IDisposable? _shells;
    private readonly IDisposable? _files;

    private readonly ObservableList<TerminalInstance>? _shellSource;
    private readonly ObservableList<FileBrowserTab>? _fileSource;

    /// <summary>The two lists only, not the things that own them: what this does is weave two
    /// orders into one, and neither a terminal store nor a file browser is needed to say that.</summary>
    public ContentTabRun(ObservableList<TerminalInstance>? shells, ObservableList<FileBrowserTab>? files)
    {
        _shellSource = shells;
        _fileSource = files;

        _shells = shells?.Subscribe(change =>
            Apply(change, instance => new ContentTab.Shell(instance)));
        _files = files?.Subscribe(change =>
            Apply(change, tab => new ContentTab.File(tab)));
    }

    public ObservableList<ContentTab> Tabs => _tabs;

    /// <summary>
    /// Moves one tab to another slot, and renumbers the run so the arrangement is the order.
    /// </summary>
    /// <remarks>
    /// Renumbered rather than only moved, because the order is what a fresh run seeds itself from
    /// and this one does not live long: switching repositories away and back builds another, and an
    /// arrangement held only in this list would not survive the trip.
    /// </remarks>
    public void Move(int from, int to)
    {
        if (from == to) return;
        if (from < 0 || from >= _tabs.Count || to < 0 || to >= _tabs.Count) return;

        _tabs.Move(from, to);
        foreach (var tab in _tabs) tab.Reorder(OpenOrder.Next());
    }

    private void Apply<T>(ListChange<T> change, Func<T, ContentTab> wrap) where T : class
    {
        switch (change.Kind)
        {
            case ListChangeKind.Added:
                Insert(wrap(change.Item!));
                break;
            case ListChangeKind.Removed:
                _tabs.Remove(wrap(change.OldItem!));
                break;
            case ListChangeKind.Replaced:
                _tabs.Remove(wrap(change.OldItem!));
                Insert(wrap(change.Item!));
                break;
            // Reset arrives once on subscribing and Cleared on a source emptying; neither says what
            // moved, so both are answered by reading the sources again.
            default:
                Reseed();
                break;
        }
    }

    /// <summary>Puts a tab where its open order says it goes, which for a genuinely new one is the
    /// end.</summary>
    private void Insert(ContentTab tab)
    {
        var at = _tabs.Count;
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].OpenedAt <= tab.OpenedAt) continue;
            at = i;
            break;
        }

        _tabs.Insert(at, tab);
    }

    private void Reseed()
    {
        _tabs.Clear();
        if (_shellSource is { } shells)
            foreach (var instance in shells) Insert(new ContentTab.Shell(instance));
        if (_fileSource is { } files)
            foreach (var tab in files) Insert(new ContentTab.File(tab));
    }

    public void Dispose()
    {
        _shells?.Dispose();
        _files?.Dispose();
        _tabs.Clear();
    }
}
