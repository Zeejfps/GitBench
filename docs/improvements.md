# Improvements: ZGF.Gui framework + GitBench usage

A prioritized, source-grounded list of improvement areas across the `ZGF.Gui` framework
(the `framework/` submodule) and GitBench's use of it. File references use
`GitBench/...` for app code and `framework/ZGF.Gui/...` for framework code.

The framework is in good shape — fine-grained reactive (SolidJS/Svelte family), a clean
`Prop<T>` authoring model, mount-scoped binding lifetimes, reconciler-free structural
reactivity (`Show`/`Switch`), and a NativeAOT-clean render path. GitBench consumes it with
strong MVVM discipline (immutable `State<TState>` + `Slice` projections, generation-guarded
async lanes, typed outcomes). The items below are the highest-leverage refinements, not a
rewrite.

---

## Framework (ZGF.Gui)

### 1. Make `Derived` lazy (pull) instead of eager (push) — highest impact
`Derived<T>.Recompute` runs on **every** upstream invalidation regardless of whether anything
observes the derived value (`framework/ZGF.Gui/Observables/Derived.cs:79`). Because
`ViewModelBase.Slice` mints one `Derived` per projected field
(`GitBench/Infrastructure/ViewModelBase.cs:60`), a VM with 20–50 slices recomputes **all** of
them synchronously on every `State.Update`, even slices bound only to off-screen/collapsed
views. This is the root cost behind "one reload cascades through many subscriptions."

**Fix:** dirty-mark on invalidate and recompute lazily on read (the SolidJS memo model), and/or
skip recompute when a derived has no downstream subscribers. Biggest single win for update cost.

### 2. Add batching / transactions and glitch-freedom
State writes propagate synchronously in-stack with no coalescing
(`framework/ZGF.Gui/Observables/State.cs:28`), and diamond dependencies (A→B, A→C, both→D)
recompute D twice per root change. Add a `Batch`/`Transaction` primitive so a multi-field
update fires a single propagation wave, plus generation- or topology-based scheduling to avoid
redundant intermediate recomputes.

### 3. Stop churning the dependency set on every recompute
`Derived.Recompute` tears down and rebuilds the entire `_dependencies` set and all event
subscriptions each run (`framework/ZGF.Gui/Observables/Derived.cs:81`). Fine when cold, wasteful
on frequently-recomputed deriveds. Diff the dependency set instead of full teardown/rebuild.

### 4. Provide a keyed `Each<T, TKey>`
`Each<T>` reconciles by item **reference** identity
(`framework/ZGF.Gui/Widgets/Each.cs`). Any app whose models are value/record lists must
hand-roll value→VM reconciliation — which GitBench did as
`GitBench/Infrastructure/KeyedViewModelList.cs`. Promote a keyed `Each<T, TKey>` into the
framework and delete the app-side layer.

### 5. Firm up the DI container semantics
`Context` selects "the constructor with the most parameters"
(`framework/ZGF.Gui/Context.cs:111`) and returns a **new transient instance** for any
unregistered-but-constructible type on every `Get<T>()` (`framework/ZGF.Gui/Context.cs:90`).
The "most params" heuristic is fragile and transient-by-default surprises. The
`GitIdentityService` post-construction `AttachIdentityResolver` dance
(`GitBench/App/AppServices.cs`) is a workaround for a circular dependency the container can't
express. Prefer explicit/source-generated registration, enforce a single public constructor, and
drop implicit transient construction (or make it opt-in).

### 6. Add mid-level primitives so apps drop to raw `View` less often
Two needs recur in app code with no framework support:
- **Virtualized/scrollable lists:** normalized scroll state isn't exposed observably, so
  `NotifyScrollChanged`/`ScrollMath` get hand-rolled across `CommitsView`, `BranchesView`,
  `DiffContentView`, and `LocalChangesPanel` (`GitBench/Controls/ScrollMath.cs` plus per-view
  copies). Expose normalized scroll as an observable on the scroll pane.
- **Themed text:** there's no `Theme.Text()` factory, so ~70 manual `BindThemedTextColor` calls
  exist across views. A themed-text widget/factory would shrink every view's preamble.

The declarative `Widget` path is pleasant, but the hot-path views drop to `ContainerView` +
direct canvas drawing (`GitBench/Features/Commits/CommitsView.cs` ~890 lines,
`GitBench/Features/Diff/DiffContentView.cs` ~1055 lines) with manual layout, hit-testing,
`TextStyle` farms, and shared mutable `RectStyle`. A virtualized row-list widget especially would
keep far more code in the declarative lane.

### 7. Split `ZGF.Observable` into its own assembly
The reactive core physically lives inside the `ZGF.Gui` project
(`framework/ZGF.Gui/Observables/`). Extracting it makes the (genuinely good) signal system
reusable headless and independently testable.

---

## GitBench (app)

Several of these were already scoped in the now-removed `codebase-cleanup.md`; carried here so
the remaining work isn't lost.

### 8. Decompose `GitService`
`GitBench/Git/GitService.cs` is ~2994 lines behind a 99-method `IGitService`
(`GitBench/Git/IGitService.cs`). Split into per-domain partial classes + role interfaces
(branches, commits, stash, worktree, submodule, diff). This was explicitly deferred in the prior
cleanup pass.

### 9. Replace boolean-flag state machines with typed states in large VMs
`GitBench/Features/LocalChanges/LocalChangesViewModel.cs` (~1031 lines) and
`GitBench/Features/Branches/BranchesViewModel.cs` (~913 lines) still encode hidden state machines
in independent booleans. Model each as one sealed-type field (e.g.
`PendingOp = None | CheckingOut(sha) | …`, `EditorMode = Normal | Amending(...) | Merging(...)`)
so invalid combinations become unrepresentable and guard cascades become exhaustive switches.

### 10. Extract duplicated UI scaffolding
- `BuildLabeledRow` is reimplemented across `ResetCommitDialog`, `MergeBranchDialog`, and
  `CreateTagDialog`; checkbox/inset magic numbers (`Height = 22`/`28`) are scattered across ~8
  dialogs. Extract a shared dialog-fields helper.
- Per-view `TextStyle` farms (e.g. `GitBench/Features/Branches/BranchesView.cs`, ~16 instances)
  → a shared themed-style palette.

### 11. Adopt `Show`/`Switch` for the remaining hand-rolled conditionals
Now that `Show`/`Switch`/`SwapRegion` are in the framework, replace the hand-rolled
`BindChildren(() => new[]{cond}, …)` visibility toggles (e.g. `GroupHeaderRow`, the lone banner
views, dialog empty-states). Keep the deliberate "repaint one heavy mounted view" sites on `Prop`,
not `Show`.

### 12. Even out service interface usage
`GitService` and the repo stores have `I…` interfaces, but `PreferencesService`,
`UpdateService`, and the `*SyncService` classes don't — uneven testability/mockability. Add
interfaces where these are collaborators worth faking.

### 13. Surface tunable heuristics
Hardcoded constants like the snapshot warm-set `N=4`
(`GitBench/Features/Repos/RepoSnapshotStore.cs`) are reasonable defaults but undocumented and
untunable; lift to named, documented config.

---

## Suggested sequencing
1. **Framework #1 (lazy `Derived`)** + **#2 (batching)** — they compound, and every VM benefits.
2. **Framework #4 (keyed `Each`)** — deletes an app-side layer.
3. **App #8 (`GitService` split)** + **#9 (typed VM states)** — the big readability/correctness wins.
4. **Framework #6 (virtualized list / themed text)** then **App #10/#11** — collapse the UI duplication.
5. The rest as cleanup.
