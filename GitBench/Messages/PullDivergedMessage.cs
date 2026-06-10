namespace GitBench;

// Broadcast when a pull on the *active* repo fails because local and upstream diverged — the one
// pull failure that's recoverable in-app. ActionsToolbarViewModel turns it into the reconcile
// dialog, keeping dialog construction in the view layer so RepoOperationsStore stays free of it.
public readonly record struct PullDivergedMessage(Repo Repo);
