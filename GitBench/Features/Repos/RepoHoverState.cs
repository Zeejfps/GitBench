using ZGF.Observable;

namespace GitBench.Features.Repos;

// The primary repo row the pointer is currently over, shared so the global hotkey gesture can target
// it for assignment (hover a row + Ctrl/Cmd+N pins that repo). Transient UI state — never persisted.
public sealed class RepoHoverState
{
    public State<Guid?> HoveredPrimary { get; } = new(null);

    public void Enter(Guid repoId) => HoveredPrimary.Value = repoId;

    // Clears only if this row is still the hovered one, so a stale exit firing after the next row's
    // enter doesn't wipe the newer hover.
    public void Exit(Guid repoId)
    {
        if (HoveredPrimary.Value == repoId) HoveredPrimary.Value = null;
    }
}
