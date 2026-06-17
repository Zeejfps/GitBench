using ZGF.Observable;

namespace GitBench.Infrastructure;

/// <summary>
/// Projects a derived, ordered domain list into a stable <see cref="ObservableList{TVm}"/> of view
/// models: the view model whose key persists across a recompute is reused (and moved into place),
/// new keys create one, dropped keys dispose theirs. The counterpart to
/// <c>ObservableList.Map</c> for sources that are filtered/ordered/computed rather than a 1:1
/// mirror — so a parent view model can feed a derived child list to an <c>Each</c> without
/// remounting every row on each change.
/// </summary>
internal sealed class KeyedViewModelList<T, TKey, TVm> : IDisposable
    where TKey : notnull
    where TVm : class, IDisposable
{
    private readonly Func<T, TKey> _keyOf;
    private readonly Func<T, TVm> _create;
    private readonly Dictionary<TKey, TVm> _byKey = new();
    private readonly List<TKey> _order = new();
    private readonly ObservableList<TVm> _items = new();
    private readonly IDisposable _subscription;

    public ObservableList<TVm> Items => _items;

    public KeyedViewModelList(IReadable<IReadOnlyList<T>> source, Func<T, TKey> keyOf, Func<T, TVm> create)
    {
        _keyOf = keyOf;
        _create = create;
        // Subscribe fires immediately with the current value, seeding the initial set.
        _subscription = source.Subscribe(Reconcile);
    }

    private void Reconcile(IReadOnlyList<T> next)
    {
        var nextKeys = new List<TKey>(next.Count);
        var nextSet = new HashSet<TKey>(next.Count);
        foreach (var item in next)
        {
            var key = _keyOf(item);
            nextKeys.Add(key);
            nextSet.Add(key);
        }

        // Drop view models whose key is gone (back-to-front keeps indices valid).
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            var key = _order[i];
            if (nextSet.Contains(key)) continue;
            var vm = _byKey[key];
            _order.RemoveAt(i);
            _byKey.Remove(key);
            _items.RemoveAt(i);
            vm.Dispose();
        }

        // Reorder survivors and insert newcomers so the list matches `next` position-for-position.
        for (var index = 0; index < next.Count; index++)
        {
            var key = nextKeys[index];
            if (_byKey.TryGetValue(key, out var existing))
            {
                var current = _order.IndexOf(key);
                if (current != index)
                {
                    _order.RemoveAt(current);
                    _order.Insert(index, key);
                    _items.Move(current, index);
                }
            }
            else
            {
                var vm = _create(next[index]);
                _byKey[key] = vm;
                _order.Insert(index, key);
                _items.Insert(index, vm);
            }
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        foreach (var vm in _items) vm.Dispose();
        _items.Clear();
        _byKey.Clear();
        _order.Clear();
    }
}
