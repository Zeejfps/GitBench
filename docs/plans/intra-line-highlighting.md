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
- `DiffContentView.SetRenderState` (`DiffContentView.cs:187`) → `FlattenRows`
  (`DiffContentView.cs:275`) turns each `DiffLine` into a `DiffRow.Line(Kind, OldNumber,
  NewNumber, Text, Chars, Spans)` (`DiffRow.cs:20-26`). `Text` is tab-expanded via
  `DiffText.ExpandTabs` (`DiffText.cs:13`, a fixed 4-space replacement).
- `DrawLineRow` (`DiffContentView.cs:836`) paints **one full-width background rect per line**
  keyed on `Kind` (`:851-856`), then gutters/glyph, then `DrawLineText`.
- `DrawLineText` (`DiffContentView.cs:878`) already renders **multiple colored runs within one
  line** by walking `Spans` and calling `DrawTextRun` (`:909`), which measures each substring with
  `c.MeasureTextWidth` to position the next run. `SlotColor` (`:920`) maps a `TokenColorSlot` to a
  **foreground** color only.

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

## Locked decisions

1. **`DiffResult`/`DiffHunk`/`DiffLine` stay byte-for-byte as git produced them.** No emphasis
   fields on `DiffLine`. Emphasis rides as parallel metadata on `DiffRow.Line` (view-local).
2. **Intra-line emphasis = a background layer**, computed inside `FlattenRows` on the
   tab-expanded `Text` (so it shares one coordinate space with `Spans` and the glyph grid). It is
   cheap and local; no background lane, no new render-state field, no VM changes.
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

New value type (next to `TokenSpan` in `Theming/TokenColorSlot.cs`, or in `Features/Diff`):

```csharp
internal readonly record struct CharRange(int Start, int Length);
```

`Emphasis` is null for context lines, pure adds/removes, and full-rewrite pairs (see gate below).

### 2. Computation — `IntraLineDiff` (pure, testable)

A new static helper `GitBench/Features/Diff/IntraLineDiff.cs`. Two responsibilities:

**(a) Pair lines within a hunk.** A *replace block* is a maximal run of `Removed` lines
immediately followed by a maximal run of `Added` lines (no intervening `Context`). Within a
block of `R` removed and `A` added lines, pair index-wise for `k in [0, min(R,A))`. Lines past
the overlap have no counterpart and get no emphasis (they read as plain add/remove). Index-wise
pairing is what GitHub/most viewers do for the common balanced case; similarity-based re-pairing
of unbalanced blocks is a noted future refinement.

**(b) Diff a paired (old, new) line into changed ranges.** Operate on the **tab-expanded**
strings:
1. Trim the common prefix and common suffix (char-wise). If everything matches, emit nothing.
2. Tokenize the differing middle into words (`[A-Za-z0-9_]+`), whitespace runs, and individual
   symbols — word granularity reads far better than per-char.
3. Run an LCS over the token lists (lines are short; `O(n·m)` is fine). Guard: if either side's
   middle exceeds ~`2000` chars, skip (treat as full-line change) to bound cost.
4. Map the non-matching tokens back to char ranges, offset by the trimmed prefix length, and
   coalesce adjacent ranges. Return old-side ranges and new-side ranges separately.

**Similarity gate (anti-noise).** If the changed fraction of a pair is high — e.g. matched chars
`< ~30%` of the longer line — the two lines are a wholesale rewrite, not an edit. Emit *no*
emphasis for that pair so the line just reads as plain delete+add (highlighting everything is
noise). Tunable constant in `IntraLineDiff`.

Suggested surface:

```csharp
internal static class IntraLineDiff
{
    // Per-line emphasis for one hunk, indexed by position in hunk.Lines. expandedTexts[i] is
    // DiffText.ExpandTabs(hunk.Lines[i].Text). Entry is null where there is no emphasis.
    public static IReadOnlyList<CharRange>?[] ForHunk(
        IReadOnlyList<DiffLine> lines, IReadOnlyList<string> expandedTexts);

    // Exposed for unit tests: ranges (old, new) for a single paired line.
    public static (IReadOnlyList<CharRange> Old, IReadOnlyList<CharRange> New) ForPair(
        string oldExpanded, string newExpanded);
}
```

### 3. Integration — `FlattenRows`

`FlattenRows` (`DiffContentView.cs:301-328`) already expands tabs per line and attaches
`highlight?.ForLine(...)` spans. Add a per-hunk pre-pass that computes emphasis, then attach it
when building each row. Because the inner loop emits rows sequentially, compute the whole hunk's
emphasis array first:

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

This recomputes on the `StartHighlight` re-emit (one extra pass per file) — negligible for short
lines, and it keeps a single coordinate source (the expanded `text`). No VM/render-state changes.

### 4. Rendering — emphasis background rects + z-layering

In `DrawLineRow` (`DiffContentView.cs:836`), after the full-line background rect (`:851-856`)
and **before** the gutters/text, paint one rect per emphasis range. Position each rect by
measuring substrings of `l.Text` exactly as `DrawTextRun` does, so it lands under the right
glyphs (the text start `x` is the value computed at `:858-870`):

```csharp
if (l.Emphasis is { Count: > 0 } ranges)
{
    var emBg = l.Kind == DiffLineKind.Removed
        ? _styles.LineRemovedEmphasisBackground
        : _styles.LineAddedEmphasisBackground;
    foreach (var rng in ranges)
    {
        var pre = c.MeasureTextWidth(l.Text.Substring(0, rng.Start), MonoStartStyle);
        var w   = c.MeasureTextWidth(l.Text.Substring(rng.Start, rng.Length), MonoStartStyle);
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(x + pre, bottom, w, _lineHeight),
            Style = SolidBgStyle(emBg),
            ZIndex = z + 1,
        });
    }
}
```

**Z-layering.** Today the line bg is at `z` and gutters/glyph/text at `z + 1`
(`DiffContentView.cs:855` vs `:863-872`). Insert the emphasis layer between them: line bg `z`,
emphasis `z + 1`, foreground bumped to `z + 2`. (Bump the four `z + 1` foreground draws in
`DrawLineRow` to `z + 2`; verify `DrawDiffRowAt` at `DiffContentView.cs:635` leaves a free
integer per row — if rows are spaced by 1, widen the per-row z stride to ≥3.)

Cost note: the two `Substring` + `MeasureTextWidth` calls per range allocate. If profiling flags
it on huge diffs, precompute cumulative column widths once per visible line; for typical diffs
the visible-row count is small (virtualized) so it is unlikely to matter.

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

### 7. Tests (`GitBench.Tests/IntraLineDiffTests.cs`)

Pure-function tests on `IntraLineDiff.ForPair` / `ForHunk` (mirroring `DiffHighlightTests.cs`
which fabricates spans with no git):
- Single-word change mid-line → one range each side, prefix/suffix excluded.
- Trailing-only / leading-only change.
- Multiple disjoint changes in one line → multiple coalesced ranges.
- Identical lines → no emphasis.
- Full rewrite (below similarity gate) → no emphasis.
- Unbalanced replace block (3 removed, 1 added) → only the paired index emphasized; extras plain.
- Tab-containing lines → ranges align to expanded columns (compute on `ExpandTabs` output).

---

## Edge cases

- **Patch round-trip:** `HunkPatchBuilder` reads only `Kind`/`Text` (`HunkPatchBuilder.cs:62-68`);
  emphasis never touches those, so staging/discarding a hunk is unaffected. Add a regression test
  asserting a diff with emphasis still produces a byte-identical patch.
- **FullFile mode** (`DiffRenderState.FullFile`, `DiffViewModel.cs:28`): single-side, no paired
  lines — intra-line is inert there. Leave `FlattenFullFile` (`DiffContentView.cs:344`) untouched.
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
