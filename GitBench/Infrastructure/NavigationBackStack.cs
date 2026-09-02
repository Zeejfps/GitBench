using System.Diagnostics.CodeAnalysis;

namespace GitBench.Infrastructure;

internal sealed class NavigationBackStack<T>
{
    public const int DefaultCapacity = 64;

    private readonly List<T> _places = [];
    private readonly int _capacity;

    public NavigationBackStack(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

    public bool CanGoBack => _places.Count > 0;

    public int Count => _places.Count;

    public void Push(T place)
    {
        _places.Add(place);
        if (_places.Count > _capacity) _places.RemoveAt(0);
    }

    public bool TryPop([MaybeNullWhen(false)] out T place)
    {
        if (_places.Count == 0)
        {
            place = default;
            return false;
        }

        place = _places[^1];
        _places.RemoveAt(_places.Count - 1);
        return true;
    }

    public void Clear() => _places.Clear();
}
