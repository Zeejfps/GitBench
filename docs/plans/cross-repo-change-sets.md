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

**Phases done:** Phase 1 (detection + entry-point affordance), Phase 2 (batch actions on a
synced branch), Phase 3 (the cross-repo review surface — the headline), Phase 4 (start a change
set — authoring), Phase 5 (cross-repo working-tree review + batch commit — the Changes panel), and
Phase 6 (set health strip + localization/cheatsheet sweep; persistence evaluated and skipped).
Phase 7 (verification): not started.

**Build/test status:** `dotnet build GitBench.sln` clean; `dotnet test` green at **325** tests
(286 baseline + 7 in `SyncedBranchIndexTests.cs` + **11** in `GitBench.Tests/ChangeSetOperationsTests.cs`
+ 7 in `GitBench.Tests/ChangeSetReviewTests.cs` + **5** in
`GitBench.Tests/ChangeSetWorkingTreeReviewTests.cs` + **8** in
`GitBench.Tests/ChangeSetHealthTests.cs`). Phase 6 added the **8** new health tests (pure drift
computation: tracked-clean → in sync, unpushed/behind/dirty → attention, ahead-of-base is not drift,
no-upstream informational, detached-HEAD not mislabeled, failed-load → unavailable, roll-up tally).
**No display available** — the Phase-5 GUI wiring and the Phase-6 header health strip are manual-pass
only; see the consolidated manual-verification list at the end of this section.

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

**What Phase 3 added (all under `GitBench/`):**

- **New types (the plan's "New types" section), in `Features/Review/ChangeSetSession.cs`:** public
  `ChangeSetSession(string Name, IReadOnlyList<ReviewSession> Members)` and the
  `OpenChangeSetReviewMessage(ChangeSetSession Session)` broadcast. Both live in `Features.Review`
  (not `Messages/`) so the `ChangeSetSession`→`ReviewSession` dependency stays inside the feature.
- **`Features/Review/RepoQualifiedPaths.cs`** — the pure repo-qualified path scheme (Locked decision
  #4). `BuildKeys(members)` → a stable, unique, **slash-free** key per member (display name, dup names
  suffixed `" (2)"`); `Qualify(key, path)` prefixes; `TryResolve(qualified, byKey, out member, out
  path)` splits back off the leading segment. This is the "resolver" the plan calls for, as a pure unit.
- **`Features/Review/ChangeSetAggregator.cs`** — `ChangeSetMemberLoad` (`Ok(…, ReviewStack)` /
  `Failed(…, message)`) + the pure `ChangeSetAggregator.LoadAll/LoadMember` that resolves each member
  through the **existing** `IReviewStackSource.LoadAsync` (per-repo base resolution, no new git
  plumbing) and **folds a thrown load into that member's `Failed`** so one member never sinks the rest.
  This is the unit-tested core (two-stub-stack aggregation; per-member failure isolation).
- **`Features/Review/ChangeSetReviewedFiles.cs`** — `IReviewedFileTracker` #3: aggregates N members'
  Viewed state keyed by **qualified path**, resolving each mark back to its member's `(RepoId, HeadRef)`
  key in the shared `IReviewProgressStore` (so marks compose with single-repo reviews for free), with
  per-(qualified)path fingerprints for change-detection.
- **`Features/Review/ChangeSetReviewViewModel.cs`** — **implementation #3 of `IReviewSurfaceModel`**
  (the headline). Pinned to a `ChangeSetSession`; resolves all members off-thread via the aggregator,
  drives its own `CommitDetailsViewModel.ShowRanges`, aggregates the `ReviewHud`, owns the cursor +
  marks tracker, and reloads a single member on its `RefsChangedMessage` (small per-member cache).
  A plain `IReviewSurfaceModel`/`IDisposable` (mirrors `WorkingTreeReviewViewModel`, **not** a
  `ViewModelBase`) — it hand-rolls `Task.Run`→`IUiDispatcher.Post` since the aggregator can't throw.
- **`CommitDetailsViewModel.ShowRanges(...)` (3.1)** — the widening. New public
  `DetailsRangeSection` (`Range`/`Failed`) input; loads each member's `LoadRangeFiles`, maps results to
  **repo-qualified paths** (incl. qualified `OldPath` for renames), keeps a private `qualified → (RepoId,
  bare path, base, head)` resolver (`_rangeFiles`, non-null ⇒ Ranges mode) that `CreateFileDiff`/
  `SelectFile` route through, and synthesizes one combined `CommitDetails` (aggregate range key as
  `Sha`, `RepoId = Guid.Empty`). **`ShowRange` and every existing caller are untouched** — the four
  single-repo entry points now also null `_rangeFiles` so switching surfaces is clean.
- **`CommitFileTab`** gained an optional `displayPath` (tab identity = qualified path, git target = bare
  path) so a range tab can carry a repo-qualified identity without the diff widgets learning about repos.
- **Window plumbing (3.3):** `ChangeSetReviewWindowsViewModel` (auto-wired singleton, mirrors
  `ReviewWindowsViewModel`; **dedupe key = sorted member `(RepoId, HeadRef)` pairs**, focus-existing on
  repeat) → `ChangeSetReviewWindowsView` (zero-sized presenter, mounted in `AppView` right after
  `ReviewWindowsView`) → `ChangeSetReviewRootView` (`ChangeSetReviewHeaderBar` + reused
  `CommitChangesPanel`/`ReviewDiffPanel` split, `ReviewKeyController`, `ReviewCheatsheetOverlay`).
- **Entry point (3.3):** `BranchesViewModel.AddChangeSetMenuItems` — the Phase-1 placeholder toast is
  **replaced** by `OpenChangeSetReview(branchName, members)`, which builds one auto-base `ReviewSession`
  per member and broadcasts `OpenChangeSetReviewMessage`. (`changesets.review_placeholder` key is now
  unused but left in the JSONs — unused keys are not a build error.)
- **Keyboard (3.4):** free — `ReviewKeyController` binds the surface via `IReviewSurfaceModel`, and the
  aggregated file list orders members' files contiguously, so `j`/`k`/`v`/`?` walk across repo
  boundaries with no new work.
- **Localization:** 3 new `changesets.*` keys (`review_window_title`, `review_repos`,
  `review_member_failed`) added to **all 6** `Localization/Strings/*.json`.
- **Tests:** `GitBench.Tests/ChangeSetReviewTests.cs` (7) — qualified-path round-trip (incl. duplicate
  display names and the `src/index.ts`-in-two-repos collision), unknown-key echo, the marks tracker
  routing a qualified mark to the owning member's progress key, two-stub-stack aggregation in session
  order, one-member-throws isolation, all-members-throw.

**What Phase 4 added (all under `GitBench/`):**

- **`ChangeSetOperations.CreateInAll(IReadOnlyList<(Guid RepoId, string StartPoint)> members,
  string branchName)`** — the sixth batch op, following the existing five. Loops
  `CreateBranch(repo, name, startPoint, checkout: true)` per member through the shared `Run` →
  `RunOverMembers` seam with the Phase-2 summary reporting (success toast / warning-toast-with-Details),
  so a name collision in one repo fails that repo alone and still creates the others (Locked decision
  #5). `touchesWorkingTree: true` (checkout switches each member). Success key `changesets.toast_create`.
- **`ChangeSetOperations.ResolveStartPoint(startById, repoId)`** — pure `internal static`; trims the
  member's start point, falling back to `HEAD` when blank (matches `CreateBranchDialog`). This is the
  unit-tested per-repo mapping seam (`CreateInAll` itself is fire-and-forget).
- **`Features/ChangeSets/StartChangeSetDialog.cs`** — `internal sealed record` Widget (the
  `CreateBranchDialog`/`MergeBranchDialog` pattern): a branch-name `LabeledInput` (live `RefNameRules`
  validation) + a two-column "Repositories / Start point" checklist, one row per group primary
  (name checkbox + bare start-point input built via `DialogFrame.TextInput`/`WrapInput`, registered
  with the dialog's `DialogInputRegistry`). Takes `IReadOnlyList<Repo> Repos` (the group's primaries,
  resolved at menu-build time).
- **`Features/ChangeSets/StartChangeSetDialogViewModel.cs`** — `IDialogViewModel`. Holds `Name`, a
  `RepoRow` per primary (`Included` default on, `StartPoint` seeded blank then filled off-thread from
  `IGitService.GetDefaultBranchName`), `NameStatus`, and the `Create` `AsyncCommand`. `Create`'s work
  is a no-op; the gate is "valid non-empty name **and** ≥1 member checked" (a `Derived` reading each
  row's `Included`, so it re-gates as checkboxes toggle); `onSuccess` gathers the selection, calls
  `ChangeSetOperations.CreateInAll`, and closes.
- **Entry points (4.1):** **"Start change set…"** on the **group-header** context menu
  (`Features/Repos/GroupHeaderRow.BuildMenuItems`) and the **primary repo** context menu
  (`Features/Repos/RepoNodeViewModel.AddStartChangeSetItem`, called from `BuildPrimaryMenu`). Both
  gate on the group holding ≥2 primaries and broadcast `ShowDialogMessage(new StartChangeSetDialog{…})`
  with `LucideIcons.FolderGit2` (the change-set glyph).
- **`IRepoRegistryExtensions.PrimariesOfGroup(group)`** — new helper returning a group's primary
  `Repo`s in membership order (both menu sites use it).
- **Immediate visibility (4.2):** falls out for free — `CreateInAll`'s `Report` broadcasts
  `RefsChangedMessage` per member, and `SyncedBranchIndex` already refreshes a repo on that message, so
  the just-created set is detected (synced glyph, "Also on…", batch menu) with no timer.
- **Localization:** 6 new `changesets.*` keys (`start_menu`, `start_title`, `start_name_label`,
  `start_repos_label`, `start_start_point_label`, `toast_create`) added to **all 6**
  `Localization/Strings/*.json`.
- **Tests:** `GitBench.Tests/ChangeSetOperationsTests.cs` +3 (now 8) — per-repo start-point mapping
  creates every member with the mapped start point + `checkout: true`; a name collision in one repo is
  reported per-repo while the others are still created (no rollback); `ResolveStartPoint` trims explicit
  values and falls back to `HEAD` for blank/missing. The `FakeGitService` now implements `CreateBranch`
  (records `(RepoId, Name, StartPoint, Checkout)` in `CreateCalls`).

**What Phase 5 added (all under `GitBench/`):**

- **`CommitDetailsViewModel.ShowWorkingTrees(IReadOnlyList<DetailsWorkingTreeSection>)` (5.1)** — the
  working-tree twin of `ShowRanges`. New public `DetailsWorkingTreeSection` (`Files`/`Failed`). Adds a
  **fourth** mode field `_workingTrees` (a `qualified → (RepoId, bare path)` resolver, the working-tree
  analogue of `_rangeFiles`); `SelectFile`/`CreateFileDiff` branch on it (opening `CommitFileTab.ForWorkingTree`
  with the qualified `displayPath`), and every `Show*`/`Clear` now nulls all four mode flags. Synchronous
  (files pushed in by the host) and it keeps open tabs across refreshes, like `ShowWorkingTree`.
- **`CommitFileTab.ForWorkingTree(..., string? displayPath = null)`** — the tab's identity is `displayPath`
  (the repo-qualified path), the git target the bare path; the diff widgets stay repo-blind.
- **`Features/Review/ChangeSetWorkingTreeAggregator.cs`** — the **pure** aggregation core (unit-tested).
  `MemberWorkingTree` (repo + key + unstaged/staged) → `Aggregate(...)` returns the merged qualified file
  list (grouped by repo, member order), the resolver, and the per-qualified-path `FullyStaged`/`PartlyStaged`
  sets (a path in Staged-only is fully staged; a path in both is the indeterminate mark — `StagedFileTracker`'s
  rule, per member). `PlanStage(paths, stage, fully, partly, resolver)` groups a stage/unstage request into
  per-member bare-path batches, skipping paths already in the requested state. `MergeStagedWins(unstaged,
  staged)` is the shared "staged wins, sort by path" merge used by both `Aggregate` and the VM's section build.
- **`Features/Review/ChangeSetStagedFileTracker.cs`** — `IReviewedFileTracker` **#4** (`MarkKind = Staged`),
  the N-repo twin of `StagedFileTracker`. Holds the aggregated `FullyStaged`/`PartlyStaged`/resolver (fed by
  `SetState`); `IsViewed`/`IsPartiallyStaged`/`HasStagedContent` read them; `SetViewed` routes through
  `PlanStage` to per-repo `Action<Guid, IReadOnlyList<string>>` stage/unstage delegates the host supplies.
- **`Features/Review/ChangeSetWorkingTreeReviewViewModel.cs`** — **implementation #4 of `IReviewSurfaceModel`**
  (5.2). A **live singleton** (like `WorkingTreeReviewViewModel`, not pinned): tracks the active repo + set
  membership (the synced-branch members that have the branch *checked out* — 5.3), aggregates their working
  trees via `GetLocalChanges` per member off-thread, drives its own `CommitDetailsViewModel.ShowWorkingTrees`,
  owns the cursor + the staged tracker. Membership recomputes on active-repo/`RefsChangedMessage`/`index.Revision`;
  each member reloads on its `WorkingTreeChangedMessage`/`CommitCreatedMessage`. Exposes `IsAvailable`
  (≥2 members on the set branch, gates the toggle) + `BranchName`, and a title-only commit box
  (`CommitTitle`/`SetCommitTitle`/`Commit`) that opens the confirm dialog → `ChangeSetOperations.CommitInAll`.
- **`Features/LocalChanges/ChangeSetPanelScope.cs`** — `enum ChangeSetPanelScope { ThisRepo, AllRepos }`,
  a **session-scoped** (not persisted) `State<ChangeSetPanelScope>` registered in `AppServices`.
- **`Features/LocalChanges/ChangeSetWorkingTreeReviewView.cs`** — the cross-repo Review layout: the reused
  `CommitChangesPanel`/`ReviewDiffPanel` split + `ReviewKeyController` + cheatsheet, plus its **own compact
  commit bar** (title input + Commit button), provides its own `IReviewSurfaceModel`/`IReviewedFileTracker`/
  `CommitDetailsViewModel` into the subtree.
- **Panel wiring (5.3):** `WorkingChanges.cs` (`LocalChangesView`) — the body is now a 3-way `Switch<int>`
  (List / single-repo Review / cross-repo Review) keyed on `layout` + `scope` + `crossVm.IsAvailable`; the
  shared footer's `kind` returns `NoFooter` in cross mode (the cross view owns its commit bar).
  `WorkingChangesTabStrip` gained a **scope toggle** (`UnderlineTab` was made generic `UnderlineTab<T>`),
  shown only in the Review layout while `crossVm.IsAvailable` — "This repo / All repos on `<branch>`".
- **`ChangeSetOperations.CommitInAll(repoIds, message, changeSetName)` (5.4)** — the seventh batch op.
  Stamps the `Change-Set: <name>` trailer (`StampTrailer`, pure), commits only members with staged changes
  (`CommitOverMembers`, pure — skipped members contribute no outcome), no rollback (Locked #5/#6). Reuses the
  shared `Report` (now with a `commitCreated` flag that also broadcasts `CommitCreatedMessage` per committed
  member). Success key `changesets.toast_commit`.
- **`Features/ChangeSets/CommitChangeSetDialog.cs`** — the confirm step: the up-front per-repo staged summary
  (`changesets.commit_repo_staged` per member) + a Primary "Commit all" action → `CommitInAll` (the
  `DeleteChangeSetBranchDialog` VM-less pattern).
- **Localization:** 9 new `changesets.*` keys (`toast_commit`, `scope_this_repo`, `scope_all_repos`,
  `commit_title`, `commit_body`, `commit_repo_staged`, `commit_confirm`, `commit_button`, `commit_none`)
  added to **all 6** `Localization/Strings/*.json`.
- **Tests:** `GitBench.Tests/ChangeSetOperationsTests.cs` +3 (now 11) and
  `GitBench.Tests/ChangeSetWorkingTreeReviewTests.cs` (5). The `FakeGitService` now implements `Commit`
  (records `(RepoId, Message, Amend)`) and `GetLocalChanges` (staged count per repo).

**What Phase 6 added (all under `GitBench/`):**

- **`Features/Review/ChangeSetHealth.cs`** — the **pure** drift core (unit-tested). Public
  `readonly record struct ChangeSetMemberHealth(RepoKey, Unavailable, AheadOfBase, Unpushed, Behind,
  NoUpstream, Dirty)` with `NeedsAttention` (Unavailable || Unpushed>0 || Behind>0 || Dirty) and `IsQuiet`;
  its static factory `From(repoKey, loadFailed, aheadOfBase, RepoStatus)` folds a member's loaded stack
  (ahead-of-base = increment count; failed load ⇒ `Unavailable`, other fields zeroed) and its live
  `RepoStatus` probe into one record. `AheadOfBase` is **not** drift (it's the reviewed range); `NoUpstream`
  is informational (excluded from `NeedsAttention`, and not flagged for a detached HEAD). Public
  `sealed record ChangeSetHealth(Members)` rolls up `AllClear` / `AttentionCount`. Depends only on the
  public `RepoStatus` record — no git, no UI.
- **`ChangeSetReviewViewModel`** gained an **`IRepoStatusStore` ctor param** (threaded from
  `ChangeSetReviewWindowsViewModel`, which also gained it — auto-wired) and five members: `MemberHealth()`
  (builds a `ChangeSetHealth` from `_loads` + `_status.For(repoId)`, reactive via a `_loadRevision` read so
  it re-projects on both range reloads and live status-probe changes), `HealthSeverity()` (0 in sync /
  1 attention / 2 unavailable), `HealthLabel()` (aggregate string), `HealthTooltip()` (title + per-member
  lines), and a private `FormatHealthLine`.
- **`Features/Review/ChangeSetReviewHeaderBar.cs`** — a **set health strip** inserted between the set-name
  chip and the progress group: `ChangeSetHealthChip` (a new `Widget<ButtonState>` in the same file, so the
  hover tooltip can attach) renders a status glyph (`CircleCheck`/`TriangleAlert`) colored by severity
  (Success/Warning/Danger) + `HealthLabel`, with `HealthTooltip` on hover. All bindings read the VM's live
  health so the strip updates as probes land. **The Changes-panel twin was intentionally not built** (see
  Phase 6 Deviations); the pure core is ready if it's wanted later.
- **6.2 persisted `ChangeSet` entity: evaluated, NOT built** — convention proved sufficient (see below).
  `RepoStateStore.CurrentSchemaVersion` stays **6**; no migration added.
- **Localization sweep:** all 6 `Localization/Strings/*.json` verified key-identical (**603** keys each).
  Removed 2 orphans (`changesets.review_placeholder`, `changesets.commit_none`). Added 9 health keys
  (`health_title`, `health_all_clear`, `health_attention`, `health_in_sync`, `health_missing`,
  `health_dirty`, `health_no_upstream`, `health_unpushed`, `health_behind`) to all 6.
- **Cheatsheet/keyboard:** verified parity — the cross-repo review window already reuses the single-repo
  `ReviewKeyController` + `ReviewCheatsheetOverlay` via `IReviewSurfaceModel` (Phase 3), so no new work; no
  global shortcut registry exists to register with.
- **Tests:** `GitBench.Tests/ChangeSetHealthTests.cs` (**8**) over the pure `ChangeSetHealth` core.

**Why 6.2 (persisted `ChangeSet`) was skipped (for the Phase-7 agent):** the plan gates it on convention
proving insufficient. It didn't. Detection (`SyncedBranchIndex`), batch ops (`ChangeSetOperations`), both
review surfaces, authoring (`StartChangeSetDialog`), and the Phase-6 health strip all run on live
branch-name correlation + `RepoStatusStore` + each member's auto-resolved base — no set needs a stored
identity, pinned per-set bases, cross-group membership, or set-scoped notes in the MVP. If Phase 7 (or a
later feature) introduces any of those, the paved path is a `Group`-sibling entity in `RepoStateStore.State`
with `CurrentSchemaVersion` 6 → 7 + a migration, exactly as the plan sketches.

**Deviations (Phase 6):** see the "Deviations from the plan text" block under the Phase 6 heading — health
strip on the review *window* header only (Changes-panel twin scoped out, no display to verify it); one
aggregate badge + per-member tooltip rather than per-member chips; ahead-of-base shown but not drift;
no-upstream informational; branch-missing = failed stack load; 6.2 skipped per the plan's own criterion;
6.3 cheatsheet/keyboard parity was already delivered by Phase 3 (verified only).

**Deviations (Phase 5):** see the "Deviations from the plan text" block under the Phase 5 heading — a live
singleton (not a pinned session) since it *is* the Changes panel; a sibling `ChangeSetStagedFileTracker`
(the git index has no progress store to reuse); a self-contained compact commit bar (title-only, no
amend/description) rather than re-binding the shared `CommitBarWidget`; `CommitInAll` skips unstaged members
by omission; and the GUI wiring is manual-pass only (no display).

**Deviations (Phase 4):** see the "Deviations from the plan text" block under the Phase 4 heading —
the dialog has a small VM (unlike Phase 2's VM-less delete dialog) for off-thread default resolution +
gating; the confirm `AsyncCommand`'s work is a no-op that exists only to gate/close (the real create is
fire-and-forget through `CreateInAll`); per-repo start points default to each repo's default branch,
blank → `HEAD`; both entry points gate on ≥2 primaries.

**Deviations (Phase 3):** see the "Deviations from the plan text" block under the Phase 3 heading.

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
- **Phase 4 shipped `CreateInAll` as the sixth batch op** — Phase 5's `CommitInAll` is the next one,
  same `Run`→`RunOverMembers`+summary-toast shape (only the success key and `touchesWorkingTree` differ;
  `Commit` also validates staged state up front per the plan). `CreateInAll` takes per-member
  `(RepoId, StartPoint)` and maps start points via the pure `ResolveStartPoint` (blank → `HEAD`); mirror
  that if a later op needs per-member arguments.
- **Phase 5 shipped `CommitInAll` as the seventh batch op.** It does **not** use `RunOverMembers` (which
  runs every member) — it uses the pure `CommitOverMembers(members, hasStaged, commit)` so members with
  nothing staged are *omitted* from the result (skipped, not a no-op success). The trailer is stamped by
  the pure `StampTrailer(message, name)`. Both are `internal static` and unit-tested directly. `Report`
  grew a `commitCreated` flag (broadcasts `CommitCreatedMessage` per committed member); pass it for any
  future op that produces commits.
- **`CommitDetailsViewModel` now has FOUR surface modes** (was three): single-repo
  (`_currentSha`/`_currentBaseSha`/`_currentRepoId`), single working-tree (`_workingTree` bool), cross-repo
  ranges (`_rangeFiles != null`), and **cross-repo working-trees (`_workingTrees != null`)**.
  `SelectFile`/`CreateFileDiff` branch `_rangeFiles` → `_workingTrees` → `_workingTree` → single. Every
  `Show*` entry and `Clear` must reset **all four** — if you add a fifth mode, thread it through all of
  them the same way (the modes are mutually exclusive).
- **The cross-repo working-tree surface (`ChangeSetWorkingTreeReviewViewModel`) is a live singleton, not a
  pinned session.** It is the one place in the change-set code that tracks the *live* active repo + index
  (every other cross-repo surface is pinned per Locked decision #7) — because it *is* the Changes panel.
  If Phase 6's drift/health work wants a set-health strip in the Changes panel, hang it off this VM's
  `IsAvailable`/`BranchName`/membership; the strip in the review *window* hangs off `ChangeSetReviewViewModel`.
- **Cross-repo staging routes through delegates, not a shared VM.** `ChangeSetStagedFileTracker` takes two
  `Action<Guid, IReadOnlyList<string>>` (stage/unstage) the VM wires to `IGitService.Stage`/`Unstage` +
  a `WorkingTreeChangedMessage` broadcast; the message round-trips back to reload that member (no optimistic
  bump, unlike the single-repo `StagedFileTracker` which drives `LocalChangesViewModel` directly). If a mark
  feels laggy, that's the round-trip — add an optimistic path in `SetViewed` if it matters.
- **The scope toggle + body switch live in `WorkingChanges.cs` / `WorkingChangesTabStrip.cs`.** The body is a
  3-way `Switch<int>` over `layout`+`scope`+`crossVm.IsAvailable`; the footer suppresses itself in cross
  mode. `UnderlineTab` is now generic (`UnderlineTab<T>`). The scope `State<ChangeSetPanelScope>` is
  **session-scoped, not persisted** — if Phase 6 wants it remembered, add it to `PreferencesService` like
  `WorkingChangesLayout`.
- **`ChangeSetWorkingTreeReviewViewModel` runs its aggregation even when the cross view is hidden** (scope =
  ThisRepo) as long as the active branch is a set — every member's `GetLocalChanges` reloads on any member's
  working-tree change. Cheap for small sets; if a big set is a problem, gate the reloads on `scope == AllRepos`.
- **The `StartChangeSetDialog` is the template for future authoring dialogs** that fire-and-forget through
  the coordinator but still want a gated confirm button: give the dialog an `IDialogViewModel` with a
  `Create` `AsyncCommand` whose `work` is `() => null` and whose `onSuccess` does the real work + close,
  gated by a `Derived<bool>`. The empty async hop is the only way to reuse `DialogShell`'s gate/busy
  wiring while keeping the git work inside `ChangeSetOperations` (which owns threading + per-repo toasts).
- **`ChangeSetOperations.RunOverMembers` is `static`** and takes `Func<Repo, GitOutcome>`, so any op
  whose per-repo call returns (or can be mapped to) a `GitOutcome` is testable without touching the
  index or the UI. Non-`GitOutcome` results (like `Pull`'s `PullOutcome`) are mapped inside the
  op lambda before entering the loop — see `PullInAll`.
- **Phase 5 reuses Phase 3's shared plumbing.** `RepoQualifiedPaths` (the qualified-path scheme) and
  `ChangeSetReviewedFiles` (aggregate `IReviewedFileTracker` over qualified paths) are surface-kind
  agnostic — the working-tree surface #4 reuses both verbatim (`ChangeSetReviewedFiles` just wraps a
  `StagedFileTracker`-style store instead of `IReviewProgressStore`, or is generalized). The Phase-3.1
  twin the plan asks for — `ShowWorkingTree(repoId, files)` → `ShowWorkingTrees(sections)` — mirrors
  `ShowRanges` exactly: build a `_rangeFiles`-style resolver keyed by qualified path, thread the bare
  path + member repo into `CommitFileTab.ForWorkingTree` (add a `displayPath` there too, like the range
  ctor already has), and qualify the pushed file list. `_workingTree`/`_rangeFiles` are the two mode
  flags in `CommitDetailsViewModel`; a working-trees mode would be a third (keep them mutually
  exclusive — every entry point already nulls the other two).
- **`CommitDetailsViewModel` now has three surface modes**, gated by two fields: single-repo
  (`_currentSha`/`_currentBaseSha`/`_currentRepoId`), working-tree (`_workingTree`), and cross-repo
  ranges (`_rangeFiles != null`). `SelectFile`/`CreateFileDiff` branch on `_rangeFiles` first. When you
  add the working-trees twin, branch it the same way and reset all mode state in every `Show*` entry.
- **Cross-repo review keys progress by `(RepoId, HeadRef)` per member — unchanged from single-repo.**
  A mark made in the cross-repo window pre-ticks the same file in that repo's single-repo review and
  vice versa (shared `IReviewProgressStore`). This falls out of `ChangeSetReviewedFiles` resolving the
  qualified path back to the member key; don't "fix" it.
- **The cross-repo review window never overrides a member's base** (no per-member base dropdown in
  MVP). Each member auto-resolves (upstream → default → its remembered `PreferredBase`) through the
  same `GitReviewStackSource` a single-repo review uses. If Phase 6's drift/health work wants per-member
  base control, it goes on the header's `ReviewBaseChip` (currently read-only, tooltip-only).
- **A failed member is a `Conflicted` file row, not a window state.** `ChangeSetReviewViewModel` always
  ends in `_details` Loaded (even all-members-failed), so the window is never a dead placeholder. If a
  later phase needs to distinguish "member failed" rows from real conflicts, give them a dedicated
  `FileChangeStatus` rather than overloading `Conflicted`.
- **Phase 6 health strip reactivity (for the Phase-7 agent).** The strip re-derives when a member's
  status probe (`RepoStatusStore`) changes (its `State<GitStatusSummary>.Value` read inside the header's
  `Prop.Bind` is auto-tracked) and when a member's range reloads (`MemberHealth()` reads `_loadRevision`).
  Note the review *window* is **pinned** (Locked #7) but `RepoStatus` reflects each member repo's **live
  active checkout** — so if a member repo is not currently on the reviewed branch, its unpushed/behind/dirty
  signals describe that repo's live state, not the pinned head. That's an accepted best-effort limitation
  of "mostly presentation over `RepoStatusStore`"; a real integration test would checkout-in-all first.

**Consolidated manual-verification list (no display in any phase — everything below compiles + is wired
but was never exercised in a running app; Phase 7's manual pass owns these):**

- **Phase 3/4/5 GUI:** the cross-repo review *window* (header, tree grouped by repo, stacked diff,
  `j`/`k`/`v`/`?` loop, member-failed rows), the "Also on…" / "Review across N repos…" menu, the "Start
  change set…" dialog, and the Changes-panel cross-repo mode (scope toggle, 3-way body switch, compact
  commit bar, commit confirm dialog).
- **Phase 6 health strip:** the badge glyph/color per severity (green check / amber alert / red), the
  aggregate label ("All repos in sync" vs "N of M need attention"), the per-member hover tooltip, and that
  it updates live as a member goes dirty / gains an unpushed commit / has its branch deleted. The
  `70-change-set` fixture already parks these drift states (mismatched checkout, unpushed commit, moved
  base, dirty tree, a member with no remote).
- **Localization:** the 9 new `changesets.health_*` strings render sensibly in all 6 locales (translations
  were authored, not machine-verified in situ).

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

### Phase 3 — The cross-repo review surface (the headline) ✅ DONE

One window, one tree grouped by repo, one progress meter, one keyboard loop.

**Deviations from the plan text:**

- **`ShowRange` was kept byte-for-byte; `ShowRanges` is a new sibling, not a refactor of it.** The
  plan says "`ShowRange` becomes the 1-section case". Taken literally that would qualify the
  single-repo review window's paths (a visible behavior change — a repo-name top folder where there
  was none). To hold "zero behavior change for existing callers", `ShowRange` stays exactly as it was
  (bare paths, one repo pin) and `ShowRanges(IReadOnlyList<DetailsRangeSection>)` is the additive
  N-section path that qualifies. The two share nothing but the surrounding VM.
- **The header uses one compact `ReviewBaseChip` ("N repos") with a per-member bases tooltip**, rather
  than a row of per-member base chips or an interactive bases dropdown. Per-member base *override* is
  not offered on the cross-repo surface (each member still auto-resolves its own base); the chip is
  read-only. The compact form is the plan's stated alternative ("a compact 'bases' dropdown").
- **A failed member renders as one red `Conflicted` row under its repo folder** whose path *is* the
  error message (`changesets.review_member_failed`), rather than a bespoke error-group widget. It
  carries no resolver entry, so it has no diff to open. This keeps the tree/diff widgets repo-blind and
  the window alive; the failed member still counts toward the "files total", so "Review complete" is
  honestly unreachable while a member is broken.
- **A member's `RefsChangedMessage` reloads just that member** (its cached range is replaced and the
  combined list re-driven), reusing the other members' cached stacks — the plan's intent, implemented
  with a small per-member cache rather than reloading the whole set.

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

### Phase 4 — Start a change set (authoring) ✅ DONE

**Deviations from the plan text:**

- **The dialog has a small view model (`StartChangeSetDialogViewModel`), unlike Phase 2's VM-less
  `DeleteChangeSetBranchDialog`.** It needs off-thread per-repo default-branch resolution, live
  name validation, and confirm-button gating — the `CreateBranchDialog`/`CleanBranchesDialog`
  shape — so a plain inline-`Action` widget wasn't enough.
- **The confirm button is wired through an `AsyncCommand` whose work is a no-op (`() => null`).** The
  actual create is fire-and-forget through `ChangeSetOperations.CreateInAll` (the plan's "summary
  reporting as in Phase 2"), so the command exists only to reuse the dialog shell's gate/busy wiring;
  the real action runs in `onSuccess` (gather selection → `CreateInAll` → close). The gate is
  "valid, non-empty branch name **and** ≥1 member checked".
- **Per-repo start points default to each repo's default branch, resolved off-thread and seeded into
  the fields**; a field left blank falls back to `HEAD` at create time (`ResolveStartPoint`), matching
  `CreateBranchDialog`. The resolution is fire-once on open and won't clobber a field the user already
  edited.
- **Both entry points are gated on the group holding ≥2 primaries** (a change set spans ≥2 repos), and
  both open the *same* dialog with the checklist defaulting to all of the group's primaries.
- **Coordinator tests drive the create through `RunOverMembers` + `ResolveStartPoint` over the fake**
  (the same op `CreateInAll` builds), rather than the fire-and-forget instance method — matching the
  existing `RunOverMembers` test precedent. `ResolveStartPoint` is a pure static, tested directly.

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

### Phase 5 — Cross-repo working-tree review + batch commit (the Changes panel) ✅ DONE

The write side of the headline: the review panel under Changes learns the same aggregation the
window got in Phase 3, so "review → stage → commit" works across the set from one surface.
Deliberately after Phase 3, which lands the shared plumbing (`ShowRanges`, the qualified-path
resolver) this phase reuses.

**Deviations from the plan text:**

- **`ShowWorkingTree` was kept byte-for-byte; `ShowWorkingTrees` is a new sibling** (mirroring the
  Phase-3 `ShowRange`/`ShowRanges` deviation exactly). It is a **fourth** mutually-exclusive mode field
  on `CommitDetailsViewModel` — `_workingTrees` (a qualified-path resolver, the working-tree twin of
  `_rangeFiles`) — not a rewrite of the single-repo `_workingTree` bool. Every `Show*` entry + `Clear`
  now nulls all the other mode flags; `SelectFile`/`CreateFileDiff` branch `_rangeFiles` → `_workingTrees`
  → `_workingTree` → single-repo. `CommitFileTab.ForWorkingTree` gained an optional `displayPath` (tab
  identity = qualified path, git target = bare path), like the range ctor already had.
- **The cross-repo working-tree surface is a live singleton, not a pinned session.** Unlike the Phase-3
  review *window* (`ChangeSetReviewViewModel`, pinned to a `ChangeSetSession`), the Changes-panel surface
  (`ChangeSetWorkingTreeReviewViewModel`) tracks the **live** active repo + set membership — because it
  *is* the Changes panel, which always follows the active repo. Membership recomputes on active-repo /
  `RefsChangedMessage` / detection-revision changes; each member's working tree reloads on its
  `WorkingTreeChangedMessage`/`CommitCreatedMessage`. It reuses `RepoQualifiedPaths` verbatim.
- **`ChangeSetReviewedFiles` was *not* reused; a sibling `ChangeSetStagedFileTracker` was written.** The
  Phase-3 tracker wraps `IReviewProgressStore` (Viewed marks); the working-tree mark is the git index,
  which has no store — so the staged tracker holds the aggregated per-qualified-path staged/partial sets
  and routes stage/unstage through the pure `ChangeSetWorkingTreeAggregator.PlanStage` to the owning
  repo's `IGitService.Stage`/`Unstage`. Both trackers are surface-kind-agnostic *shapes*; they don't
  share code because their backing store differs (as the Phase-3 gotcha anticipated: "wraps a
  `StagedFileTracker`-style store instead of `IReviewProgressStore`").
- **The cross-repo surface owns its own compact commit bar** (title + Commit button) rather than
  re-binding the shared `CommitBarWidget`. `CommitBarWidget.Vm` is a concrete `LocalChangesViewModel`,
  and generalizing it to an interface + hiding amend/description would touch a heavily-shared widget with
  no way to visually verify; a self-contained bar in `ChangeSetWorkingTreeReviewView` keeps the blast
  radius to the new mode. It is still "the panel's commit box" (same Changes tab, the shared footer
  steps aside in cross mode). The box is **title-only** (no description/amend — amend is out of scope
  per 5.5); the message is `title + "\n\nChange-Set: <name>"`.
- **`CommitInAll` skips unstaged members by *omitting* them from the result, not recording a no-op.** The
  pure core `CommitOverMembers(members, hasStaged, commit)` filters to members with staged changes before
  committing, so a skipped member contributes no `GitOutcome` at all (it isn't a success *or* a failure);
  the success toast counts only members that actually committed. Staged validation is up-front (in the
  confirm dialog's per-repo summary) *and* re-checked inside `CommitInAll` (via `GetLocalChanges`).
- **No display was available**, so the pure cores are unit-tested (aggregation, stage routing, trailer
  stamping, skip-unstaged, partial-failure) and the GUI wiring (the scope toggle, the body switch, the
  commit bar, the confirm dialog) is **manual-pass only** — it compiles and is wired, but was not
  exercised in a running app.

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

### Phase 6 — Drift panel + (only if needed) persistence ✅ DONE

**Deviations from the plan text:**

- **6.1 landed on the cross-repo review *window* header only** (`ChangeSetReviewHeaderBar`), the
  plan's stated primary target. The Phase-5 agent's suggested Changes-panel strip
  (hanging off `ChangeSetWorkingTreeReviewViewModel.IsAvailable`) was **deliberately scoped out**:
  the review window is where a reviewer weighs whole-set drift, the Changes panel already carries
  per-repo sidebar badges + its own commit summary, and with no display the extra surface couldn't
  be visually verified. The pure `ChangeSetHealth` core is reusable if a later phase wants that strip.
- **The strip is one aggregate badge with a per-member tooltip**, not a row of per-member chips. A
  green check / "All repos in sync", an amber alert / "N of M need attention", or red when a member's
  branch/range is unavailable; the tooltip lists every member's state line-by-line. Compact form keeps
  the header uncluttered and dodges horizontal-overflow risk (untestable without a display).
- **"Ahead of base" is shown but is not treated as drift.** A member's `base..head` increment count is
  the change under review — the normal case — so it never flips the badge; only unpushed/behind commits,
  a dirty tree, or an unavailable member do. **"No upstream" is informational** (surfaced in the tooltip,
  a distinct per-member line) but not counted as attention, so a freshly-started, not-yet-pushed set
  still reads "in sync" rather than lighting up the whole strip.
- **"Branch missing" is detected as a member whose stack failed to resolve** (`ChangeSetMemberLoad.Failed`),
  which is what a deleted branch produces on that member's `RefsChangedMessage` reload — rather than a
  separate branch-existence probe. It renders as the strongest (red) severity.
- **6.2 (persisted `ChangeSet` entity) was evaluated and skipped** per the plan's own criterion
  ("only if convention proves insufficient"). Nothing shipped in Phases 1-6 needs it: detection,
  batch ops, both review surfaces, authoring, and the health strip all run on live branch-name
  correlation + `RepoStatusStore` + each member's auto-resolved base. None of the plan's named triggers
  (pinned bases per set, cross-group membership, set-scoped notes) is in scope for the MVP, so
  `RepoStateStore` stays at `CurrentSchemaVersion = 6` with no migration. See the consolidated note below.
- **6.3 keyboard/cheatsheet parity was already delivered by Phase 3's design** (verified, not rebuilt):
  the cross-repo review window's `ChangeSetReviewRootView` uses the *same* `ReviewKeyController` +
  `ReviewCheatsheetOverlay` the single-repo `ReviewWindowRootView` does, both bound through
  `IReviewSurfaceModel`, so `j`/`k`/`v`/`Space`/`?`/`Esc` and the shortcuts card are identical. There is
  no global/app-level shortcut registry to also register with — the cheatsheet is per-window. The
  localization sweep verified all 6 locales are key-identical (603 keys each) and removed two genuine
  orphans (`changesets.review_placeholder`, dead since Phase 3 replaced the placeholder toast, and
  `changesets.commit_none`, added in Phase 5 but never wired — the commit button is gated so it is
  unreachable).

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
