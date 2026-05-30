using ZGF.Observable;

namespace GitGui;

// Shared so the Stash dialog (constructed from the toolbar) can read what the user
// has selected in LocalChangesView's Unstaged panel and pre-check the matching rows.
// Selection is otherwise owned by LocalChangesViewModel, whose lifetime is per-view.
public sealed class LocalChangesSelectionStore
{
    public State<IReadOnlyList<string>> UnstagedPaths { get; } = new(Array.Empty<string>());
}
