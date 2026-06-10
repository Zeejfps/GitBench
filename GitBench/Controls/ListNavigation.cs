namespace GitBench.Controls;

/// <summary>
/// Shared helpers for keyboard arrow navigation over flat, single-select row lists
/// (the commit history and the commit-details file list). Keeps the "first press lands
/// on an edge, later presses clamp" rule identical across views.
/// </summary>
internal static class ListNavigation
{
    /// <summary>
    /// Index to move to when the user presses Up/Down by <paramref name="delta"/> rows.
    /// With nothing selected (<paramref name="currentIndex"/> &lt; 0) a downward press
    /// lands on the first row and an upward press on the last, so the cursor has a
    /// visible starting point. Otherwise the index moves by delta, clamped to the list
    /// bounds. Returns -1 for an empty list.
    /// </summary>
    public static int NextIndex(int count, int currentIndex, int delta)
    {
        if (count == 0) return -1;
        return currentIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : Math.Clamp(currentIndex + delta, 0, count - 1);
    }
}
