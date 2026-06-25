namespace GitBench.Messages;

// Broadcast right after a successful push/pull so the RepoBar/toolbar ahead-behind numbers snap to
// their known outcome immediately instead of waiting for the reconciling `git status` probe: a push
// leaves the branch with nothing left to send (Ahead 0); a pull leaves it level with the upstream it
// pulled (Behind 0). A null component means "leave that count to the probe" — fetch sends nothing
// here because its new behind count isn't known without reading. RepoStatusStore applies the patch
// and the probe (kicked by the accompanying RefsChangedMessage) confirms it a beat later.
public readonly record struct RemoteSyncOptimisticMessage(Guid RepoId, int? Ahead, int? Behind);
