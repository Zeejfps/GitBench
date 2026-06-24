namespace GitBench.Controls;

/// <summary>
/// One action a user can take on a selected row, defined once and consumed by both the row's
/// context menu and its keyboard handler — so a shortcut and its menu hint stay in lockstep. A null
/// <see cref="Gesture"/> means the action is menu-only (no keyboard binding).
/// </summary>
public sealed record RowAction(
    string Label,
    Action Invoke,
    string? Icon = null,
    KeyGesture? Gesture = null,
    bool Enabled = true);
