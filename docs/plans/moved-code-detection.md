# Moved-code detection (Diff improvement #1, part 2)

> Plan for the moved-code half of `docs/diff-improvements.md` item #1: detect a block that was
> deleted in one place and re-added (verbatim) elsewhere, and render both sides as **"moved"**
> instead of an unrelated delete + add. Split out from intra-line highlighting
> (`docs/plans/intra-line-highlighting.md`) because it is a distinct, larger body of work:
> cross-hunk in v1, and cross-**file** in the full vision — which the current per-file diff
> architecture does not support without new plumbing.

## Current state (grounded)

- Diffs come from the `git` CLI, parsed into immutable records. `GitService.GetDiff`
  (`GitBench/Git/GitService.cs:2503`) runs `git diff`/`git show` with `--no-color -M
  --unified=3` — **no `--color-moved`**. `ParseGitDiff` (`:2692`) → `ParsePatch` (`:2744`) build
  `DiffResult → DiffHunk → DiffLine` (`GitBench/Git/DiffResult.cs:7-42`), `Kind ∈ {Context,
  Added, Removed}`. There is **no `Moved` kind anywhere.**
- **`GetDiff` is per-file** — there is no whole-changeset diff aggregate to cross-reference, so
  cross-file move detection has nothing to work with today.
- `HunkPatchBuilder.Build` (`GitBench/Git/HunkPatchBuilder.cs:62-68`) reconstructs patches from
  `line.Kind` and `line.Text`. **Neither may be mutated** for cosmetics, or staging/discarding a
  hunk breaks. Movedness must ride as parallel metadata, default-empty.
- Render: `DiffViewModel.StartLoad` (`GitBench/Features/Diff/DiffViewModel.cs:389`) loads on a
  background lane and emits `DiffRenderState.Loaded(DiffResult, DiffHighlight?)` (`:24`);
  `StartHighlight` (`:505`) re-emits the same `Loaded` carrying syntax spans.
  `DiffContentView.FlattenRows` (`GitBench/Features/Diff/DiffContentView.cs:275`) turns each
  `DiffLine` into a `DiffRow.Line` (`DiffRow.cs:20-26`); `DrawLineRow` (`:836`) paints a
  full-width background rect keyed on `Kind` (`:851-856`).
- `StatusPalette` already has a `Purple` slot (`GitBench/Theming/ThemePalettes.cs:46`) — the
  natural "moved" hue.

## Locked decisions

1. **v1 detects within-file moves only.** Cross-file moves are deferred (see "Deferred" below).
2. **`DiffResult`/`DiffHunk`/`DiffLine` stay byte-for-byte as git produced them.** No `Moved`
   kind; `Kind` stays `Added`/`Removed` (moved lines genuinely appear on one side, and `Kind`
   feeds `HunkPatchBuilder`). Movedness is parallel metadata.
3. **A precomputed `MoveMap`** is carried on `DiffRenderState.Loaded`, computed once on the load
   background thread (it is cross-hunk and O(total lines), so not view-local like intra-line).
4. **Runtime-toggleable**, mirroring `DiffOptions.SyntaxHighlightingEnabled` (`DiffOptions.cs:13`):
   add `MovedCodeDetectionEnabled`.

---

## Implementation

### 1. Data — `MoveMap` on `Loaded`, `MoveKind` on `DiffRow.Line`

```csharp
internal enum MoveKind { None, MovedAway, MovedHere }   // removed→away, added→here

// Identifies moved lines without mutating DiffResult. Key = (hunk index, line index in hunk).
internal sealed record MoveMap(IReadOnlyDictionary<(int Hunk, int Line), MoveKind> Lines)
{
    public static readonly MoveMap Empty = new(new Dictionary<(int, int), MoveKind>());
}
```

Carry it on the render state (`DiffViewModel.cs:24`):

```csharp
public sealed record Loaded(
    DiffResult Result, DiffHighlight? Highlight = null, MoveMap? Moves = null) : DiffRenderState;
```

Add `MoveKind Move = MoveKind.None` to `DiffRow.Line` (`DiffRow.cs:20-26`).

### 2. Computation — `MoveDetector` (pure, testable)

New helper `GitBench/Features/Diff/MoveDetector.cs`, run on the load background thread:

1. Walk all hunks; collect two ordered lists with positions: `removed[(hunk,line) → normalized
   text]` and `added[(hunk,line) → normalized text]`. Normalize = trim trailing whitespace
   (start indentation-sensitive; an "ignore-whitespace" mode is a later toggle).
2. Skip lines whose normalized text is blank or trivial (`}`, `{`, `)`, ...) as match *anchors*
   to avoid false positives — they can still be *part* of a longer matched run.
3. Find maximal runs where a contiguous removed sequence equals a contiguous added sequence,
   length ≥ `MinMovedBlockLines` (start at **3**). Practical approach: hash each normalized line;
   build `hash → list of added indices`; for each removed line, seed candidate matches and extend
   forward while both sides stay contiguous and equal; keep the longest, then mark all lines in
   both runs. Greedy longest-first; each line is claimed once. Bounded by
   `DiffOptions.TruncationLineCap` (5000).
4. Emit a `MoveMap`: removed run → `MovedAway`, added run → `MovedHere`.

Pure and unit-testable on a fabricated `DiffResult`.

```csharp
internal static class MoveDetector
{
    public static MoveMap Detect(DiffResult diff);
}
```

### 3. Plumbing

- In `StartLoad`'s `work` (`DiffViewModel.cs:435-437`), after `GetDiff`, compute the map off the
  UI thread:
  ```csharp
  var diff = git.GetDiff(repo, path, side, commitSha);
  var moves = DiffOptions.MovedCodeDetectionEnabled ? MoveDetector.Detect(diff) : null;
  if (mode == DiffViewMode.Diff)
      return (new LoadResult(new DiffRenderState.Loaded(diff, Moves: moves), diff), null);
  ```
- **Preserve `Moves` across the highlight re-emit.** `StartHighlight` currently builds
  `new DiffRenderState.Loaded(diff, highlight)` (`DiffViewModel.cs:519`), which would drop the
  map. Change to `cur with { Highlight = highlight }` so `Moves` survives.
- Audit `CarryHighlightForward` (`DiffViewModel.cs:449`) and any optimistic hunk-apply re-emit for
  the same drop (`grep "new DiffRenderState.Loaded"` to find all constructors).
- `SetRenderState` → `FlattenRows` (`DiffContentView.cs:215`): pass `loaded.Moves` and stamp each
  row: `var move = moves?.Lines.GetValueOrDefault((i, j)) ?? MoveKind.None;`.

### 4. Rendering & theme

In `DrawLineRow` (`DiffContentView.cs:838-849`), when `l.Move != None`, override the background
(and optionally the glyph color) to the moved tint:

```csharp
var bg = l.Move != MoveKind.None
    ? _styles.LineMovedBackground
    : l.Kind switch { /* existing Added/Removed/Context */ };
```

Add `LineMovedBackground` (+ optional `LineMovedGlyph`) to `DiffContentStyles`
(`ThemeStyles.Diff.cs:29-44`) and resolve from `status.Purple` (`ThemePalettes.cs:46`) in
`BuildDiffContent` (`ThemeStyles.Diff.cs:91`), reusing the existing tint alpha:

```csharp
LineMovedBackground: WithAlpha(status.Purple, DiffLineTintAlpha),
```

Keep the `+`/`-` glyph (the gutter line numbers still differ); the purple tint communicates
"moved". A left-edge accent bar is optional polish, not required for v1.

### 5. Toggle

Add to `DiffOptions` (`DiffOptions.cs`):

```csharp
public static bool MovedCodeDetectionEnabled = true;
```

Off → `Moves` is null → all rows `MoveKind.None` → identical to today.

### 6. Tests (`GitBench.Tests/MoveDetectorTests.cs`)

Mirror `DiffHighlightTests.cs` (fabricated `DiffResult`, no git):
- A 4-line block removed at top + re-added verbatim at bottom → both runs flagged away/here;
  surrounding edits untouched.
- Block shorter than `MinMovedBlockLines` → not flagged.
- A line that merely repeats (e.g. a lone `}`) → not flagged (anchor/length gate).
- Moved-and-modified (one line differs) → run breaks at the diff; the matched sub-run still
  flags, the modified line stays a normal add/remove (acceptable v1 behavior — note it).
- Detection off → `MoveMap` null → all rows `MoveKind.None`.
- Patch round-trip: a diff with moves still produces a byte-identical patch via
  `HunkPatchBuilder` (movedness never touches `Kind`/`Text`).

---

## Deferred — cross-file moves

Cut-from-A / pasted-into-B is **out of scope for v1.** `GetDiff` is per-file
(`GitService.cs:2503`) and there is no changeset-level diff aggregate, so cross-file detection
needs either:

- **(a)** a new whole-changeset diff call — `git diff` over all paths with
  `--color-moved=zebra`, parsing the move markup. Conflicts with the current `--no-color`
  invocation and the clean `ParsePatch`/`HunkPatchBuilder` round-trip; ANSI parsing is fragile.
- **(b)** a higher-level service that collects every per-file removed/added block across the
  changeset and matches across them in-house (reuses the v1 matcher, fed a multi-file corpus).

Both are larger architectural changes. Revisit after within-file moves prove the UX. Whatever
the cap, **surface it** (`log`/status) so "no moves found" isn't mistaken for "cross-file moves
aren't detectable here".

## Edge cases

- **Patch round-trip:** `HunkPatchBuilder` reads only `Kind`/`Text` (`HunkPatchBuilder.cs:62-68`);
  `MoveMap` never touches those, so staging/discarding a hunk is unaffected (covered by a test).
- **FullFile mode** (`DiffRenderState.FullFile`, `DiffViewModel.cs:28`): single-side, no removed
  lines — moves are inert. Leave `FlattenFullFile` (`DiffContentView.cs:344`) untouched.
- **Conflict / binary / error / truncated:** no `Loaded` line rows or already gated; nothing to do.
- **Pure reindent:** trimming trailing whitespace only — an indentation change is *not* a move,
  so the indentation-sensitive default keeps it as edits, not a false "moved".
- **Large diffs:** matching is bounded by `TruncationLineCap` (5000 lines).

## Relationship to intra-line highlighting

Intra-line (character) highlighting is the other half of diff-improvement #1 and is tracked in
`docs/plans/intra-line-highlighting.md`. The two are independent: intra-line is view-local and
cheap; moves are precomputed and cross-hunk. **Ship intra-line first.** A moved block may also
carry small edits — once both land, a `MovedHere`/`MovedAway` line can additionally show
intra-line emphasis where the moved copy was tweaked (no special-casing needed; the layers
compose).
