# GitBench Codebase Cleanup — Top 3

Goals: more readable to humans, invalid state unrepresentable (types over if/else corner-case fixing), little to no duplication.

The codebase is in decent shape structurally (MVVM, immutable snapshots, message bus), but three patterns work against all three goals at once.

## 1. Replace the ~45 `XyzOutcome(bool Success, string? ErrorMessage, bool SomeFlag)` records with one real result type — and one operation runner

The single biggest win: fixes invalid-state representability *and* duplication *and* readability in one move.

`IGitService.cs:119-271` defines ~45 near-identical records like `PushOutcome(bool Success, string? ErrorMessage)`, `MergeOutcome(bool Success, string? ErrorMessage, bool HasConflicts = false)`, `PullOutcome(..., bool HasDivergedBranches = false)`. Every one of them can represent nonsense: `(Success: true, ErrorMessage: "boom")`, `(Success: false, HasConflicts: true)`. Callers compensate with if/else corner-case checks.

Worse, ~30 methods in `GitService.cs` (Push, Pull, CreateBranch, DeleteBranch, CreateTag, ApplyStash, AddWorktree, …) wrap these in an identical 10–15-line skeleton: `IsGitRepo` check → `LockRepo` → run → `result.Ok ? success : BlockError` → `catch (Exception ex) => (false, ex.Message)`. That's several hundred lines of pure ceremony.

**Fix:** a sealed result hierarchy (`Succeeded | Failed(string Message) | Conflicted` — extend per-operation only where genuinely needed) plus a single `RunOperation(repo, args, mapResult)` helper that owns the repo check, lock, and exception handling. Most of those 30 methods collapse to 2–4 lines, error text becomes impossible on the success path, and `switch` exhaustiveness replaces flag-checking.

## 2. Turn the ViewModels' boolean-flag soup into explicit typed states

The ViewModels encode hidden state machines in independent fields that must be kept in sync by hand:

- `CommitsViewModel.cs:51-53` — `_isCheckingOutCommit`, `_isMovingBranch`, `_isApplyingCommit`: three booleans where the real state is "at most one operation in flight."
- `LocalChangesViewModel.cs:71-77` — `_amend` (nullable), `_mergeActive` (bool), `_renderedRepoId` (nullable Guid), mutated from three different places (`SetAmend`, `HandleMergeState`, `OnStoreLocalChanges`). The 91-line `OnStoreLocalChanges` (lines 772-863) is five nested branches existing purely to navigate combinations of these flags, and there's a real race window around lines 844-860 where a stale amend session can apply after a repo switch.
- `BranchesViewModel` uses a *different* convention for the same concept (`BusyBranch` string, checked 50+ times as `!= null`).

**Fix:** model each as one field of a sealed type — e.g. `PendingOp = None | CheckingOut(sha) | MovingBranch(name) | Applying(sha)` and `EditorMode = Normal | Amending(AmendSession) | Merging(message)`. The nested guard cascades become a single exhaustive `switch`, "amending while merging" becomes unrepresentable, and the three ViewModels stop inventing three different busy-flag dialects. Pair this with extracting the snapshot-load skeleton (null → loading, error → show error, else apply) that all four ViewModels currently reimplement.

## 3. Extract the UI layer's copy-pasted building blocks — dialogs and scroll sync first

The pure-duplication win, and very mechanical:

- **`NotifyScrollChanged` is the same algorithm pasted four times**: `DiffContentView.cs:975-1036`, `CommitsView.cs:288-316`, `BranchesView.cs:600-628`, `LocalChangesPanel.cs:398-431`. One `ScrollMath.Normalize(contentSize, viewport, scrollPos)` helper deletes ~150 lines and ends the risk of the four copies drifting (they already differ slightly).
- **30 dialogs repeat the same scaffolding**, and worse, `BuildLabeledRow` is reimplemented verbatim in `ResetCommitDialog.cs:84-110`, `MergeBranchDialog.cs:89-115`, and `CreateTagDialog.cs:103-125`; `BuildCommitValue` likewise (~40 lines × 3). Checkbox `Height = 22` is hardcoded in 8+ dialogs. A small `DialogFields` helper class (labeled rows, commit-value chips, standard checkbox/input constructors) cuts roughly 400 lines and makes the dialogs visually consistent by construction.
- Per-view `TextStyle` farms (16 instances in `BranchesView.cs:45-79` alone) — a shared themed-style palette would shrink every view's preamble.

## Deep dive: item 1 — the Outcome redesign

### Exact inventory (30 record types, 3 shapes)

**Shape A — 20 identical `(bool Success, string? ErrorMessage)` clones.** `PushOutcome`, `FetchOutcome`, `FastForwardOutcome`, `CheckoutOutcome`, `ResetOutcome`, `CreateBranchOutcome`, `MoveBranchOutcome`, `CreateTagOutcome`, `DeleteTagOutcome`, `RenameBranchOutcome`, `DeleteBranchOutcome`, `DeleteRemoteBranchOutcome`, `EditRemoteOutcome`, `SetLocalIdentityOutcome`, `ResolveOutcome` (`IGitService.cs:193-245`), plus `WorktreeAddOutcome`/`WorktreeRemoveOutcome`/`WorktreePruneOutcome` (`Worktree.cs:20-24`) and `SubmoduleAddOutcome`/`SubmoduleDeinitOutcome` (`Submodule.cs:54,73`). Byte-for-byte the same shape; the type name carries zero information the method name doesn't already.

**Shape B — 6 with `bool HasConflicts = false`.** `MergeOutcome`, `RebaseOutcome`, `CherryPickOutcome`, `RevertCommitOutcome`, `StashOutcome`, `SubmoduleUpdateOutcome`. The semantics (per the comment at `IGitService.cs:134-137`) are "started successfully but left conflicts" — i.e. a *third state*, encoded as a flag combination `(true, null, true)` where `(false, msg, true)` is meaningless but representable. Bonus invalid state: `StashOutcome` is shared by `CreateStash`/`ApplyStash`/`DropStash`/`RenameStash`, but `HasConflicts` is only meaningful for apply — three of the four methods carry a flag that must never be set.

**Shape C — 4 bespoke.** `PullOutcome` (+`HasDivergedBranches` — only meaningful on *failure*), `AbortOperationOutcome` (+`ForceQuitAvailable` — only meaningful on failure), `ContinueOperationOutcome` (+`HasMoreConflicts` — a third state like Shape B), `CloneOutcome` (+`RepoPath` — only meaningful on *success*, nullable anyway).

### The error channel is actually triple-redundant

The waste compounds across layers:

1. `GitService` catches exceptions and folds them into `Outcome(false, ex.Message)` (~30 try/catch blocks).
2. `ViewModelBase.RunBackground` (`ViewModelBase.cs:80-107`) *also* catches exceptions into a separate `string? Error` tuple channel.
3. Every caller therefore reconciles both: `if (error != null || !outcome!.Success)` … `(error ?? outcome?.ErrorMessage) ?? "Stash apply failed."` (`BranchesViewModel.cs:461-465`, and 40+ similar `.Success` checks across Features/).

And it propagates: `RepoOperationsStore.cs:115` unpacks `PullOutcome` into yet another ad-hoc tuple `(o.Success, o.Success ? null : o.ErrorMessage ?? "Pull failed.", o.HasDivergedBranches)`.

### Target design

One closed hierarchy per genuine shape — 6 types replace 30. The private base ctor + nested derived records makes the hierarchy truly closed (nothing outside can add a case):

```csharp
public abstract record GitOutcome
{
    private GitOutcome() { }
    public static readonly GitOutcome Ok = new Success();
    public sealed record Success : GitOutcome;
    public sealed record Failed(string Message) : GitOutcome;
}

// Merge, Rebase, CherryPick, Revert, ApplyStash, UpdateSubmodules
public abstract record MergeLikeOutcome
{
    private MergeLikeOutcome() { }
    public sealed record Completed : MergeLikeOutcome;
    public sealed record Conflicted : MergeLikeOutcome;   // op started, conflicts left to resolve
    public sealed record Failed(string Message) : MergeLikeOutcome;
}

public abstract record PullOutcome     // Ok | Diverged | Failed(msg)
public abstract record AbortOutcome    // Ok | Failed(msg, bool ForceQuitAvailable)
public abstract record ContinueOutcome // Ok | MoreConflicts | Failed(msg)
public abstract record CloneOutcome    // Cloned(string RepoPath) | Failed(msg)
```

Every previously-invalid combination is now unrepresentable: no error text on success, no conflict flag on failure, no null `RepoPath` on a successful clone, no `HasConflicts` on `CreateStash`. Callers consume with a switch expression; the sealed hierarchy plus CS8509 gives near-exhaustiveness checking, so adding a case breaks every consumer at build time instead of silently falling through a flag check.

Note the codebase already has the right instinct: `MergePreviewResult` uses a proper `MergePreviewState` enum (`IGitService.cs:112-119`). This design just extends that to the operations themselves.

### The operation runner (GitService side)

Two layers inside `GitService`:

```csharp
// Owns the repo check, lock, and exception fold — used by everything.
private GitOutcome RunOperation(Repo repo, Func<GitOutcome> body)
{
    try
    {
        if (!IsGitRepo(repo.Path)) return new GitOutcome.Failed("Not a git repository.");
        using var _ = LockRepo(repo.Path);
        return body();
    }
    catch (Exception ex) { return new GitOutcome.Failed(ex.Message); }
}

// The whole happy path for the ~20 Shape-A methods.
private GitOutcome RunSimple(Repo repo, string label, params string[] args)
    => RunOperation(repo, () =>
    {
        var result = _runner.Run(repo.Path, args);
        return result.Ok ? GitOutcome.Ok : new GitOutcome.Failed(result.FirstLineError(label));
    });
```

Before (`GitService.cs:1710-1724`, repeated ~30 times):

```csharp
public CheckoutOutcome CheckoutLocalBranch(Repo repo, string branchName)
{
    try
    {
        if (!IsGitRepo(repo.Path))
            return new CheckoutOutcome(false, "Not a git repository.");
        using var _ = LockRepo(repo.Path);
        return RunGitCheckout(repo.Path, new[] { "checkout", branchName });
    }
    catch (Exception ex)
    {
        return new CheckoutOutcome(false, ex.Message);
    }
}
```

After:

```csharp
public GitOutcome CheckoutLocalBranch(Repo repo, string branchName)
    => RunSimple(repo, "git checkout", "checkout", branchName);
```

Methods with real logic (e.g. `Merge` at `GitService.cs:1976-2016` with its MERGE_HEAD sentinel probe) keep their body but lose the ceremony — they pass a closure to `RunOperation` and return `MergeLikeOutcome.Conflicted` where today they return `(true, null, HasConflicts: true)`.

### Caller side

With exceptions already folded into `Failed` inside the service, the `RunBackground` tuple error channel is redundant for these calls. One small normalizer collapses the three-way reconciliation:

```csharp
// outcome is null only when RunBackground itself caught (thread-level failure)
static GitOutcome Normalize(GitOutcome? outcome, string? error)
    => outcome ?? new GitOutcome.Failed(error ?? "Operation failed.");
```

Then `BranchesViewModel.cs:456-475` style call sites become a single switch:

```csharp
onResult: (outcome, error) =>
{
    switch (Normalize(outcome, error))
    {
        case GitOutcome.Failed f:
            _bus.Broadcast(new ShowOperationErrorMessage("Stash apply failed", f.Message));
            return;
        case GitOutcome.Success:
            ...
    }
}
```

### Migration plan (incremental, one outcome type at a time)

Each old record type is independent, so this never needs a big-bang change:

1. Add the `GitOutcome` hierarchy + `RunOperation`/`RunSimple` to `GitService`, and `Normalize` to `ViewModelBase`.
2. Migrate the 20 Shape-A methods mechanically (change interface signature, collapse body, update the ~2-3 call sites each). Compile errors are the worklist.
3. Migrate the 6 Shape-B methods to `MergeLikeOutcome`; this is where callers like `UpdateSubmodulesDialogViewModel.cs:41` (`outcome.Success || outcome.HasConflicts`) and `BranchesViewModel.cs:470` become explicit cases.
4. Migrate the 4 bespoke types (`Pull`, `Abort`, `Continue`, `Clone`), fixing the tuple re-encoding in `RepoOperationsStore.cs:115` along the way.
5. Delete the 30 old records.

### Expected impact

- ~30 try/catch skeletons (10-15 lines each) collapse to 1-3 lines → roughly 300 lines out of `GitService.cs`.
- 30 record declarations → 6.
- 40+ `error != null || !outcome.Success` reconciliations become single switches with one fallback site.
- Every flag-combination bug class (error-on-success, conflict-on-failure, stash-create-with-conflicts) becomes a compile error.

## Honorable mention

`GitService.cs` at 3,534 lines with 32 ad-hoc `.Split()` parsing sites and 200+ line methods (`Load` at lines 87-305) deserves decomposition too — but item 1 shrinks it substantially as a side effect, so do that first and reassess.
