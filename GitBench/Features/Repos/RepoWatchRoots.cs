namespace GitBench.Features.Repos;

// What a watcher rooted at a given path is entitled to conclude from an event. The recursive
// working-tree root sees everything and splits by path; each narrow root carries one thing, because
// one thing is all it can see.
internal enum WatchRootKind
{
    // Recursive working tree, gitdir subtree included: split by path into the classifier or the
    // working-tree channel.
    WorkingTree,

    // The resolved gitdir, or a subtree of it. Every path underneath is gitdir-relative, so it goes
    // straight to ClassifyGitChange.
    GitDir,

    // The working-tree root, non-recursive, watched for `.gitmodules` alone.
    Gitmodules,
}

// What a recursive watch costs, which is the only thing the root layout turns on.
internal enum WatchCost
{
    // Windows (ReadDirectoryChangesW) and macOS (FSEvents): one handle covers an entire tree, drawn
    // from this process's own quota. Watching the working tree is free, so we do, and a local edit
    // shows up instantly.
    RecursiveIsOneHandle,

    // Linux (inotify): one instance plus one watch per *directory* in the subtree, both drawn from a
    // per-user budget shared with every other application the user is running — so a recursive watch
    // on a working tree is 10k-50k watches out of a pool an editor also needs. See WatcherDiagnostics.
    RecursiveIsOneWatchPerDirectory,
}

// Optional marks the roots git creates on demand: absent is normal there, and must not be reported
// as a failure to watch.
internal readonly record struct WatchRoot(
    string Path,
    bool Recursive,
    WatchRootKind Kind,
    bool Optional = false);

internal static class RepoWatchRoots
{
    public static WatchCost CurrentCost => OperatingSystem.IsLinux()
        ? WatchCost.RecursiveIsOneWatchPerDirectory
        : WatchCost.RecursiveIsOneHandle;

    // Takes the cost as an argument rather than reading the OS, because the expensive branch is the
    // one nobody developing this can run: both layouts have to be reachable from a unit test on any
    // platform, or the Linux one ships unexercised.
    public static IReadOnlyList<WatchRoot> For(string workingTree, string gitDir, WatchCost cost)
    {
        if (cost == WatchCost.RecursiveIsOneHandle)
            return [new WatchRoot(workingTree, Recursive: true, WatchRootKind.WorkingTree)];

        // Roughly 5-50 watches per repo instead of one per directory in the working tree. What this
        // gives up is instant feedback on a local edit; RepoReconcileService's focus gain and tick
        // are the refresh path for that, which is what comparable git clients do on every platform.
        return
        [
            // Non-recursive: HEAD, packed-refs, FETCH_HEAD, ORIG_HEAD, MERGE_HEAD, and refs/,
            // worktrees/ or modules/ appearing or vanishing. Not recursive because objects/ is the
            // bulk of a gitdir and the classifier already ignores every path under it; modules/ is
            // each submodule's whole gitdir, and submodules are their own registry entries with
            // their own roots.
            new WatchRoot(gitDir, Recursive: false, WatchRootKind.GitDir),

            // Loose ref moves — branches, remotes, tags. One watch per namespace segment, so tens,
            // and `git gc` packs them into packed-refs (covered by the root above) as they grow.
            new WatchRoot(Path.Combine(gitDir, "refs"), Recursive: true, WatchRootKind.GitDir, Optional: true),

            // Only exists once `git worktree add` has run. Covers linked worktrees that aren't open
            // in GitBench; the ones that are get their own entry, and their own roots, from here.
            new WatchRoot(Path.Combine(gitDir, "worktrees"), Recursive: true, WatchRootKind.GitDir, Optional: true),

            // One watch, and `.gitmodules` is the only thing on it we act on. It would also report
            // top-level working-tree edits, but acting on those would make refresh latency depend on
            // how deep a file happens to sit — worse than uniformly polled. It is here because an
            // external `.gitmodules` edit is the one working-tree signal with no polling equivalent.
            new WatchRoot(workingTree, Recursive: false, WatchRootKind.Gitmodules),
        ];
    }
}
