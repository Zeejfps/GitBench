# Cross-Repo Change Sets — coordinated branches + one review across repos

> GitBench organizes multiple repositories, but every git operation and every review surface is
> still scoped to **one** repo. The workflow this plan solves: a change that must land in several
> repos at once (an API change in `service-a` + its consumers in `service-b` and `service-c`),
> where the author needs to (a) keep the branches **in line** across repos and (b) **review the
> whole change as one thing** in the Review window. Phases are ordered **outside-in** (the
> `stacked-diffs.md` discipline) so every phase produces something runnable and locally testable
> before the next one starts.

## What a "change set" means here (scope)

- **In scope — correlation by branch name.** A change set is *the same branch name existing in
  more than one repo of a group*. `feature/new-auth` in 3 repos **is** the change set — no
  manifest, no new ontology, and it matches how people already do this by hand (and how Gerrit
  topics / `repo start <topic>` model it). GitBench detects it, surfaces it, batches operations
  over it, and reviews it as one surface.
- **In scope — a cross-repo review surface.** One Review window over N `(repo, base..head)`
  ranges: one file tree grouped by repo, one combined progress meter, one keyboard flow
  (`j`/`k`/`n` walk straight from the last file of repo A into the first file of repo B). This is
  the thing no web UI can do and the headline of the plan.
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
  `WorkingTreeReviewViewModel` (the working tree, `MarkKind = Staged`). The cross-repo surface is
  implementation #3 — the tree, diff list, key controller, progress meter, cheatsheet, and
  Viewed-tracking all come along for free.
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
        │                                    (extended: ShowRanges — N sections)
ChangeSetReviewViewModel : IReviewSurfaceModel     ← implementation #3 of the seam
   • pinned ChangeSetSession = name + IReadOnlyList<ReviewSession>
   • loads N stacks via IReviewStackSource (one per member, per-repo base resolution)
   • aggregates: repo-qualified paths, combined Hud, one cursor/key flow
   • per-member ReviewProgressStore keys (RepoId, HeadRef) — unchanged

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

### Phase 1 — Detect synced branches + the entry-point affordance

Make the convention *visible* with zero write operations and no new window.

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
- **Ship / test locally:** create `feature/x` by hand in two grouped repos → right-click it →
  the "Also on" caption and "Review across 2 repos…" appear; clicking flashes the placeholder
  toast; branches not shared show neither. Unit tests for the index's grouping/diffing logic
  (real-git fixture repos, precedent: `ReviewStackTests`).

### Phase 2 — Batch actions on a synced branch

The cheap, immediately useful wins — before the big review surface.

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

### Phase 5 — Batch commit with a shared message

The most write-sensitive piece, deliberately after the review surface exists to check it with.

- **5.1** A **"Commit to change set…"** action (set section of the branch menu; later maybe a
  LocalChanges affordance). Dialog: one message box + a per-repo summary of *staged* changes
  (from `RepoStatusStore`/`GetLocalChanges`), members with nothing staged shown unchecked.
  Validation up front: only members with staged changes participate.
- **5.2** On confirm: per member, `Commit(repo, message + "\n\nChange-Set: <name>", amend: false)`
  (locked decision #6). Per-repo outcomes as ever; no rollback.
- **5.3** Explicitly **not** doing: auto-staging, cross-repo hunk selection, amend-across-repos.
  Staging stays a per-repo activity in LocalChanges — the batch is only the message + the act.
- **Ship / test locally:** stage edits in 2 of 3 members → dialog pre-checks those two → commit →
  both repos show the commit with the trailer; the cross-repo review window reflects the new
  increments after `RefsChangedMessage`.

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

## Open decisions (recommend, but worth confirming)

1. **Detection scope: group-only vs whole registry.** Group-only (recommended — groups are the
   user's own statement of "these belong together", and it bounds index cost) vs any two repos
   in the registry. Escape hatch: an "Add repos…" step on the review-window header if a set ever
   spans groups.
2. **Default-branch names as sets.** `main` existing everywhere technically matches the
   convention. Recommendation: exclude each repo's default branch (and detached HEADs) from
   detection — a set must be a *feature* branch name.
3. **Repo-qualified path scheme.** `"<DisplayName>/<path>"` with display names de-duplicated by
   suffixing (recommended — human-readable tree folders for free) vs a structured
   `(Guid, path)` key threaded through the widgets (cleaner, but breaks the "widgets stay
   repo-blind" property and touches every shared surface).
4. **Cross-repo increments rail (per-repo commit stacks in the window).** Skip for MVP
   (recommended; net-diff mode is the shipped default for single-repo review too) vs build a
   two-level rail (repo → increments) in Phase 6.
5. **Where batch actions live long-term.** Context menu only (recommended for MVP) vs a
   dedicated "Change sets" sidebar section listing active sets with inline actions — revisit
   after Phase 2 usage.
6. **Trailer format.** `Change-Set: <branch-name>` (recommended — human-readable, greppable,
   matches the convention key) vs a generated id (survives branch renames, but opaque; only
   needed if Phase 6.2's persisted entity lands).
