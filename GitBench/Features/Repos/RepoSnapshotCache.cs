using GitBench.Git;

namespace GitBench.Features.Repos;

/// <summary>
/// Small bounded most-recently-used cache of per-repo snapshots, keyed by <see cref="Repo.Id"/>.
/// Lets a view model show the last-known data for a repo instantly on switch-back while a fresh
/// load runs in the background (soft refresh), instead of tearing down to a "Loading…"
/// placeholder and re-running the full git query on every switch.
///
/// Not thread-safe by design: every access happens on the UI thread — view-model
/// <c>Update</c> calls and the <c>onResult</c> continuations <c>RunBackground</c> posts back to
/// the dispatcher. The background git work itself never touches the cache.
///
/// Bounded to <see cref="Capacity"/> entries; the least-recently-used repo is evicted when full,
/// which caps memory for users who churn through many repos. A cached snapshot is only ever a
/// display head-start — the caller always kicks a fresh load alongside showing it, so a stale
/// entry self-corrects within one refresh.
/// </summary>
internal sealed class RepoSnapshotCache<T> where T : class
{
    public const int DefaultCapacity = 8;

    private readonly int _capacity;
    // Most-recently-used at the head; the tail is the next eviction victim.
    private readonly LinkedList<Guid> _order = new();
    private readonly Dictionary<Guid, (LinkedListNode<Guid> Node, T Value)> _entries = new();

    public RepoSnapshotCache(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    /// <summary>
    /// Returns the cached value for <paramref name="repoId"/> and marks it most-recently-used.
    /// <paramref name="value"/> is null when there is no entry.
    /// </summary>
    public bool TryGet(Guid repoId, out T? value)
    {
        if (_entries.TryGetValue(repoId, out var entry))
        {
            _order.Remove(entry.Node);
            _order.AddFirst(entry.Node);
            value = entry.Value;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>
    /// Inserts or replaces the cached value for <paramref name="repoId"/>, marking it
    /// most-recently-used and evicting the least-recently-used entry if that pushes past capacity.
    /// </summary>
    public void Set(Guid repoId, T value)
    {
        if (_entries.TryGetValue(repoId, out var existing))
        {
            _order.Remove(existing.Node);
            _order.AddFirst(existing.Node);
            _entries[repoId] = (existing.Node, value);
            return;
        }

        var node = new LinkedListNode<Guid>(repoId);
        _order.AddFirst(node);
        _entries[repoId] = (node, value);

        if (_entries.Count > _capacity)
        {
            var victim = _order.Last!;
            _order.RemoveLast();
            _entries.Remove(victim.Value);
        }
    }

    /// <summary>Drops the entry for <paramref name="repoId"/> if present (e.g. after a failed load).</summary>
    public void Remove(Guid repoId)
    {
        if (_entries.Remove(repoId, out var entry))
            _order.Remove(entry.Node);
    }

    public void Clear()
    {
        _entries.Clear();
        _order.Clear();
    }
}
