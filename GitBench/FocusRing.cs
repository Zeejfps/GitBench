namespace GitGui;

/// <summary>
/// A simple ordered focus cycle. Each stop supplies how to focus itself (and optionally how
/// to release focus); the ring drives Tab / Shift+Tab transitions between them, blurring the
/// current stop before focusing the next. Tab from the last stop wraps to the first.
/// </summary>
internal sealed class FocusRing
{
    public sealed class Stop
    {
        public required Action Focus { get; init; }
        public Action? Blur { get; init; }

        // When set and returning false, the stop is skipped during traversal — e.g. the
        // commit button is only a stop while it's enabled.
        public Func<bool>? CanFocus { get; init; }
    }

    private readonly List<Stop> _stops = new();

    public Stop Add(Action focus, Action? blur = null, Func<bool>? canFocus = null)
    {
        var stop = new Stop { Focus = focus, Blur = blur, CanFocus = canFocus };
        _stops.Add(stop);
        return stop;
    }

    public void Next(Stop current) => Move(current, 1);
    public void Previous(Stop current) => Move(current, -1);

    private void Move(Stop current, int direction)
    {
        var index = _stops.IndexOf(current);
        if (index < 0 || _stops.Count < 2) return;
        var count = _stops.Count;
        for (var step = 1; step <= count; step++)
        {
            var nextIndex = ((index + direction * step) % count + count) % count;
            var next = _stops[nextIndex];
            if (ReferenceEquals(next, current)) break;
            var canFocus = next.CanFocus == null || next.CanFocus();
            Console.WriteLine($"[focusdbg] Move from {index} dir {direction} -> candidate {nextIndex} canFocus={canFocus}");
            if (!canFocus) continue;
            current.Blur?.Invoke();
            next.Focus();
            return;
        }
    }
}
