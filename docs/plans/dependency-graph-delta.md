# Dependency-graph delta

Plan for diff-improvement #4: *"show which module edges the change added or removed, and flag
new cycles or layer-boundary crossings."* Grounded in the current codebase; phased so an
MVP ships before the visual graph.

## What it does

Given the scope of a change (a commit, or the working tree's staged/unstaged set), build a
directed **module dependency graph** for the *before* and *after* states, then surface the
difference:

- **Added / removed edges** — which module `A → B` dependencies the change introduced or dropped.
- **New cycles** — dependency cycles that exist in *after* but not *before*.
- **Layer-boundary crossings** — added edges that violate the project's layering rules
  (e.g. `GitBench.Git` reaching up into `GitBench.Features.*`).

The point is the *delta*, not a full repo audit: we flag what *this change* introduced, so a
pre-existing violation in untouched code is not noise.

## Context: how a change is currently modeled

The diff stack already gives us everything needed to define "before/after" for a change:

- **Commit scope.** `CommitDetails` carries `ParentShas` and `Files` (`Features/Commits/CommitDetails.cs:31`).
  Before = `sha~1`, after = `sha`. `CommitDetailsViewModel` already loads this off-thread
  (`Features/Commits/CommitDetailsViewModel.cs:116`) and selecting a file builds a
  `DiffTarget(path, DiffSide.Commit, sha)` (`:89`).
- **Working-tree scope.** `DiffSide.{Unstaged,Staged}` (`Git/DiffResult.cs:3`). Before/after are
  index-vs-HEAD or worktree-vs-index, exactly as `GetFileText` already resolves them
  (`Git/GitService.cs:2561`).
- **Reading file content at a revision.** `IGitService.GetFileText(repo, path, side, oldSide, sha)`
  (`Git/IGitService.cs:60`) returns one side's full text. Internally it is `git show <rev>:<path>`
  (`Git/GitService.cs:2591`).
- **Reading the *whole tree* at a revision.** `GitService` already opens the repo in-process with
  LibGit2Sharp (`using var lg = new Repository(repo.Path)` — `Git/GitService.cs:94`) and uses
  `git ls-tree` elsewhere (`:2194`). A whole-graph build can walk the tree + read blobs in-process,
  avoiding ~1400 `git show` process spawns.

The diff feature also establishes the patterns this feature will mirror:

- **ViewModel pattern.** `DiffViewModel : ViewModelBase<DiffState>` with `Slice(...)` projections,
  `RunBackground<T>(work, onResult)` for off-thread loads, and bus subscriptions
  (`Features/Diff/DiffViewModel.cs:46`, `:421`, `:126`).
- **Graceful degradation.** `SyntaxHighlighter.Highlight` returns `null` ("render plain") on any
  failure (`Features/Diff/SyntaxHighlighter.cs:55`). The dependency extractor degrades the same
  way: an unparseable / unsupported file contributes no edges, never an error.
- **Language registry.** `LanguageRegistry` maps extension → language id in one table
  (`Features/Diff/LanguageRegistry.cs:13`). The extractor registry will mirror this shape.
- **Custom graph drawing already exists.** `CommitGraphRenderer` is a stateless,
  pure-function renderer drawing the commit-DAG lanes (`Features/Commits/CommitGraphRenderer.cs`),
  with `LaneAssigner` doing the layout. This is the precedent for the (stretch) visual node-link view.
- **Secondary windows.** `DiffWindowsView` is a headless `Widget` that reflects an
  `ObservableList` into real OS windows via `ISecondaryWindowFactory`
  (`Features/Diff/DiffWindowsView.cs:20`), wired once in `App/AppView.cs:44`. A pop-out dependency
  view would follow this exactly.
- **DI.** `Context.Require<T>()` auto-constructs unregistered classes by ctor injection
  (`framework/ZGF.Gui/Context.cs:95`), so a new VM needs an explicit `AddSingleton` only if it must
  be shared / eager / a bus subscriber (`App/AppServices.cs`).

No Roslyn / `Microsoft.CodeAnalysis` is referenced anywhere (deps are LibGit2Sharp, TextMateSharp,
Velopack — `GitBench/GitBench.csproj:21`). Adding it would be a large dependency; we avoid it.

## Decisions

1. **Module granularity = C# namespace (file-scoped).** 370 files use file-scoped `namespace X;`
   and the namespaces map 1:1 to folders/features (`GitBench.Git`, `GitBench.Features.Diff`,
   `GitBench.Controls`, …). Namespaces are the cheapest reliable signal *and* the natural unit for
   layering. Edges come from `using` directives + the declaring `namespace`. Project-level edges
   (`.csproj` `ProjectReference`) are a secondary, later granularity (see Deferred).

2. **No AST.** A line-scan extractor for `namespace` + `using` (handling `using static`,
   `global using`, aliases, `using X = ...;`) is enough at namespace granularity and matches the
   "lightweight, degrade to nothing" ethos. Type-level resolution would need Roslyn; out of scope.

3. **Delta = whole-graph before vs after (correctness first).** Cycles and layer crossings cannot
   be decided from changed files alone — a cycle can route through untouched code. Phase 1 builds
   the full namespace graph at both revisions and diffs the edge sets. Incremental/cached builds
   (start from a cached HEAD graph, re-extract only changed files) are a Phase-4 optimization.

4. **Flag deltas, not the baseline.** Layer violations and cycles are reported only when *new* in
   *after*. A pre-existing cycle/violation is suppressed (it shows in an optional full-audit mode
   only). This sidesteps the fact that some existing edges already cross layers (e.g.
   `Messages/OpenDiffWindowMessage.cs:1` references `Features.Diff.DiffTarget`).

5. **Layer rules live in a code table** (`LayerRules.cs`), mirroring `LanguageRegistry`. Each rule
   is `(fromPrefix, toPrefix, Allowed|Forbidden)`. Initial ruleset derived from the namespace
   layering observed in the repo (below). Externalizing to JSON is deferred.

6. **MVP UI is textual.** Grouped lists (added edges / removed edges / new cycles / new
   violations). The visual node-link graph is a stretch built on `CommitGraphRenderer`'s approach.

### Layering model (initial `LayerRules`)

Derived from namespace counts (`GitBench.Features.* = features`, `Controls`/`Widgets = UI
primitives`, `Git`/`Infrastructure`/`Platform`/`Theming`/`Localization`/`Messages` = foundation,
`App` = composition root):

| Layer | Namespaces | May depend on |
|---|---|---|
| L0 foundation | `Git`, `Infrastructure`, `Platform`, `Theming`, `Localization` | (other foundation only) |
| L1 contracts | `Messages` | foundation |
| L2 UI primitives | `Controls`, `Widgets` | foundation, contracts |
| L3 features | `Features.*` | all below |
| L4 app | `App` | everything |

Forbidden-edge examples the delta would flag if a change introduced them: `Git → Features.*`,
`Widgets → Features.*`, `Controls → Features.*`, `Features.A → Features.B` if we later choose to
forbid cross-feature coupling. Cross-feature edges are *allowed* initially (the repo has them) and
treated as a soft signal, not a violation.

## Components

### 1. Data model — `Features/Dependencies/` (new folder)

```
public readonly record struct ModuleId(string Name);            // namespace, e.g. "GitBench.Git"
public readonly record struct ModuleEdge(ModuleId From, ModuleId To);

public sealed record ModuleGraph(
    IReadOnlySet<ModuleId> Modules,
    IReadOnlySet<ModuleEdge> Edges);

public enum EdgeDeltaKind { Added, Removed }
public sealed record EdgeDelta(ModuleEdge Edge, EdgeDeltaKind Kind);

public sealed record DependencyDelta(
    IReadOnlyList<EdgeDelta> Edges,
    IReadOnlyList<IReadOnlyList<ModuleId>> NewCycles,     // each is one new SCC cycle
    IReadOnlyList<EdgeDelta> LayerViolations);            // subset of Added edges crossing a forbidden boundary
```

### 2. Namespace extractor — `NamespaceExtractor.cs`

`IReadOnlySet<ModuleEdge> Extract(string fileText, string path)`:
- Detect language via a registry mirroring `LanguageRegistry` (C# first; the rest return empty).
- Parse the declaring namespace(s) and `using` directives (line scan, regex-light, tolerant of
  `global using`, `using static N.T;`, `using Alias = N.T;`). Map each `using` to its namespace.
- Emit `From = declaredNamespace`, `To = usedNamespace` for each import where both are in-repo
  (filter to namespaces prefixed `GitBench` / configured roots, so framework + BCL edges drop out).
- Return empty on any parse trouble — never throw.
- Pure + synchronous → trivially unit-testable, like `HunkPatchBuilder` / `LanguageRegistry`.

### 3. Graph builder — `ModuleGraphBuilder.cs` (in the Git layer or Dependencies, headless)

`ModuleGraph Build(Repo repo, GraphScope scope)` where scope identifies a revision
(`Commit sha` / `Staged` / `Unstaged`):
- Enumerate source files at that revision. For a commit: LibGit2Sharp tree walk on `lg.Lookup<Commit>(sha).Tree` (reuse the `new Repository(repo.Path)` pattern at `Git/GitService.cs:94`). For working tree: enumerate tracked files via `git ls-files` (already used at `:1188`) + on-disk reads.
- Read each blob's text in-process; feed to `NamespaceExtractor`; union the edges.
- Surface this behind `IGitService` (e.g. `ModuleGraph BuildModuleGraph(Repo, GraphScope)`), keeping all git/IO in the git layer, consistent with `GetFileText` (`Git/IGitService.cs:60`).

### 4. Delta + analysis — `DependencyAnalyzer.cs` (pure)

`DependencyDelta Analyze(ModuleGraph before, ModuleGraph after, LayerRules rules)`:
- **Edge diff:** set difference both ways → `Added` / `Removed`.
- **Cycle detection:** Tarjan SCC on `after` and on `before`; a cycle (SCC of size > 1, or a self-loop) present in `after` but not `before` → `NewCycles`. (Graph-algorithm precedent already in repo: `LaneAssigner` / `CommitGraphRenderer`.)
- **Layer violations:** for each `Added` edge, look up `rules.Classify(From, To)`; if `Forbidden`, add to `LayerViolations`.
- Pure → unit tests assert exact deltas on hand-built graphs.

### 5. Layer rules — `LayerRules.cs`

Static table (extension point mirrors `LanguageRegistry`): `Verdict Classify(ModuleId from, ModuleId to)` resolving the longest-prefix layer for each side and consulting the rule matrix above.

### 6. ViewModel — `DependencyDeltaViewModel : ViewModelBase<DependencyDeltaState>`

- State: `Placeholder | Loading | Loaded(DependencyDelta)` (same shape as `CommitDetailsRenderState`, `Features/Commits/CommitDetailsViewModel.cs:15`).
- Scope source: subscribe to `CommitSelectedMessage` (commit scope) and `WorkingTreeChangedMessage` (working-tree scope), reusing the exact subscriptions `CommitDetailsViewModel` / `DiffViewModel` use (`Features/Diff/DiffViewModel.cs:126`).
- On scope change: `RunBackground` → build before/after graphs via `IGitService`, `DependencyAnalyzer.Analyze`, push `Loaded`. Mirror the lane/staleness handling (`Gen.Bump`, `RunBackground`) from `DiffViewModel.StartLoad` (`:389`).
- Clicking an edge can broadcast the existing `OpenDiffWindowMessage` / select the relevant file so the user can jump from "edge added" to the diff that added it.

### 7. View — `DependencyDeltaView` (Widget)

- **MVP:** a `Column` of titled sections (Added / Removed / New cycles / Violations) using the existing list/row widgets (`Widgets/`, `Controls/`). Violations and new cycles use the themed warning/banner styles already in `Theming/`.
- **Stretch:** a node-link canvas. Build a stateless renderer modeled on `CommitGraphRenderer` (pure draw over `ZGF.Geometry` + `ICanvas`), with a simple layered layout (rank nodes by layer; or reuse a lane-style assignment from `LaneAssigner`).

### 8. UI surface / entry point

Recommended: a **panel alongside commit details** (the scope already lives there) plus an optional **pop-out**, since the analysis is change-level, not file-level.

- Embed: add the panel to the commit-details / diff region, gated by a toolbar toggle (the diff window toolbar pattern is `Features/Diff/DiffWindowToolbar.cs`).
- Pop-out (optional): a new `OpenDependencyDeltaMessage` + a headless `DependencyDeltaWindowsView` reflecting an `ObservableList` into OS windows, copied from `DiffWindowsView` (`Features/Diff/DiffWindowsView.cs:20`) and registered once in `App/AppView.cs` next to `new DiffWindowsView()` (`:44`).

### 9. Wiring

- Register the VM in `App/AppServices.cs` only if shared/eager; otherwise let `ctx.Require<>` construct it (`framework/ZGF.Gui/Context.cs:95`).
- Add any new message as a `readonly record struct` under `Messages/` (e.g. `OpenDependencyDeltaMessage`), following `OpenDiffWindowMessage` (`Messages/OpenDiffWindowMessage.cs:9`).
- New localized strings go through the localization generator (see the i18n flow already in place).

## Phasing

- **Phase 0 — extractor + model.** `NamespaceExtractor`, data records, extractor registry. Unit tests. No UI.
- **Phase 1 — graph + analysis.** `ModuleGraphBuilder` (commit scope), `IGitService.BuildModuleGraph`, `DependencyAnalyzer` (edge diff only). Unit tests on hand-built graphs + a small fixture repo.
- **Phase 2 — cycles + layers.** Tarjan SCC new-cycle detection, `LayerRules`, violation flagging. Tests.
- **Phase 3 — textual UI.** `DependencyDeltaViewModel` + `DependencyDeltaView` panel, wired to commit selection; click-through to diffs. Working-tree scope.
- **Phase 4 — stretch.** Visual node-link graph (`CommitGraphRenderer`-style); incremental/cached graph builds; project-level (`.csproj`) granularity; TS/JS extractor; full-audit (non-delta) mode.

## Files to create / modify

Create (under `GitBench/Features/Dependencies/` unless noted):
- `ModuleModel.cs` (records), `NamespaceExtractor.cs`, `ExtractorRegistry.cs`,
  `DependencyAnalyzer.cs`, `LayerRules.cs`, `DependencyDeltaViewModel.cs`, `DependencyDeltaView.cs`
- `Git/ModuleGraphBuilder.cs` (or fold into `GitService`), plus `BuildModuleGraph` on
  `Git/IGitService.cs`
- (optional pop-out) `Messages/OpenDependencyDeltaMessage.cs`,
  `Features/Dependencies/DependencyDeltaWindowsView.cs` + `…ViewModel.cs`
- Tests: `GitBench.Tests/NamespaceExtractorTests.cs`, `DependencyAnalyzerTests.cs`,
  `LayerRulesTests.cs` (xUnit, flat files like `LanguageRegistryTests.cs` / `HunkPatchBuilderTests.cs`)

Modify:
- `Git/IGitService.cs` + `Git/GitService.cs` — add the graph build
- `App/AppView.cs` and/or the commit-details view — mount the panel/window
- `App/AppServices.cs` — register the VM if shared
- Localization catalogs — new strings

## Performance & threading

- Whole-graph builds read every source blob. Use LibGit2Sharp in-process (`Git/GitService.cs:94`),
  not per-file `git show`, to avoid ~1400 process spawns.
- All builds run via `RunBackground` off the UI thread (`Features/Diff/DiffViewModel.cs:421`); stale
  results dropped via `Gen`/lane guard as in `StartLoad`.
- Apply the same guardrails as highlighting: skip absurdly large blobs (`SyntaxHighlighter.MaxFileChars`,
  `Features/Diff/SyntaxHighlighter.cs:21`) and cap total work; degrade to "no edges from this file."
- Cache per-tree graphs keyed by tree/commit SHA (immutable), so re-selecting a commit is instant;
  Phase 4 incremental build re-extracts only `CommitDetails.Files`.

## Risks & open questions

- **Namespace ≠ file granularity.** Edges are aggregated to the namespace; the view should let the
  user expand an edge to the files/usings that produced it (store per-edge provenance).
- **`using` ≠ real dependency.** Unused `using`s create phantom edges; aliased/`global using`s need
  care. Acceptable for a review aid; note it in the UI.
- **Layer ruleset accuracy.** The initial matrix must be validated so it flags *deltas* only — run
  it against the current HEAD graph and confirm zero "new" violations on an empty diff.
- **Scale on huge repos.** Whole-graph build cost grows with file count; the cache + Phase-4
  incremental path is the mitigation. Surface a clear "computing…" state.
- **Cross-language changes.** A change touching only non-C# files yields an empty delta in MVP;
  communicate "no analyzable module edges" rather than a blank panel.

## Deferred (not in MVP)

- Project-level (`.csproj` `ProjectReference`) granularity and the GitBench-app ↔ framework boundary.
- TypeScript / other-language extractors.
- Full-repo audit mode (all current cycles/violations, not just the delta).
- Visual node-link rendering and interactive layout.
- Externalizing `LayerRules` to a JSON/embedded config.

## Verification

- Unit tests: extractor on representative C# (file-scoped ns, `global using`, alias, `using static`);
  analyzer on hand-built before/after graphs asserting exact added/removed/new-cycle/violation sets;
  `LayerRules.Classify` on each layer pair.
- Fixture-repo test: a tiny git repo with two commits, one introducing an edge `Git → Features.X`,
  asserting the delta reports the added edge + the layer violation.
- Manual: select a commit known to add/remove a dependency; confirm the panel matches; confirm an
  empty diff yields an empty delta (no false "new" violations).
