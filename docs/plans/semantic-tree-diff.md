# Semantic (Tree-Based) Diff for the Diff View

> Item **#3** from `docs/diff-improvements.md`: *diff the syntax tree instead of
> lines, so renames, rewraps, and reindents stop reading as noise.* This is the
> largest of the five backlog items — it replaces (for one view mode) git's
> line-based diff with our own structural diff over a parsed tree of both file
> sides. The plan delivers it in phases, each of which builds and ships, and
> defers the highest-fidelity pieces behind a clean seam.

## What "noise" means today

The diff body is fed a **line-based unified diff produced by git itself**
(`GitService.GetDiff`, `GitService.cs:2503` — it shells out to `git diff` /
`git show` and parses the patch into `DiffResult → DiffHunk → DiffLine`). Line
diffing has no idea what the bytes *mean*, so three common edits read as large
deletions-plus-additions even though nothing semantically moved:

- **Reindent / re-brace** — wrap a block in an `if`, change tab width, reformat:
  every shifted line shows as removed + added.
- **Rewrap** — split one long call across lines (or join several): the whole run
  churns.
- **Rename / move** — pull a method up, rename a symbol: the old site reads as a
  pure delete and the new site as an unrelated add.

A structural diff parses both sides, matches *nodes* (which ignore whitespace and
position), and only the genuinely-changed nodes are highlighted. Reindents become
context; a rename highlights just the identifier.

## Current architecture (the seam we plug into)

The existing pipeline already does most of the plumbing a structural diff needs —
fetching both file sides and tokenizing them. The structural work slots in
alongside the syntax-highlight pass, which is the closest precedent.

| Stage | Where | Note |
|-------|-------|------|
| Compute diff | `GitService.GetDiff` (`GitService.cs:2503`) | Line-based, from git. **The thing #3 replaces in its mode.** |
| Fetch raw side text | `GitService.GetFileText(repo, path, side, oldSide, commitSha)` (`GitService.cs:2561`) | Returns full before/after text. **Reused as the structural diff's input.** |
| Tokenize | `SyntaxHighlighter.Tokenize` (`SyntaxHighlighter.cs:75`) | TextMateSharp regex tokens → per-line `TokenSpan`. Threads `IStateStack` across lines. **Reused as the structural tree's input.** |
| Orchestrate highlight | `DiffHighlightCoordinator.Compute` (`DiffHighlightCoordinator.cs:20`) | Already fetches + tokenizes *both* sides. **Sibling to the new structural coordinator.** |
| Language detect | `LanguageRegistry.DetectLanguageId` (`LanguageRegistry.cs:33`) | Extension → TextMate id. **Reused.** |
| View-model state | `DiffViewModel` (`DiffViewModel.cs`) | `DiffRenderState` variants + `DiffViewMode { Diff, FullFile }`, sticky per-pane mode, background generation-guarded lanes (`StartLoad` `:389`, `StartHighlight` `:505`). **We add a `Structural` mode + render state, mirroring `FullFile`.** |
| Flatten + draw rows | `DiffContentView` (`DiffContentView.cs`) | `FlattenRows` `:275` / `FlattenFullFile` `:344` → `List<DiffRow>`; `DrawLineText` `:878` / `DrawTextRun` `:909` already draw **per-character colored runs**. **The renderer can already paint sub-line emphasis — we just feed it a new span channel.** |
| Toggle affordance | `DiffWindowToolbar.BuildFullFileToggleButton` (`:102`), `DiffPaneHeaderWidget`, `F` key via `ListArrowKbmController` | **We add a parallel toggle, same pattern.** |
| Theme | `DiffContentStyles` / `DiffSyntaxStyles` (`ThemeStyles.Diff.cs:29/49`) | **We add added/removed emphasis colors.** |

**Hard constraint — NativeAOT.** Release publishes with `PublishAot=true` /
`IsAotCompatible` (`GitBench.csproj`). TextMateSharp is explicitly "verified
NativeAOT-clean" and ships its Oniguruma native per-RID. Anything we add must
hold that line. This is the single biggest force on the design below.

## The core problem

A structural diff cannot consume git's line diff — it needs the **two full file
texts parsed into trees**, then a tree-diff. We already have the texts
(`GetFileText`) and a tokenizer (`SyntaxHighlighter`). We do **not** have a parse
tree. So the three things to build are: a **tree provider**, a **tree-diff
algorithm**, and a **render mapping** back onto the existing row renderer.

## Design decisions

### 1. Tree provider — start with a token-nesting tree, not a new parser

We do **not** add tree-sitter or Roslyn for the first cut. Instead build a
**generic delimiter-nesting tree directly from the TextMateSharp token stream we
already produce**. This is exactly the universal "text" parser difftastic falls
back to for grammars it lacks, and it is surprisingly effective for the
brace-heavy languages this app actually highlights (C#, TS/TSX, JSON, CSS/SCSS/
LESS, XML).

- **Atoms** = non-bracket tokens (identifiers, keywords, operators, numbers).
  Strings and comments are single atoms (the state-stack threading in `Tokenize`
  already keeps a multi-line string/comment coherent).
- **Lists** = runs delimited by matched `(`/`)`, `[`/`]`, `{`/`}` (detected from
  the literal characters at the token's offsets), nested to form the tree.
- **Whitespace / indentation tokens are dropped from the tree** — that is what
  makes reindents and rewraps vanish as noise.

Why this and not the "real" options:

- **vs. tree-sitter (Tier B, later):** tree-sitter is the substrate every real
  structural-diff tool uses and gives true grammar-accurate nodes. But it means
  bundling a native runtime **plus a native grammar per language per RID**, an
  AOT/packaging effort on the order of how TextMateSharp ships Oniguruma. Defer
  it behind the `ITreeProvider` seam (below) as a fidelity upgrade, not a
  prerequisite.
- **vs. Roslyn:** highest fidelity for C# (the dominant language here) but
  **AOT-hostile** (heavy reflection, ~10 MB+) and C#-only — it would undo the
  AOT cleanliness the project deliberately protects. Rejected.

The token-nesting tree needs the **full** token stream (text + bracket/string/
comment class), which `BuildSpans` (`SyntaxHighlighter.cs:94`) currently discards
(it drops `Default`-slot tokens). So we add a sibling extraction on
`SyntaxHighlighter` that returns every token with its source `(line, startCol,
endCol)` and a coarse class derived from `ScopeColorMap` + the literal chars.
No new dependency; AOT-clean by construction; reuses the grammar cache, the
language registry, and the per-line state threading already in place.

### 2. Diff algorithm — anchored recursive match, Dijkstra later

- **First cut — anchored recursive diff.** Content-hash every subtree; match
  identical subtrees as anchors (a content hash ignores position *and*
  whitespace, which is precisely why moved/renamed/reindented blocks match);
  within unmatched gaps recurse, falling back to a Myers/patience LCS over the
  atom sequence. A few hundred lines, deterministic, AOT-clean, and it delivers
  the headline wins (reindent → context, rename → identifier-only).
- **Stretch — Dijkstra/A\* over the (lhs-pos, rhs-pos) state graph** (difftastic's
  actual algorithm). Higher fidelity on interleaved edits, much heavier. Keep the
  diff algorithm behind an interface so it can be swapped without touching the
  provider or the renderer.

### 3. Render integration — new mode + state, reuse the per-char run renderer

Mirror the full-file toggle exactly (`docs/plans/full-file-view-toggle.md`):

- Add `DiffViewMode.Structural` (sticky per-pane, `DiffViewModel.cs:17`) and a
  `DiffRenderState.Structural` variant (`DiffViewModel.cs:19`).
- The structural result is mapped back to **changed character ranges per source
  line** on each side. A line whose atoms all matched is **context** (even if its
  whitespace changed — reindent reads calm); a line with added/removed atoms is a
  change, with only those atom ranges emphasized.
- The renderer **already** paints per-character runs (`DrawLineText` `:878`,
  `DrawTextRun` `:909`) from `TokenSpan`s. We feed a **parallel emphasis-span
  channel** (changed ranges) on top of the existing syntax spans, so unchanged
  tokens on a changed line stay calm and the changed nodes pop. First deliverable
  keeps the line-based row layout (a `FlattenStructural` branch in
  `SetRenderState`, `DiffContentView.cs:187`); a difftastic-style side-by-side
  node-aligned layout is a later phase.

### 4. Read-only mode — no staging from a structural diff

Structural mode is a **view only**. Staging/discarding still flows through git's
hunk patches (`HunkPatchBuilder`); we never feed a structural diff to
`git apply`. So `FlattenStructural` sets `_hunksPatchable=false` (no Stage/
Discard buttons), exactly as `FlattenFullFile` does. This removes the single
largest correctness risk.

### 5. Silent fallback to the line diff

The highlight subsystem degrades silently to plain rendering on unknown language,
over-cap file, or timeout — match that ethos. If the language is unsupported, a
side is over the size cap, the parse/diff budget is blown, or the structural diff
finds nothing better than the line diff, **fall back to the normal `Loaded` line
diff** rather than failing. The toggle stays available but the body is the
familiar diff.

## New types (sketch)

```csharp
// Provider seam — token-nesting tree now, tree-sitter later, same interface.
internal interface ISyntaxTreeProvider
{
    SyntaxTree? Parse(string fileText, string languageId); // null → unsupported/over-cap
}

// Generic node: an atom (leaf token) or a delimited list of children.
internal sealed record SyntaxNode(
    SyntaxNodeKind Kind,        // Atom | List
    string? Text,               // atom text (normalized; null for lists)
    int ContentHash,            // subtree hash, position- and whitespace-independent
    SourceSpan Span,            // (line, startCol, endCol) in tab-expanded space
    IReadOnlyList<SyntaxNode> Children);

// One structural diff: per-side, the source spans that were added / removed,
// keyed for fast per-line lookup during flatten (parallels DiffHighlight.ForLine).
internal sealed class StructuralDiff
{
    public IReadOnlyList<EmphasisSpan> ForLine(DiffSide side, int lineNumber); // changed col ranges
    public bool LineIsContext(DiffSide side, int lineNumber);                  // all atoms matched
}

// Emphasis run in tab-expanded column space — same coordinate system as TokenSpan.
internal readonly record struct EmphasisSpan(int Start, int Length, EmphasisKind Kind); // Added | Removed
```

`StructuralDiffCoordinator.Compute(git, repo, diff, commitSha)` is the sibling of
`DiffHighlightCoordinator.Compute` (`DiffHighlightCoordinator.cs:20`): detect
language, fetch both sides via `GetFileText`, `Parse` each, run the diff, package
a `StructuralDiff` — returning null (→ fall back to line diff) on any miss.

## Implementation plan

### Phase 1 — Structural tokens out of `SyntaxHighlighter`
- Add an extraction that returns every token (not just non-`Default` ones) with
  source `(line, startCol, endCol)` + a coarse class (BracketOpen/Close, String,
  Comment, Word, Operator, Whitespace) from `ScopeColorMap` + literal chars.
- Reuse the existing whole-file tokenize loop, state-stack threading, caps, and
  timeouts (`SyntaxHighlighter.cs:75`). Pure addition; highlight path untouched.
- Tests: bracket/string/comment classification, multi-line string coherence
  (xUnit, fabricated text — no git/engine, like `DiffHighlightTests`).

### Phase 2 — Token-nesting tree + content hashing (`ISyntaxTreeProvider`)
- `TokenNestingTreeProvider.Parse`: fold the token stream into atoms + bracket
  lists; drop whitespace; compute position-independent subtree `ContentHash`.
- Tests: nesting correctness, unbalanced-bracket tolerance (degrade to a flat
  atom list, never throw), hash equality under reindent.

### Phase 3 — Tree diff (anchored recursive)
- Hash-anchor identical subtrees; recurse into gaps; atom-level LCS fallback.
  Emit per-side added/removed `SourceSpan`s. Behind an `IStructuralDiffer` seam.
- `StructuralDiffCoordinator.Compute` wiring (sibling of the highlight
  coordinator; reuses `GetFileText`).
- Tests (the payoff cases): pure reindent → zero changed atoms; symbol rename →
  only the identifier; line rewrap → only moved tokens; unbalanced/garbage →
  graceful fallback.

### Phase 4 — View-model wiring (mirror full-file)
- `DiffViewMode.Structural`; `DiffRenderState.Structural(...)`; `ToggleStructural()`
  (flip mode + `StartLoad`).
- `StartLoad` (`DiffViewModel.cs:389`) branches on mode; structural compute runs on
  the generation-guarded background lane like `StartHighlight` (`:505`), and
  re-attaches only to the still-current target. On null result → emit the normal
  `Loaded` line diff (silent fallback).

### Phase 5 — Rendering (`DiffContentView`)
- `FlattenStructural` branch in `SetRenderState` (`:187`): emit `DiffRow.Line`s
  with the **emphasis span channel** alongside syntax `Spans`; context lines (all
  atoms matched) render un-tinted even when reindented; `_hunksPatchable=false`.
- Extend `DiffLineText` (`:878`) to overlay emphasis runs (changed-range
  background tint / underline) on top of the existing syntax runs. Diff mode stays
  pixel-identical (no emphasis channel present).
- New theme slots in `DiffContentStyles` (`ThemeStyles.Diff.cs:29`):
  `StructuralAddedEmphasis`, `StructuralRemovedEmphasis`.

### Phase 6 — Toggle affordances
- Structural toggle button in `DiffWindowToolbar` (mirror
  `BuildFullFileToggleButton` `:102`) and `DiffPaneHeaderWidget`, bound to
  `vm.Mode`; distinct Lucide icon (e.g. `Binary` / `GitCompare`).
- Keyboard: extend `ListArrowKbmController` with `OnToggleStructural` + a key
  (e.g. `T`), wired in `LocalChangesContentView` / `CommitDetailsView`; unwired in
  `CommitsView` (history list, not a file list) — same boundary as `F`.

### Phase 7 — Polish & verification
- Size cap + budget (reuse `SyntaxHighlighter.MaxFileChars` / a whole-tree budget)
  → fallback. Truncation banner reuse.
- Build; `dotnet test`; manual `/run`: reindent, rename, rewrap on real C#/TS
  files; confirm context-vs-change classification, calm reindents, identifier-only
  rename emphasis, read-only (no hunk buttons), unsupported-language + huge-file
  fallback, and that **Diff mode is visually unchanged**.

## Touch list

| File | Change |
|------|--------|
| `SyntaxHighlighter.cs` | Structural-token extraction (full token stream + class + source spans) |
| `ScopeColorMap.cs` / new `TokenClass` | Coarse class for structural tokens |
| new `Diff/SyntaxTree.cs`, `TokenNestingTreeProvider.cs` | Tree model + provider (`ISyntaxTreeProvider`) |
| new `Diff/StructuralDiff.cs`, `StructuralDiffer.cs` | Diff result + anchored-recursive algorithm |
| new `Diff/StructuralDiffCoordinator.cs` | Orchestration (sibling of `DiffHighlightCoordinator`) |
| `DiffViewModel.cs` | `DiffViewMode.Structural`, `DiffRenderState.Structural`, `ToggleStructural`, mode-aware `StartLoad`, background compute + re-attach + fallback |
| `DiffContentView.cs` | `FlattenStructural`, emphasis-span overlay in `DiffLineText`, read-only |
| `ThemeStyles.Diff.cs` | `StructuralAddedEmphasis` / `StructuralRemovedEmphasis` slots |
| `DiffWindowToolbar.cs`, `DiffPaneHeaderWidget.cs` | Structural toggle button |
| `ListArrowKbmController.cs`, `LocalChangesContentView.cs`, `CommitDetailsView.cs` | `OnToggleStructural` + key |
| `GitBench.Tests/` | Tokenizer, tree, and diff unit tests (payoff cases) |

## Commit slices
1. Structural tokens + tree provider + hashing (Phases 1–2, unit-tested, no UI).
2. Anchored-recursive diff + coordinator (Phase 3, unit-tested, no UI).
3. View-model mode + render + emphasis overlay + fallback (Phases 4–5; verify via
   a temporary default mode).
4. Toggle button + keyboard (Phase 6).
5. Caps, budgets, polish, manual verification (Phase 7).

## Open decisions
- **Layout:** line-based rows with emphasis (cheaper, recommended first) vs.
  difftastic-style side-by-side node-aligned columns (a later, bigger render
  phase). Default to the former.
- **Emphasis visual:** background tint of changed ranges vs. underline vs. dimming
  the unchanged. Pick during Phase 5 against real diffs.
- **Where the toggle lives relative to FullFile:** two independent toggles, or one
  cycling Diff → Structural → FullFile. Recommend two independent toggles to start.

## Risks / non-goals
- **Non-goal:** structural mode never drives staging (`git apply`) — read-only,
  line diff remains the source of truth for patches.
- **Risk:** the token-nesting tree is coarser than a real grammar; whitespace-
  sensitive or bracket-light languages (Python, YAML) get little benefit. The
  `ISyntaxTreeProvider` seam exists so tree-sitter can replace it per-language
  later without touching the differ or renderer.
- **Risk:** quadratic blow-up on huge/pathological files — bounded by the same
  size cap + a tree-diff time budget, falling back to the line diff (matching the
  highlighter's existing guards).
