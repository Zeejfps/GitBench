# Intra-line (character) highlighting (Diff improvement #1, part 1)

> Plan for the intra-line half of `docs/diff-improvements.md` item #1: when a hunk replaces
> line(s) with similar line(s), highlight only the **characters that changed** on each side
> rather than tinting the whole line — the classic GitHub/VS Code look (a faint line tint + a
> stronger tint on the actual changed words). The other half of #1, moved-code detection, is a
> separate, larger body of work tracked in `docs/plans/moved-code-detection.md`.

## Current state (grounded)

**Computation** — diffs are produced by shelling out to `git`, parsed into immutable records.
- `GitService.GetDiff` (`GitBench/Git/GitService.cs:2503`) runs `git diff`/`git show` with
  `--no-color -M --unified=3` — **no `--word-diff`**.
- `ParseGitDiff` (`GitService.cs:2692`) → `ParsePatch` (`GitService.cs:2744`) build
  `DiffResult → DiffHunk → DiffLine` (`GitBench/Git/DiffResult.cs:7-42`). Granularity is the
  line: `DiffLine(Kind, OldLineNumber, NewLineNumber, Text)`, `Kind ∈ {Context, Added, Removed}`.
  There is **no sub-line structure and no in-house LCS/Myers** anywhere.

**Render** — the diff body is hand-painted, not a tree of label widgets.
- `DiffViewModel.StartLoad` (`DiffViewModel.cs:389`) loads the diff on a background lane and emits
  `DiffRenderState.Loaded(DiffResult, DiffHighlight?)` (`DiffViewModel.cs:24`); a second lane,
  `StartHighlight` (`DiffViewModel.cs:505`), re-emits the same `Loaded` carrying syntax spans.
  The re-emit reuses **the same `DiffResult` instance** — it re-attaches only when
  `ReferenceEquals(cur.Result, diff)` (`DiffViewModel.cs:518`), so every `DiffHunk`/`DiffLine`
  is reference-stable across the two emits. (This is what makes the §3 memoization a guaranteed
  cache hit on the second flatten.)
- `DiffContentView.SetRenderState` (`DiffContentView.cs:187`) → `FlattenRows`
  (`DiffContentView.cs:276`) turns each `DiffLine` into a `DiffRow.Line(Kind, OldNumber,
  NewNumber, Text, Chars, Spans)` (`DiffRow.cs:20-26`). `Text` is tab-expanded via
  `DiffText.ExpandTabs` (`DiffText.cs:13`, a fixed 4-space replacement).
- `DrawLineRow` (`DiffContentView.cs:837`) paints **one full-width background rect per line**
  keyed on `Kind` (`:852-857`), then gutters/glyph, then `DrawLineText`.
- `DrawLineText` (`DiffContentView.cs:879`) already renders **multiple colored runs within one
  line** by walking `Spans` **incrementally** (carrying `x` forward run-by-run) and calling
  `DrawTextRun` (`:910`), which measures each substring with `c.MeasureTextWidth` to position the
  next run. `SlotColor` (`:921`) maps a `TokenColorSlot` to a **foreground** color only.

**Key consequences for this work:**
1. A per-line **foreground** span model already exists (`TokenSpan`, `DiffRow.Line.Spans`) — but
   it is owned by syntax highlighting. Intra-line emphasis is a **background** concern, so it
   wants a *separate* channel, not the `Spans` list.
2. Line backgrounds are a single uniform rect today — per-range background tinting is net-new.
3. `HunkPatchBuilder.Build` (`HunkPatchBuilder.cs:62-68`) reconstructs patches from `line.Kind`
   and `line.Text`. **Neither may be mutated** for cosmetics, or staging/discarding a hunk
   breaks. New state must be parallel metadata, default-empty.
4. `DiffLineTintAlpha = 0x80` (`ThemeStyles.Diff.cs:89`) means the add/remove tints are already
   ~50% transparent; emphasis colors must read as *stronger* than that to stand out.
5. `FlattenRows` runs **twice per file** — once on the initial `Loaded(diff, null)` emit and again
   on the `Loaded(diff, highlight)` re-emit. Anything derived purely from `DiffResult` is therefore
   computed twice unless memoized (see §3).

## Locked decisions

1. **`DiffResult`/`DiffHunk`/`DiffLine` stay byte-for-byte as git produced them.** No emphasis
   fields on `DiffLine`. Emphasis rides as parallel metadata on `DiffRow.Line` (view-local).
2. **Intra-line emphasis = a background layer**, computed inside `FlattenRows` on the
   tab-expanded `Text` (so it shares one coordinate space with `Spans` and the glyph grid). It is
   cheap and local; no background lane, no new render-state field, no VM changes. The work is
   **memoized on hunk identity** so the double-flatten (consequence #5) computes it only once.
3. **Runtime-toggleable**, mirroring `DiffOptions.SyntaxHighlightingEnabled` (`DiffOptions.cs:13`):
   add `IntraLineHighlightingEnabled`.
4. **Monospace-but-measured positioning.** Emphasis rects are positioned with
   `c.MeasureTextWidth` over substrings of `Text`, identical to `DrawTextRun`, so they stay
   aligned even for wide glyphs.

---

## Implementation

### 1. Data — emphasis ranges on `DiffRow.Line`

Add one optional field to `DiffRow.Line` (`DiffRow.cs:20-26`). Ranges are `(Start, Length)` in
the **same tab-expanded column space** as `Text`:

```csharp
public sealed record Line(
    DiffLineKind Kind,
    string OldNumber,
    string NewNumber,
    string Text,
    int Chars,
    IReadOnlyList<TokenSpan>? Spans = null,
    IReadOnlyList<CharRange>? Emphasis = null) : DiffRow;
```

New value type, in `Features/Diff` (not `Theming` — emphasis is a diff concept and carries no
color, so `Theming/TokenColorSlot.cs` should not gain a dependency on it):

```csharp
internal readonly record struct CharRange(int Start, int Length);
```

`Emphasis` is null for context lines, pure adds/removes, and full-rewrite pairs (see gate below).

### 2. Computation — `IntraLineDiff` (pure, testable)

A new static helper `GitBench/Features/Diff/IntraLineDiff.cs`. Two responsibilities:

**(a) Pair lines within a hunk.** A *replace block* is a maximal run of `Removed` lines
immediately followed by a maximal run of `Added` lines (no intervening `Context`). Within a
block of `R` removed and `A` added lines, pair index-wise for `k in [0, min(R,A))`. Lines past
the overlap have no counterpart and get no emphasis (they read as plain add/remove).

Index-wise pairing matches GitHub/most viewers for the common balanced case, but has a known
failure mode: when a line is inserted or deleted in the *middle* of an otherwise-balanced block,
every subsequent pair shifts by one, so each shifted pair looks almost entirely changed. The
similarity gate below is what rescues this — mispaired lines fall under the match threshold and
emit nothing, degrading to plain delete+add rather than full-line noise. **The gate is therefore
load-bearing for unbalanced blocks, not just full rewrites; don't weaken it without accounting
for that.** Similarity-based best-match assignment over small blocks (`≤ ~10×10`) is the noted
future refinement.

**(b) Diff a paired (old, new) line into changed ranges.** Operate on the **tab-expanded**
strings:
1. Trim the common prefix and common suffix (char-wise). If everything matches, emit nothing.
2. Tokenize the differing middle into words (`[A-Za-z0-9_]+`), whitespace runs, and individual
   symbols — word granularity reads far better than per-char.
3. Run an LCS over the token lists (lines are short; `O(n·m)` is fine). Guard: if either side's
   middle exceeds ~`2000` chars, skip (treat as full-line change) to bound cost.
4. Map the non-matching tokens back to char ranges, offset by the trimmed prefix length, and
   coalesce adjacent ranges. Return old-side ranges and new-side ranges separately, each sorted
   and non-overlapping (the renderer in §4 relies on this to walk them in one incremental pass).

**Similarity gate (anti-noise).** Compute `matchedChars = commonPrefixLen + commonSuffixLen +
Σ(length of each matched middle token)`. If `matchedChars < ~30%` of the longer line, the pair is
a wholesale rewrite (or a mispairing from index-wise pairing) — emit *no* emphasis so the line
just reads as plain delete+add (highlighting everything is noise). The threshold is a tunable
constant in `IntraLineDiff`. Counting the trimmed prefix/suffix as matched matters: code lines
commonly share long stable prefixes (indentation, `var x = `), and omitting them would over-fire
the gate.

Suggested surface:

```csharp
internal static class IntraLineDiff
{
    // Per-line emphasis for one hunk, indexed by position in hunk.Lines. expandedTexts[i] is
    // DiffText.ExpandTabs(hunk.Lines[i].Text). Entry is null where there is no emphasis.
    // Memoized on the `lines` reference (see §3) — same hunk instance returns the cached array.
    public static IReadOnlyList<CharRange>?[] ForHunk(
        IReadOnlyList<DiffLine> lines, IReadOnlyList<string> expandedTexts);

    // Exposed for unit tests: ranges (old, new) for a single paired line.
    public static (IReadOnlyList<CharRange> Old, IReadOnlyList<CharRange> New) ForPair(
        string oldExpanded, string newExpanded);
}
```

Memoize inside `ForHunk` on the `lines` list reference, which is reference-stable across the two
flattens (consequence #5). `expandedTexts` is a deterministic function of `lines`
(`ExpandTabs` per line), so caching on `lines` alone is correct:

```csharp
private static readonly ConditionalWeakTable<IReadOnlyList<DiffLine>, IReadOnlyList<CharRange>?[]>
    Cache = new();

public static IReadOnlyList<CharRange>?[] ForHunk(
    IReadOnlyList<DiffLine> lines, IReadOnlyList<string> expandedTexts) =>
    Cache.GetValue(lines, _ => Compute(lines, expandedTexts));
```

Use `GetValue(key, factory)`, **not** `TryGetValue`+`Add`: `GetValue` creates-and-inserts
atomically, so it can't throw the duplicate-key `ArgumentException` a `TryGetValue`/`Add` pair
would if `ForHunk` were ever entered for the same `lines` from two threads. Both flattens are on
the UI thread today (grounding pass confirmed `SetRenderState`→`FlattenRows` is UI-thread), so the
race is only theoretical — but `GetValue` is also just shorter. The factory closes over
`expandedTexts`; on a cache hit it isn't invoked, so the ignored `expandedTexts` is correct (it is
a deterministic function of `lines`). `IReadOnlyList<DiffLine>` is an interface (reference type) so
it satisfies `where TKey : class`, and CWT keys by reference identity — exactly the hunk-instance
match we want. `ConditionalWeakTable` keys weakly, so entries are collected when the hunk is — no
manual eviction, no leak across diffs.

### 3. Integration — `FlattenRows`

`FlattenRows` (`DiffContentView.cs:302-329`) already expands tabs per line and attaches
`highlight?.ForLine(...)` spans. Add a per-hunk pre-pass that computes emphasis (memoized in
`ForHunk`, so the re-emit flatten is a cache hit), then attach it when building each row:

```csharp
for (var i = 0; i < r.Hunks.Count; i++)
{
    var h = r.Hunks[i];
    // ... existing separator row ...
    var expanded = new string[h.Lines.Count];
    for (var j = 0; j < h.Lines.Count; j++) expanded[j] = DiffText.ExpandTabs(h.Lines[j].Text);
    var emphasis = DiffOptions.IntraLineHighlightingEnabled
        ? IntraLineDiff.ForHunk(h.Lines, expanded)
        : null;

    for (var j = 0; j < h.Lines.Count; j++)
    {
        var l = h.Lines[j];
        var text = expanded[j];
        var spans = highlight?.ForLine(l.Kind, l.OldLineNumber, l.NewLineNumber);
        if (spans is { Count: 0 }) spans = null;
        _rows.Add(new DiffRow.Line(
            l.Kind,
            l.OldLineNumber?.ToString() ?? string.Empty,
            l.NewLineNumber?.ToString() ?? string.Empty,
            text, text.Length, spans, emphasis?[j]));
        // ... existing _maxRowCells update ...
    }
    // ... existing hunk-range bookkeeping ...
}
```

`expanded[]` is built every flatten anyway (the row needs it for `Text`), and it shares the
single coordinate source with `emphasis`. The actual LCS work runs once per hunk thanks to the
`ForHunk` cache. No VM/render-state changes.

### 4. Rendering — emphasis background rects + z-layering

In `DrawLineRow` (`DiffContentView.cs:837`), paint one rect per emphasis range. Insert the block
**right before the `DrawLineText` call** (`:873`) — by then `x` has advanced past the gutters and
glyph to the text start (the value computed at `:859-872`). Physical draw order is irrelevant;
the z-index assigned below governs layering, so this does not have to precede the gutter draws.

Walk the ranges **incrementally**, carrying `cx` forward exactly as `DrawLineText` does — measure
the gap before each range and the range itself, never re-measuring from column 0 (ranges are
sorted and non-overlapping per §2):

```csharp
if (l.Emphasis is { Count: > 0 } ranges)
{
    var emBg = l.Kind == DiffLineKind.Removed
        ? _styles.LineRemovedEmphasisBackground
        : _styles.LineAddedEmphasisBackground;
    var len = l.Text.Length;
    var col = 0;
    var cx = x; // text start
    foreach (var rng in ranges)
    {
        // Defensive clamp, mirroring DrawLineText's span guard (DiffContentView.cs:895-896).
        // §2 promises sorted, non-overlapping, in-bounds ranges, but a draw-time
        // Substring(col, start-col) throws ArgumentOutOfRangeException *every frame* if that
        // promise is ever violated (negative gap from an unsorted/overlapping range, or a
        // start/len past the tab-expanded text). A clamp degrades a bad range to a harmless
        // rect instead of taking down the whole diff body.
        var start = Math.Clamp(rng.Start, col, len);
        var end = Math.Clamp(rng.Start + rng.Length, start, len);
        if (start > col)
            cx += c.MeasureTextWidth(l.Text.Substring(col, start - col), MonoStartStyle);
        var w = c.MeasureTextWidth(l.Text.Substring(start, end - start), MonoStartStyle);
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(cx, bottom, w, _lineHeight),
            Style = SolidBgStyle(emBg),
            ZIndex = z + 1,
        });
        cx += w;
        col = end;
    }
}
```

The clamp is belt-and-suspenders: `ForPair` is still required to emit sorted, non-overlapping,
in-bounds, non-zero-length ranges (and the §7 tests must assert it). The clamp only guarantees that
a regression there shows up as a wrong-looking highlight, never as a per-frame crash in the render
loop.

**Z-layering.** Today the line bg is at `z` and gutters/glyph/text at `z + 1`
(`DiffContentView.cs:857` vs `:863-873`). Insert the emphasis layer between them: line bg `z`,
emphasis `z + 1`, foreground bumped to `z + 2` (bump the four `z + 1` foreground draws —
old/new gutter, glyph, and the `DrawLineText` call). No per-row z-stride change is needed:
`DrawDiffRowAt` already draws the hunk outline at `z + 5` and buttons at `z + 6`
(`DiffContentView.cs:664-666`), which proves each row already owns a z-band ≥7 wide.

**Shared column table (the clean end-state).** With this change, `DrawLineRow` and `DrawLineText`
now *both* walk the same line measuring substrings independently. The duplication — not the raw
per-range cost — is the smell. If it shows up in profiling on huge diffs, compute one cumulative
column→x table per visible line and have both the emphasis rects and the syntax runs index into
it. Visible rows are virtualized (small count), so for typical diffs the two incremental walks are
fine and this stays a deferred cleanup.

### 5. Theme — emphasis tokens

Add to `DiffContentStyles` (`ThemeStyles.Diff.cs:29-44`):

```csharp
uint LineAddedEmphasisBackground,
uint LineRemovedEmphasisBackground,
```

Resolve in `BuildDiffContent` (`ThemeStyles.Diff.cs:91`) as a *stronger* tint than the line bg —
e.g. reuse `status.SuccessLineBg`/`status.DangerLineBg` at a higher alpha (the line tint uses
`0x80`; emphasis at ~`0xC8` reads as the "changed word" highlight over the faint line tint):

```csharp
private const byte DiffEmphasisTintAlpha = 0xC8;
...
LineAddedEmphasisBackground:   WithAlpha(status.SuccessLineBg, DiffEmphasisTintAlpha),
LineRemovedEmphasisBackground: WithAlpha(status.DangerLineBg, DiffEmphasisTintAlpha),
```

No new palette slots needed — reuses existing status colors.

### 6. Toggle

Add to `DiffOptions` (`DiffOptions.cs`):

```csharp
public static bool IntraLineHighlightingEnabled = true;
```

Gated in `FlattenRows` (§3). Off → `Emphasis` is null everywhere → identical to today.

Like `SyntaxHighlightingEnabled`, this is baked into rows at flatten time, so flipping it at
runtime takes effect on the **next** `FlattenRows` (next diff load / re-emit), not instantly. If a
live toggle is ever wanted, wire a re-flatten on change — but that limitation is shared with the
existing syntax toggle and is out of scope here.

### 7. Tests (`GitBench.Tests/IntraLineDiffTests.cs`)

Pure-function tests on `IntraLineDiff.ForPair` / `ForHunk` (mirroring `DiffHighlightTests.cs`
which fabricates spans with no git):
- Single-word change mid-line → one range each side, prefix/suffix excluded.
- Trailing-only / leading-only change.
- Multiple disjoint changes in one line → multiple coalesced ranges (assert sorted, non-overlapping).
- Identical lines → no emphasis.
- Full rewrite (below similarity gate) → no emphasis.
- Shared-prefix gate sanity: lines differing only in a short suffix stay *above* the gate (prefix
  counts as matched) and emit emphasis rather than being suppressed as a rewrite.
- Unbalanced replace block (3 removed, 1 added) → only the paired index emphasized; extras plain.
- Mid-block insertion (4 removed, 5 added, one inserted line) → shifted mispairs fall under the
  gate and emit nothing, not full-line noise.
- Tab-containing lines → ranges align to expanded columns (compute on `ExpandTabs` output).
- `ForHunk` called twice on the same `lines` returns the cached array (reference-equal).

---

## Edge cases

- **Patch round-trip:** `HunkPatchBuilder` reads only `Kind`/`Text` (`HunkPatchBuilder.cs:62-68`);
  emphasis never touches those, so staging/discarding a hunk is unaffected. Add a regression test
  asserting a diff with emphasis still produces a byte-identical patch.
- **FullFile mode** (`DiffRenderState.FullFile`, `DiffViewModel.cs:28`): single-side, no paired
  lines — intra-line is inert there. Leave `FlattenFullFile` (`DiffContentView.cs:345`) untouched.
- **Conflict / binary / error / truncated:** no `Loaded` line rows or already gated; nothing to do.
- **Whitespace-only diffs:** intra-line tightly boxes the changed whitespace (useful).
- **Very long lines / huge replace blocks:** the `IntraLineDiff` length guard (§2) and
  `TruncationLineCap` (5000) bound cost.
- **Wide (CJK) glyphs:** rects use `MeasureTextWidth`, matching `DrawTextRun`, so alignment holds.

## Relationship to moved-code detection

Moved-code detection is the other half of diff-improvement #1, tracked in
`docs/plans/moved-code-detection.md`. The two are independent: intra-line is view-local and
cheap; moves are precomputed and cross-hunk. **Ship intra-line first.** Once both land, a moved
line that was also tweaked can show intra-line emphasis on top of the "moved" tint — the layers
compose with no special-casing.

---

## Virtual implementation walkthrough (grounding pass, 2026-06-29)

A dry run: every file/line the plan touches was read and the plan's claims checked against the
real code *before* writing a line of implementation. Result: the plan's line numbers and APIs are
accurate as of `aff703a`; the integration is mechanically clean; two robustness gaps were found and
folded back into §2 and §4 above.

### Grounding — claims confirmed against the tree

- **`DiffRow.Line` is safe to extend.** Only two positional constructions exist — `FlattenRows`
  (`DiffContentView.cs:317`) and `FlattenFullFile` (`DiffContentView.cs:362`). Every other reference
  (`:403`, `:430`, `:554`, `:658`, `:837`, `:879`) is a *property* pattern (`is DiffRow.Line l`),
  never a positional deconstruction, so a trailing optional `Emphasis` param breaks nothing.
  `FlattenFullFile`'s 6-arg call stays valid (param defaults to null) → no edit there, matching the
  FullFile edge-case note.
- **Theme wiring is a one-factory change.** `BuildDiffContent` is the sole `DiffContentStyles`
  factory and its only call site is `ThemeStyles.Build.cs:40`; the call passes `(p, status,
  diffSyntax)` and that signature is unchanged, so the call site needs no edit. `BuildDiffContent`
  builds the record with *named* arguments (`ThemeStyles.Diff.cs:92-120`), so the two new fields can
  be added anywhere in the record and initialized by name — order-independent.
- **`WithAlpha` is ARGB top-byte** (`ThemeStyles.cs:47-48`: `(color & 0x00FFFFFF) | (alpha << 24)`).
  `status.SuccessLineBg`/`DangerLineBg` exist (`ThemePalettes.cs:51,55`) and are fully opaque
  (`0xFF…`), so `WithAlpha(…, 0xC8)` yields the intended stronger-than-line-tint emphasis fill.
- **The memo is a guaranteed cache hit on the re-emit.** `StartHighlight` re-attaches with
  `new DiffRenderState.Loaded(diff, highlight)` reusing the *same* `diff` instance, gated on
  `ReferenceEquals(cur.Result, diff)` (`DiffViewModel.cs:518-519`). Same `DiffResult` → same
  `DiffHunk` → same `h.Lines` reference → CWT keyed on `h.Lines` hits on flatten #2. Even if this
  reuse ever changed, correctness is unaffected (a miss just recomputes an identical result); only
  the double-flatten cost returns.
- **Z-bands have headroom.** Line bg is `z` (`:856`); the four foreground draws are all `z + 1`
  (old gutter `:864`, new gutter `:868`, glyph `:870`, `DrawLineText` `:873`); row chrome is
  `z + 5`/`z + 6` (`:664`/`:666`). Inserting emphasis at `z + 1` and bumping the four foreground
  draws to `z + 2` keeps everything inside the row's existing ≥7-wide band — no per-row stride
  change. (In single-gutter/full-file mode only three of the four draws run; bumping all four is
  still correct since the old-gutter draw is inside `if (!_singleGutter)`.)

### Ordered implementation steps

1. **`CharRange` value type** (§1) — `internal readonly record struct CharRange(int Start, int
   Length);` in `Features/Diff` (same namespace as `DiffRow`, so no new usings).
2. **Extend `DiffRow.Line`** (§1) — append `IReadOnlyList<CharRange>? Emphasis = null`. Safe per
   grounding above.
3. **`IntraLineDiff.cs`** (§2) — new pure helper in `GitBench/Features/Diff/`. Usings:
   `GitBench.Git` (for `DiffLine`/`DiffLineKind`) and `System.Runtime.CompilerServices` (for
   `ConditionalWeakTable`). `ForHunk` (memoized via `GetValue`) → `Compute` (replace-block pairing,
   index-wise within each block) → `ForPair` (prefix/suffix trim → word/ws/symbol tokenize → LCS →
   map-back+coalesce → similarity gate). `Compute` must return an array of length exactly
   `lines.Count` so `emphasis?[j]` is always in range. `ForPair` must emit sorted, non-overlapping,
   in-bounds, non-zero-length ranges.
4. **`DiffOptions.IntraLineHighlightingEnabled = true`** (§6) — mirror `SyntaxHighlightingEnabled`
   (`DiffOptions.cs:13`).
5. **`FlattenRows`** (§3, `DiffContentView.cs:302-329`) — convert the per-line `foreach (var l in
   h.Lines)` to an indexed `for (j…)`; build `expanded[]` once; compute `emphasis` via `ForHunk`
   gated on the toggle; pass `emphasis?[j]` as the 7th arg. Preserve the existing `_maxRowCells`
   update and `_hunkRanges.Add(...)` bookkeeping verbatim. `FlattenFullFile` untouched.
6. **Theme** (§5, `ThemeStyles.Diff.cs`) — add `LineAddedEmphasisBackground` /
   `LineRemovedEmphasisBackground` to the `DiffContentStyles` record; add
   `const byte DiffEmphasisTintAlpha = 0xC8;`; add two named initializers in `BuildDiffContent`
   (`WithAlpha(status.SuccessLineBg/DangerLineBg, DiffEmphasisTintAlpha)`).
7. **Render** (§4, `DrawLineRow` `:837-873`) — insert the emphasis-rect walk (with the defensive
   clamp now in §4) right before the `DrawLineText` call at `z + 1`; bump the four foreground draws
   (`:864/:868/:870/:873`) from `z + 1` to `z + 2`. Line bg stays `z`.
8. **Tests** (§7) — `GitBench.Tests/IntraLineDiffTests.cs` for the enumerated `ForPair`/`ForHunk`
   cases (mirroring `DiffHighlightTests.cs`'s no-git style), plus a `HunkPatchBuilder` round-trip
   regression. Grounding confirms `HunkPatchBuilder.Build` (`GitBench/Git/HunkPatchBuilder.cs:60-71`)
   reads only `line.Kind`/`line.Text`/`line.NoNewlineAtEof` — emphasis can't perturb the patch.
9. **Build + run** `GitBench.Tests`.

### Issues caught (folded into the plan above)

- **I1 — render-loop crash hazard (fixed in §4).** The emphasis walk's
  `l.Text.Substring(col, rng.Start - col)` / `Substring(rng.Start, rng.Length)` throw
  `ArgumentOutOfRangeException` on the *very next frame, every frame* if `ForPair` ever yields an
  unsorted/overlapping/out-of-bounds range — a far worse failure than the wrong-color rect a clamp
  produces. `DrawLineText` already clamps its syntax spans (`:895-896`); §4 now mirrors that. The
  `ForPair` invariant is still required and test-asserted; the clamp is the backstop.
- **I2 — CWT duplicate-key race (fixed in §2).** `TryGetValue`+`Add` can throw `ArgumentException`
  if two threads miss and both `Add` the same `lines`. Switched to `GetValue(key, factory)`, which
  inserts atomically. Benign today (UI-thread-only), but strictly safer and shorter.
- **I3 — path nit.** `HunkPatchBuilder` lives at `GitBench/Git/HunkPatchBuilder.cs`, not under
  `Features/Diff/` (the body of the plan cites the bare filename, which is fine).

No blocking issues. The plan is implementable as written once I1/I2 (now incorporated) are in.
