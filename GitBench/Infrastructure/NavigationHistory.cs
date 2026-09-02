using System.Diagnostics.CodeAnalysis;

namespace GitBench.Infrastructure;

/// <summary>
/// A browser's two stacks: the places walked away from, and the places walked back from. Stepping
/// either way hands the place being left to the opposite stack, which is what makes forward the
/// exact inverse of back; going somewhere new drops the forward stack, because the trail it holds
/// no longer leads anywhere the reader has been.
/// </summary>
internal sealed class NavigationHistory<T> where T : class
{
    public const int DefaultCapacity = 64;

    private readonly List<T> _back = [];
    private readonly List<T> _forward = [];
    private readonly int _capacity;

    public NavigationHistory(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public void Push(T place)
    {
        _back.Add(place);
        if (_back.Count > _capacity) _back.RemoveAt(0);
        _forward.Clear();
    }

    /// <param name="leaving">Where the reader is now, for the opposite stack. Null when there is no
    /// place to come back to — nothing open — which loses the step rather than recording a return
    /// to nowhere.</param>
    public bool TryGoBack(T? leaving, [MaybeNullWhen(false)] out T place) =>
        Step(_back, _forward, leaving, out place);

    public bool TryGoForward(T? leaving, [MaybeNullWhen(false)] out T place) =>
        Step(_forward, _back, leaving, out place);

    private bool Step(List<T> from, List<T> to, T? leaving, [MaybeNullWhen(false)] out T place)
    {
        if (from.Count == 0)
        {
            place = default;
            return false;
        }

        place = from[^1];
        from.RemoveAt(from.Count - 1);
        if (leaving is not null)
        {
            to.Add(leaving);
            if (to.Count > _capacity) to.RemoveAt(0);
        }
        return true;
    }
}
