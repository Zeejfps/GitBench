# Cross-Repo Change Sets — coordinated branches + one review across repos

> GitBench organizes multiple repositories, but every git operation and every review surface is
> still scoped to **one** repo. The workflow this plan solves: a change that must land in several
> repos at once (an API change in `service-a` + its consumers in `service-b` and `service-c`),
> where the author needs to (a) keep the branches **in line** across repos and (b) **review the
> whole change as one thing** in the Review window. Phases are ordered **outside-in** (the
> `stacked-diffs.md` discipline) so every phase produces something runnable and locally testable
> before the next one starts.

## State of the world

> Kept current for the next phase's agent. Update it when you finish a phase.

**Phases done:** Phase 1 (detection + entry-point affordance) and Phase 2 (batch actions on a
synced branch). Phases 3-7: not started.

**Build/test status:** `dotnet build GitBench.sln` clean; `dotnet test` green at **298** tests
(286 baseline + 7 in `SyncedBranchIndexTests.cs` + 5 new in `GitBench.Tests/ChangeSetOperationsTests.cs`).

**What Phase 1 added (all under `GitBench/`):**

- **`Features/ChangeSets/SyncedBranch.cs`** — public records `SyncedBranch(string BranchName,
  IReadOnlyList<Guid> RepoIds)` and `RepoBranchSnapshot(string? DefaultBranch,
  IReadOnlyCollection<string> LocalBranchNames)`, plus the **pure** `internal static
  SyncedBranchCorrelator.Correlate(orderedRepoIds, byRepo)` — same-name correlation with per-repo
  default exclusion. This is the unit the tests drive; the index just wraps it.
- **`Features/ChangeSets/SyncedBranchIndex.cs`** — `internal sealed class SyncedBranchIndex`, the
  app singleton. Registered **eager** in `App/AppServices.cs` via a factory that calls
  `Start(dispatcher)` (mirrors `RepoStatusStore`). Loops `IGitService.GetBranches` +
  `GetDefaultBranchName` over each group's primaries on the deferred startup sweep and refreshes a
  repo on `RefsChangedMessage`. Reads: `SyncedReposFor(repoId, branchName)` (menu-facing) and
  `SetsForGroup(groupId)`; `IReadable<int> Revision` bumps on any snapshot change so bound views
  re-derive. Snapshots + epoch are UI-thread-only; git work runs under `IStartupSweepCoordinator`.
- **`IGitService.GetDefaultBranchName(Repo)`** (new interface method + `GitService` impl near
  `GetDefaultBranchRef`) — returns the repo's default as a **bare local branch name**
  (`origin/HEAD`'s target with the remote prefix stripped, else local `main`/`master`). No other
  `IGitService` implementations or test fakes exist, so this was a safe addition.
- **Entry point (1.2):** `BranchesViewModel` now takes a `SyncedBranchIndex` ctor param and
  `AddChangeSetMenuItems` appends, for a synced local branch, a disabled **"Also on: …"** caption +
  **"Review across N repos…"** action. The action is a **placeholder toast**
  (`ShowToastMessage(ToastIntent.Info(...))`) this phase — see Deviations.
- **Sidebar glyph (1.3):** `LocalBranchRow` gained `string? SyncedWith`; `BranchTreeBuilder.BuildRows`
  takes an optional `IReadOnlyDictionary<string,string> syncedByBranch` (branch → other members'
  display names) threaded from `BranchesViewModel.BuildSyncedInfo()` (reads `_index.Revision` so rows
  re-derive on detection change). `BranchListRow.TrailingFor` renders a `FolderGit2` glyph with a
  hover tooltip for synced branches.
- **Localization:** keys `changesets.also_on`, `changesets.review_across`,
  `changesets.review_placeholder`, `changesets.synced_tooltip` added to **all 6** `Localization/Strings/*.json`.
- **Fixture:** `scripts/make-test-repos.sh 70-change-set` (`gen_change_set`) already present and
  validated — service-a/b/c with `feature/cross-repo` (all 3), `bugfix/shared-logging` (b+c),
  `feature/only-in-a` decoy, defaults main/main/master.

**What Phase 2 added (all under `GitBench/`):**

- **`Features/ChangeSets/ChangeSetOperations.cs`** — `internal sealed class ChangeSetOperations`, the
  batch coordinator. Registered as a plain (non-eager) singleton in `App/AppServices.cs`
  (`context.AddSingleton<ChangeSetOperations>()`, auto-wired). Public fire-and-forget methods
  `CheckoutInAll(repoIds, branch)`, `PushInAll(repoIds)`, `PullInAll(repoIds)`, `FetchInAll(repoIds)`,
  `DeleteInAll(repoIds, branch, force)` each loop one `IGitService` call per member off-thread
  (`Task.Run` → `IUiDispatcher.Post`, the `RunBackground` convention — the coordinator is **not** a
  `ViewModelBase`), broadcast `RefsChangedMessage` (+ `WorkingTreeChangedMessage` for checkout/pull)
  per member, and report one toast: `ToastIntent.Success` when all succeed, else a
  `ToastIntent.Warning` whose "Details" `ToastAction` broadcasts `ShowOperationErrorMessage` with a
  per-repo `name: message` failure breakdown. The **pure static** `RunOverMembers(members, op)` is the
  unit-tested core — folds a thrown call into that member's `GitOutcome.Failed`, no rollback, a failed
  member never blocks the rest (Locked decision #5).
- **`ChangeSetOpResult`** (public record in the same file) — `IReadOnlyList<(Guid RepoId, GitOutcome
  Outcome)> Results` plus `SuccessCount` / `AllSucceeded` helpers. The honest per-repo outcome list.
- **`Features/ChangeSets/DeleteChangeSetBranchDialog.cs`** — `internal sealed record` Widget confirm
  dialog for "Delete in all…" (the `MergeBranchDialog`/`Dialog` pattern, `ShowDialogMessage`); a force
  checkbox, inline `Action` OnClick → `ChangeSetOperations.DeleteInAll`. No dialog VM (the op is
  fire-and-forget; the dialog only confirms).
- **Batch menu (2.2):** `BranchesViewModel.AddChangeSetMenuItems` appends, under the Phase-1 section,
  **Checkout in all / Push all / Pull all / Fetch all / Delete in all…** wired to `ChangeSetOperations`
  (`BranchesViewModel` gained a `ChangeSetOperations` ctor param — auto-wired by the Context).
- **Checkout guardrail (2.3):** `BranchesViewModel.OfferSwitchOtherMembers` — after a successful
  single-repo `StartCheckoutLocal` of a synced branch, shows a one-shot `Info` toast whose action
  batch-checks-out the other members (`CheckoutInAll(others, branch)`). Bypasses `StartCheckoutLocal`,
  so no recursion.
- **Localization:** 20 new `changesets.*` keys (menu labels, per-op success toasts, `toast_partial`,
  `op_failed_title`, `details`, `pull_diverged`, delete-dialog `delete_title`/`delete_body`/
  `delete_force_label`, guardrail `switch_prompt`/`switch_others`) added to **all 6**
  `Localization/Strings/*.json`.
- **Tests:** `GitBench.Tests/ChangeSetOperationsTests.cs` — 5 tests over a `FakeGitService : IGitService`
  driving `RunOverMembers`: per-repo outcomes in member order, one failure doesn't block others, a
  thrown call folds into `Failed` and the loop continues, all-fail is partial (not an exception),
  empty members is vacuous success.

**Deviations (Phase 2):**

- **Push/Pull/Fetch-all map to each member's existing `IGitService.Push/Pull/Fetch`** (current
  checkout), per the plan's direct mapping — most useful after "Checkout in all" aligns the members.
- **Pull returns `PullOutcome`**; the coordinator maps `Diverged`/`Failed` → a per-repo
  `GitOutcome.Failed` (batch has no interactive strategy prompt — `Diverged` becomes an honest
  per-repo failure using the `changesets.pull_diverged` message).
- **Delete-in-all does not pre-filter** checked-out/unmerged members — non-force deletes of those fail
  per-repo and are reported (no rollback), matching Locked decision #5. A force checkbox is offered.
- **The failure summary reuses the existing scrollable `OperationErrorDialog`** (via
  `ShowOperationErrorMessage`) behind the toast's "Details" action rather than introducing a new
  summary dialog.

**Gotchas / notes for the next agent:**

- The `Strings` type is **source-generated** from the JSON by `framework/ZGF.Gui.Generator`
  (`LocalizationGenerator`). A missing key in any locale is a **build error** (LOC004), so add new
  keys to all 6 files. Key→identifier: `changesets.review_across` → `ChangesetsReviewAcross`;
  `{count}`/`{repos}` become method params (`object`).
- `OpenChangeSetReviewMessage` and `ChangeSetSession` (listed under "New types") are **not yet
  defined** — Phase 3 introduces them and swaps the placeholder toast for the real broadcast
  (`BranchesViewModel.AddChangeSetMenuItems`).
- Correlation is scoped to a repo's group via `IRepoRegistryExtensions.FindGroupContaining`; only
  primaries appear in a `Group.RepoIds`, so worktrees/submodules are excluded for free.
- `SyncedBranchIndex.Revision` is the reactive hook; `SyncedReposFor`/`SetsForGroup` themselves are
  plain reads (fine for the on-open menu, and paired with a `Revision` read for the row projection).
- **`ChangeSetOperations` is the reusable batch seam for Phases 4 and 5.** Phase 4's "Start change
  set…" is another `CreateBranch(repo, name, startPoint, checkout: true)` loop; Phase 5's batch
  commit is a `Commit(repo, message + trailer, amend: false)` loop — both fit the existing
  `RunOverMembers` + summary-toast shape (add a `CreateInAll`/`CommitInAll` method following the
  five present ops). The reporting (success toast / warning-toast-with-Details) is already generic
  over `Func<Strings,int,string>`; only the success-message key differs.
- **`ChangeSetOperations.RunOverMembers` is `static`** and takes `Func<Repo, GitOutcome>`, so any op
  whose per-repo call returns (or can be mapped to) a `GitOutcome` is testable without touching the
  index or the UI. Non-`GitOutcome` results (like `Pull`'s `PullOutcome`) are mapped inside the
  op lambda before entering the loop — see `PullInAll`.

## What a "change set" means here (scope)

- **In scope — correlation by branch name.** A change set is *the same branch name existing in
  more than one repo of a group*. `feature/new-auth` in 3 repos **is** the change set — no
  manifest, no new ontology, and it matches how people already do this by hand (and how Gerrit
  topics / `repo start <topic>` model it). GitBench detects it, surfaces it, batches operations
  over it, and reviews it as one surface.
- **In scope — a cross-repo review surface, in BOTH review homes.** (1) The Review *window*
  over N `(repo, base..head)` ranges: one file tree grouped by repo, one combined progress
  meter, one keyboard flow (`j`/`k`/`n` walk straight from the last file of repo A into the
  first file of repo B). (2) The review *panel under Changes* over N working trees: when the
  set branch is checked out across members, review, stage (checkbox = stage in that file's
  repo), and commit the whole set from one surface. This is the thing no web UI can do and the
  headline of the plan.
- **In scope — batch authoring.** Create the branch in N repos in one action; checkout / push /
  pull / delete across the set; commit across the set with a shared message.

- **Out of scope (note, don't build):**
  - **Meta-repo / manifest tooling** (Google `repo`, gclient, meta): a heavyweight ecosystem
    commitment. Convention-based detection gets ~90% of the value with zero setup.
  - **Fake atomicity.** Cross-repo atomic commits don't exist in git. A batch commit is N real
    commits with a shared message, reported per-repo honestly (partial success is a visible
    outcome, not an error to roll back).
  - **Server/PR integration** (opening N linked PRs, GitLab MR dependencies, Gerrit
    submit-whole-topic). GitBench is a local client; the shared commit-message trailer
    (Phase 5) is the hook a later integration would key off.
  - **Cross-repo dependency ordering** (Zuul `Depends-On:` semantics — "repo B's change needs
    repo A's merged first"). Real, but a separate feature; the trailer leaves room for it.

## UX north star — prior art

- **Gerrit topics**: changes across repos grouped by a shared topic string; the review UI lists
  the whole topic and "Submit Whole Topic" lands them together. The *grouping-by-name* idea is
  exactly our branch-name convention.
  ([Gerrit cross-repository changes](https://gerrit-review.googlesource.com/Documentation/cross-repository-changes.html))
- **AOSP `repo start <topic>`**: one command creates the same-named topic branch across many
  projects — our "Start change set…" dialog is the GUI version.
- **OpenStack Zuul `Depends-On:` trailer**: commit-message metadata as the durable cross-repo
  correlation record — our `Change-Set:` trailer borrows the mechanism (out-of-scope semantics
  aside).
- **Batch CLI tools** (`mu-repo`, `gita`, `meta`): loop-a-command-over-repos with per-repo
  outcome reporting — our coordinator + toast summary is that, with a UI.

What none of them have — and a local desktop client can build — is **one review surface over the
union of the diffs**, with a single progress meter and keyboard loop. That's Phase 3-4.

## Grounding — what exists today

### The repo model is ready for this

- **`Repo` is an id + path** (`GitBench/Git/Repo.cs`): `record(Guid Id, string Path, string
  DisplayName, Guid? ParentRepoId)` + `RepoKind { Primary, Worktree, Submodule }`.
- **`IRepoRegistry`** (`Features/Repos/IRepoRegistry.cs:16`) is the app-singleton source of truth:
  `ObservableList<Repo> Repos`, `ObservableList<Group> Groups`, `State<Repo?> Active`. **Groups
  are the natural change-set boundary** — a `Group` (`Features/Repos/Group.cs`) is a live entity
  with `ObservableList<Guid> RepoIds`, and only primaries appear in groups.
- **Persistence**: `RepoStateStore` (`Features/Repos/RepoStateStore.cs:11`,
  `CurrentSchemaVersion = 6`) — JSON, schema-versioned with migrations, written via
  `RepoRegistry.Save()` → `BackgroundFileWriter`. Adding a persisted entity later is a known,
  paved path (Phase 6).

### Every git operation already takes a `Repo` — batching is a loop

`IGitService` (`Git/IGitService.cs`) is **one stateless singleton** (`App/AppServices.cs`); every
method takes the `Repo` to operate on. The batch primitives all exist:

- `CreateBranch(repo, name, startPoint, checkout)` (`IGitService.cs:60`)
- `CheckoutLocalBranch(repo, branchName)` (`:57`), `DeleteBranch(repo, name, force)` (`:66`)
- `Push(repo, force)` (`:44`), `Pull(repo, strategy)` (`:51`), `Fetch(repo)` (`:52`)
- `Commit(repo, message, amend)` (`:38`), `Stage`/`Unstage(repo, paths)` (`:33-34`)
- `GetBranches(repo)` → `Fetched<BranchListing>` (`:32`) — the per-repo branch list the
  detection index needs
- `LoadRangeFiles(repo, baseSha, headSha)` (`:20`), `LoadReviewStack` (`:17`),
  `MergeBase` (`:23`), `ResolveAutoReviewBase(repo, headRef)` (`:27`) — the whole per-repo
  review backend, reused verbatim per set member

**There is no user-facing multi-repo operation today.** The only multi-repo iteration is internal
reconciliation — `WorktreeSyncService`, `SubmoduleSyncService`, and `RepoStatusStore` all loop
`_registry.Repos` on background schedules. Those are the coordinator precedent to copy; this plan
adds the first user-initiated batch.

### Per-repo status for *all* repos already flows

`RepoStatusStore` (`Features/Repos/RepoStatusStore.cs:51`) maintains a `RepoStatus` per repo —
`(string? CurrentBranchName, …, int Ahead, int Behind, bool IsDirty, …)` (`:11-17`) — for the
sidebar badges. The drift panel (Phase 5) is largely a re-presentation of data this store already
has; what it *doesn't* have is each repo's full branch list (it knows only the checked-out
branch), which is why Phase 1 adds a detection index on top of `GetBranches`.

### The Review subsystem was built around the right seams

Everything below is from `docs/plans/stacked-diffs.md` (Phases 1-6.5, DONE) — this plan is its
sequel and reuses its machinery wholesale.

- **`ReviewSession(Guid RepoId, string HeadRef, string HeadLabel, string? BaseRef, string?
  BaseLabel)`** (`Features/Review/ReviewStack.cs:9`) — a pinned single-repo review scope.
  **A change set is a named list of these.** `BaseRef == null` ⇒ auto-resolution
  (upstream → default branch), *per repo* — exactly right for a set whose members target
  different default branches.
- **`IReviewStackSource.LoadAsync(session, cap)`** (`Features/Review/IReviewStackSource.cs`) with
  `GitReviewStackSource` bound in `AppServices` — the per-repo range resolver. A cross-repo
  surface calls it once per member; **no new git plumbing**.
- **`IReviewSurfaceModel`** (`Features/Review/IReviewSurfaceModel.cs:26`) — *the* key seam: the
  model behind a stacked review surface (file tree + `ReviewDiffPanel` + `ReviewKeyController`),
  explicitly "independent of where its files come from". Two implementations already prove the
  decoupling: `ReviewWindowViewModel` (a `base..head` range, `MarkKind = Viewed`) and
  `WorkingTreeReviewViewModel` (the working tree, `MarkKind = Staged`). The cross-repo surfaces
  are implementations #3 (ranges, Phase 3) and #4 (working trees, Phase 5) — the tree, diff
  list, key controller, progress meter, cheatsheet, and mark-tracking all come along for free.
- **The working-tree review panel has the same shape and the same single-repo pin.**
  `WorkingTreeReviewViewModel` (`Features/LocalChanges/WorkingTreeReviewViewModel.cs:20`) wraps
  the *active repo's* `LocalChangesViewModel` + a `StagedFileTracker`; its checkbox routes to
  stage/unstage (`MarkKind = Staged`, `:68`), partial staging renders as an indeterminate mark
  (`IsFilePartiallyMarked`, `:106`), and it drives the same details surface via
  `_details.ShowWorkingTree(repo.Id, files)` (`:176`) — one repo id, exactly like `ShowRange`.
  So the Phase-3 widening (`ShowRange` → sections) has a working-tree twin, and the cross-repo
  working-tree surface aggregates N of these sources the same way Phase 3 aggregates N stacks.
- **Window plumbing template**: `OpenReviewWindowMessage` → `ReviewWindowsViewModel` (owns
  `ObservableList<ReviewWindowViewModel>`, dedupes per `(RepoId, HeadRef)` and focuses the
  existing window, `ReviewWindowsViewModel.cs:59-66`) → `ReviewWindowsView` (zero-sized presenter
  in `AppView`) → `ISecondaryWindowFactory` → `ReviewWindowRootView` (header bar + tree/diff
  split, `ReviewWindowRootView.cs:24`).
- **`ReviewProgressStore`** (`Features/Review/ReviewProgressStore.cs:33`) keys Viewed marks and
  preferred bases by `(Guid RepoId, string HeadRef)` — **already repo-scoped, so per-repo state
  composes across a set with zero changes**. Marks made in a single-repo review of `service-a`
  show up pre-ticked in a cross-repo review containing `service-a`, and vice versa. This falls
  out of the keying; treat it as a feature.
- **Reload on refs change**: `ReviewWindowViewModel` re-loads on `RefsChangedMessage` for its
  repo (`ReviewWindowViewModel.cs:224`). The cross-repo VM subscribes for its *set* of repo ids.
- **Entry-point pattern**: branch context-menu items in `BranchesViewModel`
  (`BuildLocalBranchMenuItems` `:760`, `BuildRemoteBranchMenuItems` `:943`) broadcasting a
  message (`StartReview` `:748`); modal input via `ShowDialogMessage` → `DialogPresenter`
  (the `MergeBranchDialog` pattern); per-repo outcome feedback via `IToastService`
  (`Features/Notifications/`).

### The one real refactor: file identity is a bare path

- `FileChange` is `(string Path, string? OldPath, FileChangeStatus)`
  (`Features/Commits/CommitDetails.cs:20`) and the entire review surface — the tree, selection
  (`IReviewSurfaceModel.SelectedPaths`), Viewed marks, tab keys — identifies a file by its
  **repo-relative path string**. Two repos can both contain `src/index.ts`.
- `CommitDetailsViewModel` holds a **single** `_currentRepoId` (`CommitDetailsViewModel.cs:39`);
  `ShowRange(repoId, baseSha, headSha)` (`:250`) pins the whole surface to one repo. However,
  **each open tab already carries its own repo id** — `CommitFileTab(path, sha, repoId, …)`
  (`:108`) — because the Phase-6.5 `pinnedRepoId` work threaded repo identity per tab, and
  `DiffViewModel` resolves its repo from the tab's pin. So per-file repo identity below the
  details VM is a *widening* (per-file instead of per-window), not a rearchitecture.

## Architecture (target)

```
                         ┌─ Phase 1: detection ────────────────────────────────┐
GetBranches(repo) ─────► SyncedBranchIndex (background, per-group)             │
  per primary            │  branch name → [repo ids that have it]              │
                         │  feeds: context-menu "Also on…", sidebar badge      │
                         └──────────────┬──────────────────────────────────────┘
                                        │ "Review across N repos…"
                                        ▼
Branch menu / set panel ── _bus.Broadcast(OpenChangeSetReviewMessage) ─────────┐
                                                                               ▼
ChangeSetReviewWindowsViewModel                    ← mirrors ReviewWindowsViewModel
   • ObservableList<ChangeSetReviewViewModel>; dedupe per (set of RepoIds, HeadRef)
        │
ChangeSetReviewWindowsView  → ISecondaryWindowFactory (the DiffWindows template)
        │
        ▼   (per-window child Context)
ChangeSetReviewRootView:  ReviewHeaderBar' (set name · N repos · combined progress)
   └── body split: CommitChangesPanel (tree, grouped by repo)  |  ReviewDiffPanel
                         both driven by the window's CommitDetailsViewModel
        │                  (extended: ShowRange/ShowWorkingTree → N-section twins)
ChangeSetReviewViewModel : IReviewSurfaceModel     ← implementation #3 of the seam
   • pinned ChangeSetSession = name + IReadOnlyList<ReviewSession>
   • loads N stacks via IReviewStackSource (one per member, per-repo base resolution)
   • aggregates: repo-qualified paths, combined Hud, one cursor/key flow
   • per-member ReviewProgressStore keys (RepoId, HeadRef) — unchanged

ChangeSetWorkingTreeReviewViewModel : IReviewSurfaceModel   ← implementation #4 (Phase 5)
   • the Changes panel's set mode ("This repo / All repos on <branch>") — no new window
   • same aggregation over N working trees (GetLocalChanges per member,
     refresh on each member's WorkingTreeChangedMessage)
   • checkbox = Stage/Unstage on the owning repo (MarkKind = Staged, partial marks kept)
   • commit box = batch commit, one message + "Change-Set:" trailer, per-repo outcomes

                         ┌─ Phases 2/5: batch ops ─────────────────────────────┐
ChangeSetOperations      │ loop IGitService over members (Checkout/Push/Pull/  │
  (coordinator service)  │ Delete/CreateBranch/Commit), collect per-repo       │
                         │ GitOutcomes, report one summary toast/dialog        │
                         └─────────────────────────────────────────────────────┘
```

New types:

```csharp
// The pinned scope of a cross-repo review: a display name (the shared branch name in the
// convention case) + one ReviewSession per member. Mirrors ReviewSession's pinned-payload role.
public sealed record ChangeSetSession(
    string Name,
    IReadOnlyList<ReviewSession> Members);

public readonly record struct OpenChangeSetReviewMessage(ChangeSetSession Session);

// Phase 1's index: which repos (within a group) carry a branch of the same name.
public sealed record SyncedBranch(
    string BranchName,
    IReadOnlyList<Guid> RepoIds);       // ≥ 2 primaries, same group

// A batch operation's honest outcome: per-repo results, never rolled up into one bool.
public sealed record ChangeSetOpResult(
    IReadOnlyList<(Guid RepoId, GitOutcome Outcome)> Results);
```

## Locked decisions

1. **Correlation key = branch name, scoped to a sidebar group.** Same-named local branches in ≥2
   primaries of one group form a change set implicitly. No new persisted entity until Phase 6
   proves the need. (Worktrees/submodules are excluded from detection — sets are between
   *primaries*; a submodule pointer bump is the parent repo's change.)
   **Each repo's default branch and detached HEADs are excluded** — `main` existing everywhere
   is not a change set; a set must be a *feature* branch name. Exclusion is by each repo's own
   default (a repo whose default is `master` excludes `master`), not the literal name `main`.
2. **A change set review = N independent `ReviewSession`s aggregated behind one
   `IReviewSurfaceModel`.** Each member resolves its own base (upstream → default → preferred),
   loads through the existing `GitReviewStackSource`, and keeps its own
   `(RepoId, HeadRef)`-keyed progress. Nothing about single-repo review changes.
3. **The cross-repo surface shows the combined net diff per member** (the `ShowRange` PR-style
   mode), grouped by repo in the tree. Commits from different repos have no meaningful global
   order, so there is **no interleaved cross-repo commit rail** (a per-repo increments rail is a
   possible later layer, not MVP).
4. **File identity on the cross-repo surface is a repo-qualified path** — `"<repoKey>/<path>"`
   with the repo's display name as the tree's top-level folder — introduced *inside* the
   aggregating VM. Git-facing calls always receive the unqualified repo-relative path plus the
   member's `Repo`. The single-repo surfaces keep bare paths; no shared widget learns about
   repos.
5. **Batch operations are loops over `IGitService` with per-repo `GitOutcome`s, no rollback.**
   Partial success is reported per repo (summary toast, expandable detail), matching git reality.
   A failed member never blocks the others (except `Commit`, which validates staged state up
   front — see Phase 5).
6. **Batch commits stamp a `Change-Set: <name>` trailer** into each member's commit message —
   the durable correlation record that outlives branch deletion, and the hook any future
   PR/submit integration keys off.
7. **The session is pinned in the open-message payload** (stacked-diffs locked decision #2,
   inherited): the cross-repo window never tracks the active repo or the live index — reopening
   after membership changes is a new session.
8. **Naming.** User-facing: **"change set"** (menu: "Review across repos…", "Start change
   set…"). Code namespace: `Features/ChangeSets/` for detection + batch ops;
   the review surface lives in `Features/Review/` beside its siblings.

## Implementation plan — outside-in (each phase runs + is testable)

### Phase 1 — Detect synced branches + the entry-point affordance ✅ DONE

Make the convention *visible* with zero write operations and no new window.

**Deviations from the plan text:**

- **Menu action is a placeholder toast, not an `OpenChangeSetReviewMessage` broadcast.** 1.2's text
  mentions broadcasting `OpenChangeSetReviewMessage` "in this phase, a placeholder toast". Taken
  literally (and matching stacked-diffs Phase 1), the action shows a toast
  (`changesets.review_placeholder`) and the message type / `ChangeSetSession` are **deferred to
  Phase 3**, where the real broadcast + window land — so Phase 1 ships no unused types.
- **Added `IGitService.GetDefaultBranchName(Repo)`.** The per-repo default-branch exclusion needs
  each repo's default as a bare local name, and no public accessor existed (`GetDefaultBranchRef`
  was private and returned `origin/main`). Added a small public method beside it.
- **1.3 glyph reuses `LucideIcons.FolderGit2`** (no dedicated "link/synced" glyph exists in the
  embedded Lucide font) and its tooltip rides the branch row's existing hover state rather than
  introducing a new interactable widget.
- **Correlation logic extracted to a pure `SyncedBranchCorrelator`** the index wraps, so the
  grouping/exclusion logic is unit-tested directly against real-git fixtures (per the plan's
  ReviewStackTests precedent) without standing up the stateful service.

- **1.1** `SyncedBranchIndex` (`Features/ChangeSets/`) — an app singleton that maintains, per
  group, `branch name → repo ids`. Data source: `IGitService.GetBranches(repo)` looped over the
  group's primaries on a background schedule + on `RefsChangedMessage` (precedent for the
  loop-and-schedule shape: `WorktreeSyncService`; for message-driven refresh: `RepoStatusStore`).
  Expose `IReadable`-friendly lookups: `SyncedReposFor(repoId, branchName)` and
  `SetsForGroup(groupId)`.
- **1.2** Branch context menu (`BranchesViewModel.BuildLocalBranchMenuItems`, `:760`): when the
  clicked branch is synced, append a section — a disabled caption row **"Also on: repo-b,
  repo-c"** and the item **"Review across N repos…"** (broadcasts `OpenChangeSetReviewMessage`;
  in *this* phase, a placeholder toast, exactly like stacked-diffs Phase 1).
- **1.3** Optional, cheap: a small "synced" glyph on branch rows that are part of a set
  (tooltip lists the other repos).
- **Localization**: new keys in all 6 `Strings/*.json` (`changesets.also_on`,
  `changesets.review_across`, …).
- **Ship / test locally:** generate the fixture — `scripts/make-test-repos.sh 70-change-set`
  builds three sibling repos linked by `feature/cross-repo` (all three) and
  `bugfix/shared-logging` (two), plus the decoys detection must ignore: a branch that exists in
  one repo only, and default branches (`main`/`main`/`master` — exclusion is per-repo default,
  not the literal name). Add the folder as one group → right-click `feature/cross-repo` → the
  "Also on" caption and "Review across 3 repos…" appear; the decoy branch and the defaults show
  neither. Unit tests for the index's grouping/diffing logic (real-git fixture repos,
  precedent: `ReviewStackTests`).

### Phase 2 — Batch actions on a synced branch ✅ DONE

The cheap, immediately useful wins — before the big review surface.

**Deviations from the plan text:** see the "Deviations (Phase 2)" block in `## State of the world`
above — Push/Pull/Fetch-all use each member's current-checkout `IGitService` call (direct mapping),
`Pull`'s `Diverged`/`Failed` fold into a per-repo `GitOutcome.Failed`, delete-in-all reports
checked-out/unmerged members as honest per-repo failures instead of pre-filtering them, and the
failure breakdown reuses the existing `OperationErrorDialog` behind the toast's "Details" action.

- **2.1** `ChangeSetOperations` (`Features/ChangeSets/`) — a coordinator that runs one
  `IGitService` call per member off-thread (the `RunBackground` conventions), collects
  `ChangeSetOpResult`, and reports: one success toast when all succeed, or a summary
  toast/dialog listing per-repo failures.
- **2.2** Context-menu items under the Phase-1 section: **Checkout in all** (`CheckoutLocalBranch`
  per member), **Push all** (`Push`), **Pull all** (`Pull`), **Fetch all** (`Fetch`),
  **Delete in all…** (confirm dialog → `DeleteBranch`).
- **2.3** Guardrails that make "keep them in line" real day-to-day: checking out a synced branch
  in *one* repo (the ordinary single-repo action) shows a one-shot toast offering "Switch the
  other N repos too" — mismatched checkouts are the most common way cross-repo work drifts.
- **Ship / test locally:** two-repo set → "Checkout in all" switches both (sidebar badges
  confirm); kill the remote on one repo → "Push all" reports one success + one failure,
  legibly; delete-in-all confirms then removes both. Coordinator unit tests over a fake
  `IGitService`.

### Phase 3 — The cross-repo review surface (the headline)

One window, one tree grouped by repo, one progress meter, one keyboard loop.

- **3.1** **Widen `CommitDetailsViewModel` from one range to N sections.** Add
  `ShowRanges(IReadOnlyList<(Guid RepoId, string BaseSha, string HeadSha)> sections)`;
  `ShowRange` becomes the 1-section case (zero behavior change for existing callers). Internally:
  per-section `LoadRangeFiles`, results mapped to **repo-qualified paths** (locked decision #4)
  with a `path → (Repo, unqualified path)` resolver; tab creation passes the *member's* repo id
  into `CommitFileTab` (already per-tab since Phase 6.5 of stacked-diffs). The qualification
  lives entirely in this VM — `DiffViewModel`, the tree widgets, and the tab strip stay
  repo-blind.
- **3.2** `ChangeSetReviewViewModel : IReviewSurfaceModel` — pinned to a `ChangeSetSession`;
  loads each member through `IReviewStackSource.LoadAsync` (per-repo base resolution, including
  each member's `PreferredBase`), then drives `ShowRanges`. Aggregates the `ReviewHud` (files
  viewed / total across all members); `IsFileViewed`/`ToggleFileViewed` route through the
  qualified-path resolver to the member's `(RepoId, HeadRef)` progress key. Reloads a member on
  its `RefsChangedMessage`. Partial failure policy: a member whose range fails to resolve
  renders as an inline error group in the tree, not a dead window.
- **3.3** Window plumbing by template: `OpenChangeSetReviewMessage` +
  `ChangeSetReviewWindowsViewModel`/`View` (dedupe key: sorted member repo ids + head ref) +
  `ChangeSetReviewRootView` (header: set name, member count, combined progress meter, per-member
  base chips or a compact "bases" dropdown reusing `ReviewBaseChip`; body: the reused
  tree + `ReviewDiffPanel` split). Swap Phase 1's placeholder toast for the real broadcast.
- **3.4** Keyboard flow needs **no new work**: `ReviewKeyController` binds `IReviewSurfaceModel`,
  and the aggregated file list orders members' files contiguously, so `j`/`k`/`n`/`v` walk
  across repo boundaries for free.
- **Ship / test locally:** two-repo set with real edits → "Review across 2 repos…" → one window,
  tree shows two top-level repo folders, diffs render per file with correct per-repo content,
  `n` crosses the repo boundary, marking Viewed in the cross-repo window pre-ticks the same file
  in that repo's single-repo review (shared progress keys). Tests: qualified-path resolver
  round-trip; aggregation of two stub stacks; per-member failure isolation.

### Phase 4 — Start a change set (authoring)

- **4.1** **"Start change set…"** — entry points: group header context menu + repo context menu.
  A `ShowDialogMessage` dialog (the `MergeBranchDialog` pattern): branch-name field + a repo
  checklist defaulting to the group's primaries + per-repo start point (default: each repo's
  default branch tip). On confirm → `ChangeSetOperations` loops
  `CreateBranch(repo, name, startPoint, checkout: true)`; summary reporting as in Phase 2.
- **4.2** The just-created set is immediately visible via the Phase-1 index (the index refresh
  is triggered by the op completing, not by timers).
- **Ship / test locally:** start `feature/y` across 3 repos → all three switch to it; the
  branch row shows the synced glyph; one repo having a name collision reports that repo's
  failure and still creates the others.

### Phase 5 — Cross-repo working-tree review + batch commit (the Changes panel)

The write side of the headline: the review panel under Changes learns the same aggregation the
window got in Phase 3, so "review → stage → commit" works across the set from one surface.
Deliberately after Phase 3, which lands the shared plumbing (`ShowRanges`, the qualified-path
resolver) this phase reuses.

- **5.1** **Widen the working-tree details path**: `ShowWorkingTree(repoId, files)` →
  `ShowWorkingTrees(sections)` (the Phase-3.1 twin; single-section call unchanged for the
  existing panel). Same qualified-path resolver, same per-tab repo threading.
- **5.2** `ChangeSetWorkingTreeReviewViewModel : IReviewSurfaceModel` — implementation #4
  (`MarkKind = Staged`). Aggregates per-member working-tree sources (each member's changes via
  `GetLocalChanges(repo)`, refreshed on its `WorkingTreeChangedMessage`); the checkbox routes
  through the resolver to `Stage`/`Unstage` on the *owning* repo, partial-staging indeterminate
  marks included. Per-file hunk staging keeps working — the diff widgets are repo-blind and each
  tab already carries its repo.
- **5.3** **Surface it as a set mode of the Changes panel**: when the active repo's checked-out
  branch is a synced set branch, offer a toggle — **"This repo / All repos on <branch>"** (only
  members that have the branch *checked out* participate; the Phase-2 checkout guardrail is how
  you get there). No new window; it's the same panel with more sources.
- **5.4** **Batch commit through the panel's commit box**: one message, committed per member
  that has staged changes — `Commit(repo, message + "\n\nChange-Set: <name>")` (locked decision
  #6) — with an up-front per-repo staged summary in the confirm step, per-repo outcomes, no
  rollback. Members with nothing staged simply don't commit.
- **5.5** Explicitly **not** doing: auto-staging, amend-across-repos, committing members whose
  checkout doesn't match the set branch.
- **Ship / test locally:** on the `70-change-set` fixture, checkout-in-all `feature/cross-repo`,
  edit files in all three members → toggle "All repos" → one tree with three repo groups; tick
  a file → it stages in its own repo (verify per-repo in the sidebar badges); commit with one
  message → each member with staged changes gets the commit with the trailer; the cross-repo
  review window reflects the new increments after `RefsChangedMessage`.

### Phase 6 — Drift panel + (only if needed) persistence

- **6.1** A **set health strip** in the cross-repo review header (and/or the "Also on" tooltip):
  per member — ahead/behind its base, unpushed commits, dirty working tree, *branch missing*
  (was part of the set, deleted in one repo). Sources: `RepoStatusStore` + the Phase-1 index +
  each member's loaded stack; mostly presentation.
- **6.2** **Persisted `ChangeSet` entity — only if convention proves insufficient** (e.g. users
  want pinned bases per set, explicit membership across groups, or set-scoped notes). Schema:
  a sibling of `Group` in `RepoStateStore.State`, `CurrentSchemaVersion` 6 → 7 with a migration.
  Everything in Phases 1-5 is designed to not require it.
- **6.3** Localization sweep + keyboard/cheatsheet entries for the new window.

### Phase 7 — Verification

- Real-git integration tests (the `ReviewStackTests` fixture pattern: throwaway repos via the
  `git` CLI): index detection across add/delete/rename; batch create/checkout/delete; batch
  commit trailer; cross-repo range aggregation with per-member base resolution; partial-failure
  reporting.
- Manual pass on a 3-repo set: full authoring loop (start → edit → review across → commit →
  push all) and the drift cases (delete the branch in one repo, force-move a base, dirty tree).
  The `70-change-set` scenario in `scripts/make-test-repos.sh` generates the standing fixture —
  it already parks the drift states (mismatched checkout, unpushed commit, moved base, dirty
  tree, a member with no remote) and the path-collision case (`src/index.ts` in two members).

## Open decisions (recommend, but worth confirming)

1. **Detection scope: group-only vs whole registry.** Group-only (recommended — groups are the
   user's own statement of "these belong together", and it bounds index cost) vs any two repos
   in the registry. Escape hatch: an "Add repos…" step on the review-window header if a set ever
   spans groups.
2. **Repo-qualified path scheme.** `"<DisplayName>/<path>"` with display names de-duplicated by
   suffixing (recommended — human-readable tree folders for free) vs a structured
   `(Guid, path)` key threaded through the widgets (cleaner, but breaks the "widgets stay
   repo-blind" property and touches every shared surface).
3. **Cross-repo increments rail (per-repo commit stacks in the window).** Skip for MVP
   (recommended; net-diff mode is the shipped default for single-repo review too) vs build a
   two-level rail (repo → increments) in Phase 6.
4. **Where batch actions live long-term.** Context menu only (recommended for MVP) vs a
   dedicated "Change sets" sidebar section listing active sets with inline actions — revisit
   after Phase 2 usage.
5. **Trailer format.** `Change-Set: <branch-name>` (recommended — human-readable, greppable,
   matches the convention key) vs a generated id (survives branch renames, but opaque; only
   needed if Phase 6.2's persisted entity lands).
