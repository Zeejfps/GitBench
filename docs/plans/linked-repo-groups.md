# Linked Repo Groups — work across N repos as one workspace

> A group of repos, marked **linked**, becomes selectable in the RepoBar like a repo. Selecting it
> activates an **aggregate scope**: one change list, one staging surface, one commit box, one
> branch action — fanned out over the member repos. Git has no cross-repo atomicity, so the
> aggregate is *honest*, not *transparent*: membership is visible in the file tree and every write
> reports per-repo outcomes.

## Relationship to `cross-repo-change-sets.md`

That plan solves the same workflow with **implicit membership** — same branch name in ≥2 repos of
a group *is* the change set, discovered by a background `SyncedBranchIndex`. This plan replaces
that with **explicit membership** (the group, which the user already curates by drag-and-drop) and
promotes the aggregate from a *mode toggle inside the Changes panel* to a *selectable scope*.

- **Superseded:** Phase 1 (detection index), Phase 5.3 (the "This repo / All repos" toggle),
  locked decisions 1 and 7.
- **Kept wholesale:** the `ShowRanges` / `ShowWorkingTrees` widening (Phases 3.1, 5.1), the
  repo-qualified path resolver (locked decision 4), `IReviewSurfaceModel` implementations #3/#4,
  batch-ops-as-loops with per-repo `GitOutcome`s (decision 5), the `Change-Set:` trailer
  (decision 6), and the no-interleaved-rail call (decision 3).

Net effect: the detection phase disappears, membership becomes predictable, and the review
machinery that plan designed is reused verbatim.

## Why not submodules or a monorepo

Both are strictly better when repos are *always* in lockstep. Linked groups earn their keep in the
common case where repos are **mostly** in sync but independently owned, separately versioned, and
occasionally drift. That framing sets the design constraint: **drift reporting is the feature, not
a compromise.** A UI that hides which repo a change landed in is worse than no feature.

## Grounding — what exists

- **`Group`** (`Features/Repos/Group.cs`) — a live entity: `Guid Id`, `State<string> Name`,
  `State<bool> IsCollapsed`, `ObservableList<Guid> RepoIds`. Persisted via `GroupState` in
  `RepoStateStore` (`CurrentSchemaVersion = 6`, with real migration logic). **Adding
  `State<bool> IsLinked` is a one-field schema bump.**
- **`DragController`** (`Controls/DragController.cs`) — repos already drag between and within
  groups (`DragKind.Repo`), groups already reorder. **The "drag repos together" interaction the
  idea calls for is already shipped**; linking is a property of the resulting group, not a new
  gesture.
- **`IGitService`** (`Git/IGitService.cs`) — one stateless singleton, ~105 methods, **every one
  takes the `Repo` to operate on**. Batching is a `foreach`. No plumbing needed.
- **Messages** (`Messages/`) — nearly all carry a `Guid RepoId` (`RefsChangedMessage`,
  `WorkingTreeChangedMessage`, `CommitCreatedMessage`). An aggregate VM subscribes for a *set*.
- **Per-repo stores compose for free** — `RepoStatusStore`, `RepoSnapshotCache`,
  `RepoOperationsStore`, `ReviewProgressStore` are all keyed by repo id. Marks made in a linked
  review pre-tick in that repo's single-repo review, and vice versa.

### The one real obstacle: `Active`

`IRepoRegistry.Active` is a `State<Repo?>` (`IRepoRegistry.cs:20`) read directly by ~30 view
models across 100+ call sites. Three ways to let "the selected thing" be a group:

1. **Widen to `IRepoScope`** (one-or-many) — correct, but a large mechanical sweep.
2. **Parallel aggregate mode** — smears `if (aggregate)` branching into every panel. Worse.
3. **Synthetic `RepoKind.Virtual` record** — `Active` stays typed, but a virtual `Repo` reaching
   `IGitService` runs `git` in a bogus `Path`. **Rejected**: the failure is silent and the type
   system stops helping.

**Decision: (1), staged.** Introduce `IRepoScope` alongside `Active` and migrate only the VMs that
execute git work or read the change list. Panels that merely display the active repo's *name* keep
reading `Active`, which resolves to the group's lead member. The sweep is bounded by "does this VM
call `IGitService`", not by call-site count.

```csharp
// One or many repos, plus the identity of the scope itself.
public sealed record RepoScope(
    Guid Id,                        // repo id, or group id when Kind == Linked
    ScopeKind Kind,                 // Single | Linked
    string DisplayName,
    IReadOnlyList<Repo> Members)    // exactly one when Kind == Single
{
    public Repo Lead => Members[0];
}
```

## Surface-by-surface: what composes

| Surface | Behavior in a linked scope |
|---|---|
| **Changes / staging** | Union of all members' working trees, **tree grouped by repo** (top-level folder per member). Checkbox stages in the owning repo. |
| **Commit** | One message box, one button → N real commits, each stamped `Change-Set: <branch>`. Members with nothing staged don't commit. Result is a **per-repo summary**, never a single "Committed". |
| **Diff / review** | Aggregated behind `IReviewSurfaceModel` (implementations #3/#4 from the prior plan). `j`/`k`/`n` walk across repo boundaries for free. |
| **Branches** | Branch rows show member coverage (`3/3`, or `2/3` + drift badge). Create/checkout/delete fan out. |
| **Push / pull / fetch** | Fan out, per-repo outcomes, no rollback. Half-landed is a *reported state*. |
| **History** | **Per-repo lanes** — N stacked sections, each the existing graph widget over one member. No interleaving: commits across repos have no causal order, and timestamp-merging fabricates one. |
| **Rebase / cherry-pick / stash / conflict resolution** | **Not aggregated.** Route to the member repo; the linked scope offers a repo picker. |

## Drift — the normal case, not the edge case

Members disagreeing is expected. The scope needs a coherent answer for "what branch am I on?" when
they don't. Sources are mostly `RepoStatusStore`, which already tracks
`(CurrentBranchName, Ahead, Behind, IsDirty)` per repo.

- **Branch label**: the common branch when all members agree; otherwise `"mixed"` + a drift chip
  listing the outliers.
- **Drift strip** in the scope header: per member — branch mismatch, ahead/behind, unpushed,
  dirty, branch missing, no remote, mid-operation (rebase/merge in progress).
- **Guardrail**: writes that assume alignment (batch commit, batch push) surface the mismatch in
  the confirm step rather than silently skipping members.
- **Mid-operation members are excluded from batch writes** and flagged, never force-driven.

## Locked decisions

1. **Membership is the group.** `Group.IsLinked` toggles aggregate behavior; no new entity, no
   detection index. A repo belongs to at most one group already, so membership stays unambiguous.
2. **`IRepoScope` is introduced; `Active` is migrated incrementally**, git-executing VMs first.
   A synthetic virtual `Repo` is explicitly rejected.
3. **The file tree always shows repo membership** (top-level folder per member), in every
   aggregate surface. There is no "pretend it's one repo" rendering.
4. **File identity inside aggregating VMs is a repo-qualified path** (`"<DisplayName>/<path>"`,
   de-duplicated by suffixing). Git-facing calls always receive the unqualified repo-relative path
   plus the member's `Repo`. Shared widgets stay repo-blind.
5. **Every write reports per-repo outcomes.** No rollback, no rolled-up boolean. Partial success
   is a first-class result, matching git reality.
6. **History is per-repo lanes.** No interleaved cross-repo graph, ever.
7. **Interactive single-repo operations are not aggregated** and route to a member.
8. **Naming.** User-facing: a **linked group**, "Work as one". Code: `Features/Repos/` for the
   scope and linking; `Features/ChangeSets/` for batch-op coordination; aggregate review surfaces
   live in `Features/Review/` beside their siblings.

## Implementation plan — outside-in

Each phase runs and is testable before the next starts.

### Phase 1 — `IRepoScope` + a selectable linked group

No aggregation yet. Prove the selection model and the type migration in isolation.

- **1.1** `Group.IsLinked` (`State<bool>`) + `GroupState` field; `RepoStateStore`
  `CurrentSchemaVersion` 6 → 7 with a migration defaulting existing groups to unlinked.
  `IRepoRegistry.SetGroupLinked(Guid, bool)`.
- **1.2** `RepoScope` + `State<RepoScope> ActiveScope` on `IRepoRegistry`. `Active` becomes a
  derived read (`ActiveScope.Lead`) so no existing caller breaks. `SetActiveScope(Guid)` accepts a
  repo id or a linked group id.
- **1.3** RepoBar: a linked group header is selectable (link glyph + member count); "Work as one"
  in the group context menu. Selecting it sets a `Linked` scope; the scope's `Lead` keeps every
  un-migrated panel working unchanged.
- **Ship / test:** link a 3-repo group → the header selects, shows the glyph, and the app behaves
  exactly as if the lead repo were active. Unlink → back to normal. Migration test: a v6
  `state.json` loads with all groups unlinked.

### Phase 2 — Aggregate changes + batch commit

The headline write path.

- **2.1** `ChangeSetOperations` (`Features/ChangeSets/`) — loops `IGitService` per member off-thread
  (`RunBackground` conventions), collects `ChangeSetOpResult`, reports one summary toast with
  expandable per-repo detail. Precedent for the loop-and-schedule shape: `WorktreeSyncService`.
- **2.2** Widen `CommitDetailsViewModel`: `ShowWorkingTree(repoId, files)` →
  `ShowWorkingTrees(sections)`, single-section unchanged. Repo-qualified path resolver lives here;
  tab creation passes the *member's* repo id into `CommitFileTab` (already per-tab).
- **2.3** `LocalChangesViewModel` reads `ActiveScope` instead of `Active` — this is the ~16-site
  concentration point. Union the members' `GetLocalChanges`, refresh on each member's
  `WorkingTreeChangedMessage`, group the tree by repo.
- **2.4** Batch commit through the existing commit box: one message + `Change-Set:` trailer,
  per-repo staged summary in the confirm step, per-repo outcomes.
- **Ship / test:** edit files in all three members → one change list with three repo folders; tick
  a file → it stages in its own repo (verify via sidebar badges); commit → each member with staged
  changes gets the commit with the trailer; a member with a rejecting hook reports its own failure
  without blocking the others.

### Phase 3 — Aggregate review surface

- **3.1** `ShowRanges` (the `ShowWorkingTrees` twin) + `ChangeSetWorkingTreeReviewViewModel` and
  `ChangeSetReviewViewModel`, both `: IReviewSurfaceModel` (implementations #3/#4). Per-member
  base resolution through the existing `IReviewStackSource`; `(RepoId, HeadRef)` progress keys
  unchanged, so marks compose with single-repo reviews.
- **3.2** A member whose range fails to resolve renders as an inline error group in the tree, not
  a dead window.
- **3.3** Keyboard flow needs no new work — `ReviewKeyController` binds the seam and the file list
  orders members contiguously.
- **Ship / test:** review across the linked group → one tree, two repo folders, correct per-repo
  diff content, `n` crosses the boundary, marking Viewed pre-ticks in that repo's single-repo review.

### Phase 4 — Batch branch ops + drift

- **4.1** Fan out create / checkout / pull / push / fetch / delete over members, per-repo outcomes.
  "Start a branch across the group" as a dialog (the `MergeBranchDialog` pattern): name field +
  member checklist + per-repo start point (default: each repo's own default branch tip).
- **4.2** Branch rows show member coverage; the scope header gets the drift strip. Mostly a
  re-presentation of `RepoStatusStore` data.
- **4.3** Mixed-checkout guardrail: checking out a branch in one member of a linked group offers
  "switch the other N too".
- **Ship / test:** start `feature/y` across 3 repos → all switch; a name collision in one repo
  reports that failure and still creates the others; kill a remote → "Push all" reports 2 ok / 1
  failed, legibly.

### Phase 5 — History lanes + tail

- **5.1** History renders N per-repo sections (existing graph widget per member, collapsible).
- **5.2** Interactive ops (rebase, cherry-pick, stash, conflict resolution) route to a member via
  a picker.
- **5.3** Localization sweep across all 6 `Strings/*.json`; cheatsheet entries.

### Phase 6 — Verification

- Real-git integration tests (`ReviewStackTests` fixture pattern): batch create/checkout/delete,
  batch commit trailer, cross-repo aggregation with per-member base resolution, partial-failure
  reporting, path collision (`src/index.ts` in two members).
- Standing fixture: extend `scripts/make-test-repos.sh` with a `70-linked-group` scenario — three
  sibling repos sharing `feature/cross-repo`, parking the drift states (mismatched checkout,
  unpushed commit, dirty tree, a member with no remote, a member mid-rebase) and the path collision.
- Manual pass: full loop (link → start branch → edit → review → commit → push all) plus each drift
  case.

## Open questions

1. **Lead member selection.** First in `RepoIds` (recommended — user-ordered by drag, already
   persisted) vs an explicit "primary" pin per group.
2. **Can a linked group contain worktrees/submodules?** Recommend primaries only, matching the
   prior plan — a submodule pointer bump is the parent repo's change.
3. **Does linking survive a member being removed from the group?** Recommend yes, silently; a
   one-member linked group behaves as a single repo.
4. **Scope hotkeys.** Whether the 1-9 slots (`AssignHotkey`) can bind a linked group as well as a
   repo. Cheap to add, but the slot map is currently `Guid → repo`.
