namespace GitGui;

// Broadcast by DiffViewModel when the user clicks "open in new window" in the diff header.
// Carries the pinned target (path/side/sha) — not a frozen result — so the popped-out
// window's own DiffViewModel loads and stays live for that file, independent of the main
// window's selection. Handled by DiffWindowPresenter.
public readonly record struct OpenDiffWindowMessage(DiffTarget Target);
