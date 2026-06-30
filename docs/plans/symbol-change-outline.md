# Plan: symbol-level change outline (diff-improvements #2)

Goal: alongside the diff hunks, show a **tree of what changed structurally** — added /
modified / removed namespaces, classes, methods, functions — where each node is clickable
and scrolls the diff body to that symbol. This is item #2 in `docs/diff-improvements.md`.

The outcome is a sidebar like an IDE's "Outline" view, but filtered to and annotated by the
diff: a node says not just "method `Foo`" but "method `Foo` — modified" / "class `Bar` —
added", and clicking it jumps the diff to that symbol.

## Why this is tractable here

Three pieces already exist; the feature is mostly wiring them together plus one new pure
extraction pass.

- **The diff model already has everything needed to map symbols → changes.**
  `Git/DiffResult.cs` gives `DiffResult.Hunks` → `DiffHunk(OldStart, OldLines, NewStart,
  NewLines, Header, Lines)` → `DiffLine(Kind {Context,Added,Removed}, OldLineNumber,
  NewLineNumber, Text)`. Every changed line carries a precise 1-based old/new line number, so
  "which symbol does this change fall in" is interval containment.
- **A whole-file tokenizer with scope information already runs for the diff.**
  `Features/Diff/SyntaxHighlighter.cs` tokenizes each file side via TextMateSharp and has the
  raw scope list per token (`t.Scopes`, `t.StartIndex`, `t.EndIndex` in `Tokenize`,
  SyntaxHighlighter.cs:75). It currently reduces each token to a color slot and throws the
  scope away (`SlotFor`, line 113). Symbol extraction is the same tokenize pass, keeping the
  scopes instead of discarding them.
- **`DiffHunk.Header` is git's own enclosing-symbol string, already parsed.**
  `GitService.TryParseHunkHeader` (GitService.cs:2828) captures git's xfuncname section
  heading (the text after the second `@@`) and stores it on every hunk; it is already rendered
  beside each hunk separator (`DiffContentView.DrawHunkSeparatorRow`). This is a zero-parse,
  language-agnostic fallback outline.
- **The whole-file text for either side is already fetchable**, exactly as highlighting fetches
  it: `IGitService.GetFileText(repo, path, side, oldSide, commitSha)` (IGitService.cs:60, impl
  GitService.cs:2561). `DiffHighlightCoordinator` (DiffHighlightCoordinator.cs) shows the
  pattern: detect language, fetch only the needed side(s), tokenize.
- **The UI has a complete reusable tree stack.** `Controls/TreeRow.cs`, `Controls/TreeGuides.cs`,
  `Controls/TreeSelectionBar.cs`, `Features/LocalChanges/TreeMetrics.cs`, plus the
  `Branches` feature as a worked example (`BranchRow.cs`, `BranchTreeBuilder.cs`,
  `BranchListRow.cs`, `BranchRowController.cs`). An outline tree is the same pattern: a closed
  `record` node set, a pure builder that flattens to a row list, an `Each<>` template, a row
  controller that routes clicks.
- **The jump primitive already exists.** `DiffContentView.ScrollToNewLine(int lineNumber, int
  leadIn)` (DiffContentView.cs:414) scrolls the body so a given new-file line sits `leadIn`
  rows below the top, falling back to the closest preceding numbered row. An outline node
  carrying its declaration line just calls this.

What is genuinely new: a `SymbolExtractor` that turns a tokenized file into a symbol tree with
line ranges, and a `DiffOutlineBuilder` that diffs the old/new symbol trees and tags each node
added/modified/removed.

## The honest hard part: TextMate gives names, not structure

TextMate grammars classify *tokens* (this run of characters is `entity.name.function.cs`).
They do **not** give an AST: no symbol end-line, no nesting/containment, no "this method
belongs to this class." `ScopeColorMap.cs` already proves the naming signal is present — it has
rules for `entity.name.function` → Function, `entity.name.type` → Type,
`entity.name.namespace` → Type, `storage` → Keyword — but those are flat color hints.

So we recover structure with a **brace-depth pass** layered on the token stream. This is a
heuristic, not a parser, and the plan treats it as such:

- All currently-registered languages are brace- or markup-delimited (`LanguageRegistry.cs`:
  C#, TS/TSX, JSON, XML/csproj, HTML, CSS/SCSS/LESS, Svelte). For these, a symbol's body runs
  from the `{` after its declaration to the matching `}`; nesting falls out of brace depth.
- Crucially, **the tokenizer already labels strings and comments**, so we ignore braces inside
  any token whose scope contains `string` or `comment` — the classic "brace in a string"
  failure mode is handled for free by the data we already have.
- A definition is recognized when a token's scope matches a small definition-scope table
  (e.g. `entity.name.function.*`, `entity.name.type.class`, `entity.name.type.interface`,
  `entity.name.type.enum`, `entity.name.namespace`, plus a `meta.*.declaration` assist). The
  token's text (sliced from the tab-expanded line by `StartIndex..EndIndex`) is the symbol name;
  its line is the declaration line; its kind comes from the matched scope.

This yields a good-enough tree for C# and TS/TSX (the primary targets). It will occasionally
miss exotic constructs or mis-nest a brace-light language. That is acceptable because:

1. There is always a **`DiffHunk.Header` fallback** (a flat, language-agnostic list).
2. The outline is a *navigation aid*, not a correctness surface — a wrong node misroutes a
   scroll, it does not corrupt anything.

Non-goal: a true semantic AST. That is diff-improvements #3 (tree-based diff) and would need
Roslyn/tree-sitter, which the repo deliberately avoids (NativeAOT: `GitBench.csproj` sets
`PublishAot=true`/`IsAotCompatible=true`; TextMateSharp was chosen partly because it is
"verified NativeAOT-clean"). Reusing TextMateSharp keeps us inside an already-vetted dependency
and adds zero packages.

## New data model

New file `Features/Diff/DiffOutline.cs` (records, namespace `GitBench.Features.Diff`):

```csharp
internal enum SymbolKind { Namespace, Type, Interface, Enum, Method, Function, Property, Field, Other }
internal enum SymbolChange { Unchanged, Added, Modified, Removed }

// One node of the extracted structure, in a single file side's line space.
internal sealed record SymbolNode(
    SymbolKind Kind,
    string Name,
    int StartLine,            // declaration line (1-based, that side)
    int EndLine,              // matched close brace, or StartLine when unknown
    IReadOnlyList<SymbolNode> Children);

// The diff-annotated outline the UI consumes. Lines are new-side where present
// (Modified/Added/Unchanged); Removed nodes carry their old-side declaration line.
internal sealed record OutlineEntry(
    SymbolKind Kind,
    string Name,
    SymbolChange Change,
    int? NewLine,             // scroll target for ScrollToNewLine; null for pure Removed
    int? OldLine,
    IReadOnlyList<OutlineEntry> Children);

internal sealed record DiffOutline(IReadOnlyList<OutlineEntry> Roots)
{
    public bool IsEmpty => Roots.Count == 0;
}
```

## The extraction + classification pipeline

Mirror `DiffHighlightCoordinator` exactly — same shape, same threading contract.

1. **`SymbolExtractor`** (new, `Features/Diff/SymbolExtractor.cs`). Input: full file text +
   language id. Output: `IReadOnlyList<SymbolNode>` for that side. It reuses the existing
   TextMateSharp tokenize. To avoid a second registry/grammar cache, factor the per-line
   tokenize loop out of `SyntaxHighlighter.Tokenize` so both the color-span builder and the
   symbol scanner consume the same `IToken` stream. Two equally valid factorings:
   - **Combined (preferred, no extra tokenize):** give `SyntaxHighlighter` a method that returns
     both the color spans *and* the definition hits from one pass — highlight and outline already
     run on the same file in the same async lane, so one tokenize serves both.
   - **Standalone (simplest first cut):** `SymbolExtractor` holds its own `SyntaxHighlighter`-like
     tokenizer and re-tokenizes. Costs a second pass (bounded by the existing 750ms whole-file
     budget); fine for an MVP, wasteful long-term.

   The scanner walks tokens in order, maintaining a brace-depth stack (skipping `{`/`}` inside
   `string`/`comment`-scoped tokens), and on a definition-scope token pushes a `SymbolNode` whose
   `EndLine` is set when brace depth returns to the declaration's level. Nesting = stack parentage.

2. **`DiffOutlineBuilder`** (new, `Features/Diff/DiffOutlineBuilder.cs`, pure + unit-tested).
   Input: `DiffResult` + new-side `SymbolNode` tree + old-side `SymbolNode` tree. Output:
   `DiffOutline`.
   - Match symbols across sides by `(Kind, qualified name)` where qualified name = the
     `.`-joined ancestor path (`Foo.Bar.Baz`). Present-new-only → `Added`; present-old-only →
     `Removed`; present-both → candidate `Modified`/`Unchanged`.
   - Collect changed line numbers from the diff: `DiffLine.NewLineNumber` for `Added`,
     `OldLineNumber` for `Removed` (walk `diff.Hunks[].Lines`). For each candidate, mark
     `Modified` if any changed new-line falls in its new-side `[StartLine,EndLine]` (or any
     changed old-line in its old-side range); propagate `Modified` up to ancestors.
   - **Filtering:** default to changed-only — prune `Unchanged` leaves but keep `Unchanged`
     ancestors that contain a changed descendant (so the path to a change is visible). A
     "show all symbols" toggle can relax this later.
   - **Fallback:** when no language is detected (`LanguageRegistry.DetectLanguageId` returns
     null) or extraction yields nothing, synthesize a flat outline from distinct
     non-empty `DiffHunk.Header` strings, each marked `Modified` and scrolling to the hunk's
     `NewStart`. Guarantees a usable outline for every text diff.

3. **`DiffOutlineCoordinator`** (new, `Features/Diff/DiffOutlineCoordinator.cs`). Same body as
   `DiffHighlightCoordinator.Compute`: bail on binary/error/empty; detect language; fetch the
   needed side(s) via `GetFileText` (reuse `NeededSides`); extract; classify; return
   `DiffOutline?`. Returns null → no outline panel content (render the fallback or nothing).

## View-model integration

Follow the highlight precedent in `DiffViewModel.cs` precisely — it already solves the
stale-result and carry-forward problems.

- **Attach to render state.** Add `DiffOutline? Outline = null` to
  `DiffRenderState.Loaded` (DiffViewModel.cs:24), next to `Highlight`. The outline panel reads
  it via a slice; the diff body ignores it.
- **Compute on a guarded lane.** Add `_outlineLane = CreateLane()` and a `StartOutline(repo,
  diff, commitSha)` that copies `StartHighlight` (DiffViewModel.cs:505) verbatim: run
  `DiffOutlineCoordinator.Compute` under `RunBackground`, and in `onResult` re-attach only when
  `ReferenceEquals(cur.Result, diff)` so a navigated-away file's outline is dropped. Kick it off
  from the same place `StartHighlight` is invoked in `StartLoad`'s `onResult`
  (DiffViewModel.cs:453). Bump `_outlineLane` alongside `_highlightLane` in `StartLoad`
  (line 393). Extend `CarryHighlightForward` (line 358) to also carry the prior `Outline`
  forward so an optimistic hunk apply doesn't blank the panel.
  - Combined-pass optimization: if `SyntaxHighlighter` returns spans+definitions together, fold
    `StartOutline` into `StartHighlight` so one background pass produces both
    `DiffRenderState.Loaded(diff, highlight, outline)`.
- **Scroll channel.** Add a scroll-request observable on the VM so the outline panel and the
  diff body stay decoupled (both resolve the same `DiffViewModel` from context). Concretely a
  `State<int>` bumped by `RequestScrollToNewLine(int newLine)`, or a small one-shot event. The
  outline row's click handler calls `vm.RequestScrollToNewLine(entry.NewLine!.Value)`. The
  `DiffContentView` — which already does `content.Bind(vm.RenderState, …)` in `DiffView.Build`
  (DiffView.cs:40) — adds a second `content.Bind(vm.ScrollRequest, line =>
  content.ScrollToNewLine(line, leadIn: 3))`. No new reference between the two views; the VM is
  the seam. (`Removed` nodes have no `NewLine`; scroll them to the hunk that deleted them via
  the new-side line just before the removal.)

## UI: the outline panel

Build it from the existing tree stack, exactly as `BranchListRow` does.

- **Node → row record** `OutlineRowItem` flattened from `DiffOutline` by a pure
  `OutlineTreeBuilder.BuildRows(outline, collapsedKeys)` (copy `BranchTreeBuilder.cs` /
  `Features/LocalChanges/FileTree.cs`): computes `Depth`, the `TreeGuides` mask, the chevron
  for collapsible nodes, and a stable selection key. Collapse state lives in the panel's
  `Widget<TState>` (see `BranchRowState.cs`).
- **Row visual** `OutlineRow : Widget<…>` composes `Controls/TreeRow.cs` with:
  - a kind glyph (namespace/type/method/property — reuse the diff's existing glyph conventions),
  - the symbol name,
  - a trailing change badge colored by `SymbolChange` (Added = add-green, Removed = delete-red,
    Modified = the diff's modified/amber accent), pulling colors from the theme like the diff
    body does. Use `TreeMetrics.cs` for column widths so it shares the app's tree rhythm.
- **Interaction** `OutlineRowController : KeyboardMouseController` (copy `BranchRowController.cs`):
  left-click → `RequestScrollToNewLine`; chevron click → toggle collapse; arrow-key nav via
  `Controls/ListNavigation.cs` (`ListArrowKbmController`) as Local Changes already does.
- **Container** an `Each<OutlineRowItem>` inside a `ScrollArea`
  (`ZGF.Gui.Desktop/Components/Controls/ScrollArea.cs`), optionally wrapped in
  `TreeSelectionOverlay<TKey>` for the sliding selection bar.

### Where it sits

`DiffView` (DiffView.cs) is intentionally headerless and embedded in three contexts
(Local Changes via `LocalChangesContentView.cs:168`, Commit Details via
`CommitDetailsView.cs:115`, and the pop-out window via `DiffWindowRootView.cs`), all through
`new Provide<DiffViewModel> { Value = vm.DiffVm, Child = new DiffView() }`. So adding the panel
inside `DiffView`'s `BorderLayout` (DiffView.cs:46) as a new `West` makes it available
everywhere from one change:

```csharp
var diffBody = new BorderLayout
{
    West   = new OutlinePanel { Visible = vm.OutlineVisible },   // new, collapsible
    Center = new Raw { View = content },
    East   = new Raw { View = vScrollBar },
    South  = new Raw { View = hScrollBar },
};
```

Because the embedded panes are space-constrained (the diff is the bottom ~1/3 of Local Changes
via `VerticalSplitContainer`), default the panel **collapsed** in embedded panes and **expanded
in the pop-out window**, where `DiffWindowToolbar.cs` already has room for a toggle button.
Recommended sequencing: ship and prove it in the pop-out window first (Phase 3), then expose
the toggle in `DiffPaneHeaderWidget.cs` for the embedded panes.

## Phasing

- **Phase 0 — fallback-only outline (thin vertical slice, no extraction).** `DiffOutline` model;
  `DiffOutlineBuilder` fallback path only (distinct `DiffHunk.Header` → flat `Modified` list);
  VM `Outline` slice + scroll channel; `OutlinePanel` UI in the pop-out window. Proves the
  end-to-end flow (model → VM lane → tree → click → scroll) with zero parsing risk.
- **Phase 1 — C# symbol extraction.** `SymbolExtractor` (definition-scope table + brace-depth)
  for `source.cs`; `DiffOutlineBuilder` old/new matching + change classification + changed-only
  filtering. Unit tests on real C# blobs.
- **Phase 2 — TS/TSX + the rest.** Extend the definition-scope table and validate brace-depth on
  `typescript`/`typescriptreact`, then CSS/SCSS/LESS/JSON. Markup (HTML/XML/Svelte) can stay on
  the header fallback initially.
- **Phase 3 — embedded panes + polish.** Toggle in `DiffPaneHeaderWidget`/`DiffWindowToolbar`;
  collapse-state persistence; "next/prev change" keyboard nav across outline entries;
  optional "show all symbols" (un-filter) toggle.

## Risks & mitigations

- **Brace-depth mis-nesting / missed symbols.** Bounded by the header fallback and the
  navigation-only role; covered by unit tests on representative blobs. Skip braces inside
  `string`/`comment` tokens (data already present).
- **Cost.** Extraction adds at most a second tokenize per side; reuse the combined single-pass
  option and the existing `MaxFileChars`/`WholeFileBudget` guards in `SyntaxHighlighter.cs`.
  Runs off the UI thread on a guarded lane, same as highlighting.
- **Rename matching.** Cross-side name matching mis-flags a renamed symbol as Removed+Added.
  Acceptable for MVP; `DiffResult.OldPath` already handles file renames for the old-side fetch.
- **Line-number drift after optimistic hunk apply.** `CarryHighlightForward` already re-bases
  rendering after a hunk drop; carry the outline the same way and let the background pass refresh
  it, identical to highlight.
- **AOT.** No new package; TextMateSharp only. Keep all extraction code reflection-free.

## Testing

- `SymbolExtractor` unit tests (xunit, `GitBench.Tests`, `InternalsVisibleTo` already set):
  C#/TS blobs → expected `SymbolNode` trees (nesting, ranges, strings-with-braces, enums).
- `DiffOutlineBuilder` unit tests: synthetic `DiffResult` + symbol trees → expected
  `SymbolChange` tagging (added/removed/modified, ancestor propagation, changed-only filtering,
  header fallback).
- Manual: drive the running window via the GUI debug server (`ZGF_GUI_DEBUG`) — open a diff,
  confirm the tree matches the hunks and clicking nodes scrolls the body.

## Key code references

- Diff model: `GitBench/Git/DiffResult.cs` (`DiffResult`, `DiffHunk.Header`, `DiffLine`).
- Hunk header parse: `GitBench/Git/GitService.cs:2828` (`TryParseHunkHeader`); file text:
  `GitService.cs:2561` / `IGitService.cs:60` (`GetFileText`).
- Tokenizer to reuse: `GitBench/Features/Diff/SyntaxHighlighter.cs:75` (`Tokenize`, has
  `t.Scopes`/`t.StartIndex`/`t.EndIndex`); language map:
  `GitBench/Features/Diff/LanguageRegistry.cs`; scope→kind hints:
  `GitBench/Theming/ScopeColorMap.cs`.
- Async lane precedent: `GitBench/Features/Diff/DiffHighlightCoordinator.cs` and
  `DiffViewModel.cs` (`DiffRenderState.Loaded`:24, `StartHighlight`:505, `_highlightLane`,
  `CarryHighlightForward`:358, `StartLoad`:389).
- Scroll primitive: `GitBench/Features/Diff/DiffContentView.cs:414` (`ScrollToNewLine`);
  binding site: `GitBench/Features/Diff/DiffView.cs:40`.
- Tree UI to copy: `GitBench/Controls/TreeRow.cs`, `TreeGuides.cs`, `TreeSelectionBar.cs`,
  `Features/LocalChanges/TreeMetrics.cs`; worked example
  `Features/Branches/{BranchRow,BranchTreeBuilder,BranchListRow,BranchRowController}.cs`.
- Composition / embedding: `GitBench/Features/LocalChanges/LocalChangesContentView.cs:168`,
  `Features/Commits/CommitDetailsView.cs:115`, `Features/Diff/DiffWindowRootView.cs`.
