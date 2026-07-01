# Code Graph view for reviews

A layered, interactive **node-and-edge graph of a change**. For a review increment (one commit,
`commit^ → commit`) or the combined range (`base → head`), seed a graph with the symbols the diff
touched, then draw their neighbours and colour every node/edge by the diff delta:

- **Function layer** — nodes are functions/methods. A changed function `A` is highlighted; its
  callers `B`, `C` appear with arrows pointing at `A`. A caller added by this change draws its edge
  **green**; a caller removed draws **red**; unchanged edges are grey.
- **Class layer** — nodes are types. Edges are "type `X` references type `Y`" (construction, base
  type, field/param/return type, static call). Added/removed references colour the same way.
- **Namespace layer** — the module-edge delta already specced in
  [`dependency-graph-delta.md`](dependency-graph-delta.md), reached by aggregating the finer graph.

The graph is an **ego-graph**: it renders the changed symbols plus their immediate neighbours, and
expands further hops on click — it never lays out the whole-repo graph.

## Decisions (settled)

1. **Parser = tree-sitter from the start, behind the `ISyntaxTreeProvider` seam.** Not the
   lexical/TextMate first cut, not Roslyn. tree-sitter is native (P/Invoke), so it is
   AOT-compatible where Roslyn is not, and it is inherently multi-language — matching the "V1 is C#
   but this *will* extend" requirement. This commits to the fidelity upgrade that
   [`semantic-tree-diff.md`](semantic-tree-diff.md) deferred behind the same seam.
2. **Ego-graph scoping.** Seed from the diff's changed symbols, show immediate
   callers/callees/referencers, expand on click. Focused and readable; keeps the *rendered* set
   small even though the *build* is whole-tree (see §Build & scale).
3. **Embedded panel in the review window.** A new center view-mode in `ReviewWindowRootView` that
   swaps the diff pane for the graph via a header toggle (the graph wants the full center; the West
   `ReviewStackList` rail stays). Not a pop-out window for V1.

Derived decisions:

4. **One graph, three layers by aggregation.** Build the finest (function) graph once per revision;
   the Class and Namespace layers are collapses of it (group nodes by containing type / namespace,
   union the edges). One provider pass feeds all three switch positions.
5. **Edges are name+receiver heuristics, not resolved types.** tree-sitter gives precise call sites
   and receiver *expressions*, not resolved *types*. Edge resolution is a name-based (receiver-aware)
   layer we build — an over-approximation, honest about it, matching the repo's "navigation aid,
   degrade gracefully" ethos. True resolution would need Roslyn and is out of scope.
6. **Read-only.** The graph never drives staging or any git mutation. Clicking a node jumps the diff.

## The dominant constraint: NativeAOT

Release publishes `PublishAot=true` / `IsAotCompatible=true` (`GitBench/GitBench.csproj:11`). This is
why the app shells out to `git` for diffs and chose TextMateSharp ("verified NativeAOT-clean", ships
Oniguruma per-RID). tree-sitter must hold that line. Concretely:

- **Binding via `[LibraryImport]`** (source-generated marshalling, the AOT-recommended replacement
  for `[DllImport]`), hand-rolled over tree-sitter's small, stable C API
  (`ts_parser_new`, `ts_parser_set_language`, `ts_parser_parse_string`, `ts_tree_root_node`,
  `ts_node_*`, `ts_query_new`, `ts_query_cursor_exec`, `ts_query_cursor_next_match`). A thin
  hand-written binding is AOT-clean by construction and matches this repo's write-our-own ethos
  (it ships an entire custom GUI framework). Prefer it over a general-purpose managed NuGet binding
  whose AOT-cleanliness is unverified.
- **Native assets per-RID.** Ship `libtree-sitter` + the `tree-sitter-c-sharp` grammar compiled for
  each RID the app publishes (`win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`), as runtime assets that
  flow through `vpk pack` — exactly how TextMateSharp ships Oniguruma. The grammar's generated
  `parser.c` (+ `scanner.c` where present) compiles to one shared lib per RID. This packaging is the
  single highest-risk item and is why Phase 0 exists.
- **Queries, not reflection.** Symbol/edge extraction uses tree-sitter query strings (`tags`-style
  `.scm`), embedded as resources like the Svelte grammar JSON (`GitBench.csproj:56`). No reflection.

## Architecture — three subsystems joined by one seam

The risky part (parsing) is quarantined behind `ISyntaxTreeProvider`; the model/delta and the
visualization on either side of it are parser-agnostic and low-risk.

### A · Graph model + delta — language-agnostic core

New folder `GitBench/Features/CodeGraph/`.

```csharp
internal enum SymbolKind { Function, Type, Namespace }
internal enum EdgeKind   { Calls, References, Contains }
internal enum ChangeKind { Unchanged, Added, Modified, Removed }

internal readonly record struct SymbolId(string Value);   // stable: file + qualified path + kind

internal sealed record CodeSymbol(
    SymbolId Id, SymbolKind Kind, string Name, string QualifiedName,
    string File, int StartLine, int EndLine, SymbolId? Parent);

internal sealed record CodeEdge(SymbolId From, SymbolId To, EdgeKind Kind);

internal sealed record CodeGraph(
    IReadOnlyDictionary<SymbolId, CodeSymbol> Symbols,
    IReadOnlyCollection<CodeEdge> Edges);        // one graph per revision

internal sealed record GraphDelta(
    IReadOnlyDictionary<SymbolId, ChangeKind> Nodes,   // Modified from diff line-range containment
    IReadOnlyDictionary<CodeEdge, ChangeKind> Edges);  // Added/Removed from before/after set-diff
```

- **Node status.** `Modified` falls out of interval-containment of the diff's changed line numbers
  (`DiffResult.Hunks[].Lines[].NewLineNumber`/`OldLineNumber`) against each symbol's `[StartLine,
  EndLine]` — the exact trick [`symbol-change-outline.md`](symbol-change-outline.md) specs.
  `Added`/`Removed` = present in only the after/before graph.
- **Edge status.** Set-diff the before-graph and after-graph edge sets (same strategy as
  `dependency-graph-delta.md` decision #3, at function/type granularity). An edge in *after* not
  *before* incident to a changed node is the "new caller = green line"; the reverse is red.
- Pure and synchronous → trivially unit-testable, like `HunkPatchBuilder` / `LaneAssigner`.

### B · The provider seam — tree-sitter (the swappable, risky part)

```csharp
internal interface ISyntaxTreeProvider          // reused from semantic-tree-diff.md's seam
{
    ParsedTree? Parse(string fileText, string languageId);   // null → unsupported/over-cap
}

internal interface ICodeGraphProvider           // per-language, consumes ParsedTree
{
    // Definitions + intra-file call/reference sites for one file; cross-file edge
    // resolution happens in the builder, which has the whole-tree symbol index.
    FileSymbols Extract(ParsedTree tree, string path);
}
```

- `TreeSitterSyntaxTreeProvider` wraps the P/Invoke binding + grammar registry (mirrors
  `LanguageRegistry` extension→id + `SyntaxHighlighter`'s grammar cache; own size caps/budgets).
- `CSharpCodeGraphProvider` runs `tags`-style queries: definitions
  (`method_declaration`, `class_declaration`, `interface_declaration`, `namespace_declaration`, …)
  → `CodeSymbol`s with ranges + containment; call sites (`invocation_expression`) and type
  references → unresolved `(callerSymbol, calleeName, receiverText?)` records.
- **`CodeGraphBuilder`** (headless, in the Git layer alongside `GetFileText`): enumerate source blobs
  at a revision via the in-process LibGit2Sharp tree walk (`new Repository(repo.Path)` +
  `lg.Lookup<Commit>(sha).Tree`, the pattern at `GitService.cs:94`), extract per file, then **resolve
  edges** against the whole-tree symbol index (name + receiver heuristic → `Calls`/`References`
  edges). Surface behind `IGitService.BuildCodeGraph(repo, scope)` so all git/IO stays in the git
  layer, consistent with `GetFileText`.

The resolver — not the parsing — is the genuinely novel part; keep it a pure function over
`(FileSymbols[], symbolIndex)` so it is unit-testable and swappable when a real type-resolver lands.

### C · Visualization — layout, canvas, interaction (well supported today)

The exploration confirmed `ICanvas` already has every primitive needed and there are two in-repo
precedents, so this is mostly composition.

- **`GraphLayout`** (pure) — a layered (Sugiyama-style) assignment centred on the seeded node:
  callers rank above, callees below, arrows flow one direction. `LaneAssigner` is the in-repo
  precedent for DAG layout; positions are unit-testable (no-overlap / rank invariants).
- **`CodeGraphCanvasView : View`** — overrides `OnDrawSelf(ICanvas)`. Camera = `PushTranslation(-camX,
  -camY)` + `PushScale(zoom, …)` (draw-only, never touches layout — `ICanvas.cs:28,35`). Nodes:
  `DrawRect` (radius/shadow/border) + `DrawText`, tinted by `ChangeKind`. Edges: `DrawCubicBezier`
  (has gradient + dashing) coloured by edge `ChangeKind` — green added, red removed, grey unchanged.
  `CommitGraphRenderer` is the working precedent for painting connected DAG geometry with these
  primitives; `framework/NodeGraphApp/` (`BezierUtils.IsPointOverBezier`, `Camera`/`Viewport`
  screen↔world, `MousePicker`) is the algorithmic reference for camera + hit-testing.
- **`CodeGraphView : Widget`** + **`CodeGraphViewModel : ViewModelBase<CodeGraphState>`** — resolves
  the VM from `Context`, `UseViewModel`, one `KeyboardMouseController` owning pan/zoom (wheel + drag
  via `StealFocus` pointer capture) and node/edge picking (world-space geometry tests, since the
  input system only hit-tests rectangles). Render state `Placeholder | Loading | Loaded(GraphDelta,
  layout)` mirroring `CommitDetailsRenderState`.
- **Layer switch** — a `ReviewModeToggle`-style control cycling Function / Class / Namespace, which
  re-aggregates the built function graph (no re-parse).

## Embedding in the review window

`ReviewWindowRootView.Split()` (`Features/Review/ReviewWindowRootView.cs:84`) is a `BorderLayout`
with the `ReviewStackList` rail West and a `CommitDetailsHost` Center. Add a **center view-mode**
(`ReviewCenterView { Diff, Graph }`) on `ReviewWindowViewModel`, and wrap the Center in a
`Switch<ReviewCenterView>` swapping `CommitDetailsHost` for `new CodeGraphView()`. Drive it from a new
toggle in `ReviewHeaderBar` (mirrors the existing `ReviewModeToggle` for ByIncrement/Combined) and a
`ReviewKeyController` key (e.g. `g`). The graph reuses the window's scope: seed from the increment's
`CommitDetails.Files` (or the range's `LoadRangeFiles`) and the same base/head SHAs the diff uses.

## Build & scale

Finding "who calls `A`" requires scanning the whole tree at **both** base and head (callers live in
untouched files), so the *build* is whole-tree even though the *render* set is the ego-graph.

- Build off the UI thread via `RunBackground` with generation guards, exactly like
  `DiffViewModel.StartLoad` / `RepoSnapshotStore.LoadSlice`; drop stale results.
- **Cache per tree-SHA** (immutable) so re-selecting an increment is instant; a changed increment
  re-extracts only `CommitDetails.Files` against a cached HEAD graph (incremental, a later phase).
- Reuse the highlighter's guardrails: skip over-cap blobs (`SyntaxHighlighter.MaxFileChars`), cap
  total parse budget, and degrade to "no symbols/edges from this file" — never throw, never block.
- Surface a clear "building graph…" state; on huge repos the first build is the cost to watch.

## Phasing

- **Phase 0 — tree-sitter native spike (de-risk the packaging).** Hand-rolled `[LibraryImport]`
  binding; bundle `libtree-sitter` + `tree-sitter-c-sharp` per-RID through the publish; parse a C#
  string, walk the tree, run one `tags`-style query, get symbol names back — **verified under
  `dotnet publish -c Release` (AOT), not just Debug**, and through `vpk pack`. No UI. This answers
  "does tree-sitter work here at all" before anything is built on it.
- **Phase 1 — model + C# symbols + node render.** `CodeGraph`/`GraphDelta`; `CSharpCodeGraphProvider`
  definitions only (no edges); `GraphDelta` node status from diff line ranges; `GraphLayout` +
  `CodeGraphCanvasView` rendering changed symbols as nodes coloured by status; embedded in the review
  window behind the toggle. Ego-graph seeded from the changed set.
- **Phase 2 — function-layer call edges.** Call-site extraction + name/receiver resolver;
  whole-tree build at base+head (cached per tree-SHA); edge delta (green/red/grey); expand-on-click.
- **Phase 3 — class + namespace layers.** Type-reference edges; aggregation to Class and Namespace;
  the layer switch. (Namespace layer can reuse `dependency-graph-delta.md`'s extractor.)
- **Phase 4 — polish + second language.** Pan/zoom refinement, hover, click node → `ScrollToNewLine`
  in the diff, node/edge tooltips; add a second grammar (e.g. `tree-sitter-typescript`) to prove the
  multi-language seam; incremental cached builds.

## Files to create / modify

Create (under `GitBench/Features/CodeGraph/` unless noted):
- `CodeGraphModel.cs` (records), `GraphDeltaBuilder.cs` (pure), `CodeGraphLayout.cs` (pure),
  `CodeGraphCanvasView.cs`, `CodeGraphView.cs`, `CodeGraphViewModel.cs`, `CodeGraphController.cs`
- `ISyntaxTreeProvider.cs` + `TreeSitterSyntaxTreeProvider.cs` + `TreeSitter` P/Invoke binding
  (`Interop/TreeSitterNative.cs`), `ICodeGraphProvider.cs` + `CSharpCodeGraphProvider.cs`,
  `EdgeResolver.cs` (pure)
- `Git/CodeGraphBuilder.cs` (or fold into `GitService`) + `BuildCodeGraph` on `Git/IGitService.cs`
- `Assets/Queries/csharp-tags.scm` (embedded resource); per-RID native grammar assets
- Tests: `GitBench.Tests/{CSharpCodeGraphProviderTests,EdgeResolverTests,GraphDeltaBuilderTests,
  CodeGraphLayoutTests}.cs`; harness view test `CodeGraphViewTests.cs`

Modify:
- `Features/Review/ReviewWindowRootView.cs` (center `Switch`), `ReviewWindowViewModel.cs`
  (`ReviewCenterView` mode), `ReviewHeaderBar.cs` (toggle), `ReviewKeyController.cs` (key)
- `Git/IGitService.cs` + `Git/GitService.cs` (BuildCodeGraph)
- `GitBench.csproj` (per-RID native assets, embedded query resources) + release packaging
- `GitBench.Tests/GitBench.Tests.csproj` (add `ZGF.Gui.Testing` ProjectReference for view tests)
- Localization catalogs (all 6 `Strings/*.json`)

## Validation

- **Pure-logic xUnit** (`GitBench.Tests`, `InternalsVisibleTo` already set): provider extraction on
  real C# blobs → expected symbols/ranges; `EdgeResolver` on hand-built `FileSymbols` → expected
  edges (incl. the over-approximation cases); `GraphDeltaBuilder` on synthetic before/after graphs →
  exact node/edge `ChangeKind`; `CodeGraphLayout` no-overlap/rank invariants. Fixture-repo test
  (model on `ReviewStackTests`, real temp git repo): a commit that adds a caller → delta reports the
  green edge.
- **Harness view tests** (`GuiTestHarness` + `RecordingCanvas`): assert nodes/edges are actually
  drawn (the canvas captures `Rects`/`Texts`/`Beziers`), coloured by status, and that a click hits
  the right node. Requires the `ZGF.Gui.Testing` ProjectReference above.
- **Live**: run the "GitBench (MCP)" launch profile (`ZGF_GUI_MCP=1`) and drive with
  `gui_snapshot` / `gui_click` / `gui_screenshot`; give graph widgets stable `Id`s and
  `.WithRole(...)` so both the harness and MCP can address them.
- Every edit compile-checked with `dotnet build GitBench/GitBench.csproj --artifacts-path <scratch>`
  (isolated outputs). The user owns running/testing the app.

## Risks & open questions

- **tree-sitter under AOT + Velopack packaging is the top risk** — Phase 0 exists solely to retire it
  before investing in the feature. If per-RID native packaging proves intractable, the fallback is
  the lexical/TextMate provider behind the same `ISyntaxTreeProvider` seam (no model/view rework).
- **Edge over-approximation.** Name+receiver resolution mis-attributes overloads, same-named methods
  on different types, interface/virtual dispatch, extension methods. Acceptable for a review aid;
  show a subset-of-callers caveat and let node click jump to the code to confirm.
- **Whole-tree build cost** on large repos — mitigated by per-tree-SHA cache + Phase-4 incremental
  build + a clear building state.
- **Embedding vs. space** — the review window is already split; swapping the whole center for the
  graph (vs. a cramped side panel) is the recommended default. Confirm during Phase 1.
- **Rename churn** — a renamed symbol reads as Removed+Added; acceptable for V1.
- **Cross-language changes** yield an empty/partial graph in V1 (C#-only) — communicate "no
  analyzable symbols" rather than a blank canvas.

## Deferred

- Pop-out window surface; whole-change (non-ego) layout mode.
- True type resolution (Roslyn or a hand-built resolver); precise overload/inheritance edges.
- Additional languages beyond the Phase-4 proof; TS/JS resolver rules.
- Incremental/cached cross-revision builds beyond per-tree-SHA memoization.
- Persisted graph layout / node pinning.
