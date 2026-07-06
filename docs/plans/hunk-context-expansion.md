# Per-Hunk Context Expansion in `DiffView`

> GitHub-style expanders on the hunk separator bars: reveal ~20 more unchanged lines
> above/below a hunk without leaving diff mode. Complements the Full-File toggle —
> that stays the "read the whole thing" mode; this handles "show me the neighborhood
> of this hunk" (e.g. previewing the surroundings of a big deletion).

## Locked decisions

1. **Interaction model (GitHub-style)** — the existing hunk separator bar hosts the
   expanders. For the *gap* between two hunks: a **down arrow** reveals `ExpandStep`
   lines at the top of the gap (growing downward from the hunk above, rendered above
   the bar) and an **up arrow** reveals lines at the bottom of the gap (growing upward
   from the hunk below, rendered below the bar). When the remaining gap is
   ≤ `ExpandStep`, both collapse to a single **unfold-all** icon that bridges it. The
   first hunk's bar gets only the up arrow (top-of-file gap); a new trailing bar after
   the last hunk gets only the down arrow (EOF gap). A fully expanded gap drops its bar
   — the hunks read as one continuous block (GitHub behavior; the `@@` header goes with it).
2. **Step** — `DiffOptions.ContextExpandStep = 20`.
3. **Gap model** — gap `i` (0…H) is the hidden new-file region above hunk `i`; gap `H`
   is below the last hunk. Geometry comes from hunk `NewStart`/`NewLines` alone (exact
   for gaps 0…H−1). Expanded lines are by definition unchanged, so their old-side
   number is `new − delta`, delta read off the adjacent hunk boundary
   (`OldStart+OldLines − (NewStart+NewLines)` of the hunk above; 0 for gap 0). Shared
   helper `DiffGaps` (new file) computes per-gap bounds/deltas for both VM and view.
4. **Content** — after-side file text via the existing
   `GetFileText(repo, path, side, oldSide:false, commitSha, baseSha)`, **fetched lazily
   on the first expand click** (background lane, dropped if the target changes), then
   cached in the render state so every later click is synchronous. No new git plumbing.
   New side absent (deleted file) ⇒ no expanders at all (the whole file is in the hunk
   anyway).
5. **EOF gap before fetch** — whether lines exist below the last hunk isn't knowable
   from the diff alone. Heuristic: if the last hunk ends with fewer than
   `DiffOptions.ContextLines` (3) trailing `Context` lines, git hit EOF ⇒ no EOF bar.
   Otherwise show it optimistically; the first click fetches the file, and if nothing
   is actually below, the re-flatten drops the bar (rare, harmless). After fetch,
   counts are exact everywhere.
6. **Purely presentational** — expansion never mutates `DiffResult`/`DiffHunk`.
   `HunkPatchBuilder`, stage/unstage/discard, and the optimistic hunk apply are
   untouched. Expanded rows get `_rowToHunk = -1`, so hunk hover outlines and
   Stage/Discard buttons automatically exclude them; `_hunkRanges` keeps covering only
   the bar + real hunk lines.
7. **State & lifetime** — `DiffRenderState.Loaded` gains
   `ContextExpansion? Expansion = null`: the capped new-side `Lines` plus per-gap
   `(TopShown, BottomShown)` counts. Expansion resets on any reload / target change /
   optimistic hunk apply (all of those construct a fresh `Loaded` — gap indices and
   line numbers may have shifted, and GitHub resets too). It must *survive* the async
   highlight re-attach (see gotcha below).
8. **Scroll** — nothing new needed. `ApplyScrollForTransition`'s same-path/same-mode
   branch (`SetScrollTarget(prevScrollY)`) already preserves the exact offset: an
   up-arrow inserts rows below the bar (bar and everything above stay put), a
   down-arrow inserts rows above the bar (the hunk being read stays put, the bar slides
   down). Both match reader expectations.
9. **Rendering** — expanded lines are ordinary `DiffRow.Line` context rows (tab
   expansion, both gutters, syntax spans via
   `highlight?.ForLine(DiffLineKind.Context, oldN, newN)` — `DiffHighlight` already
   tokenizes the whole new-side file, so spans exist beyond the hunk). No intra-line
   emphasis. Bars additionally show a muted "… N hidden lines" label (localized;
   omitted on the EOF bar until the count is exact) and the expander icons at the left,
   over the gutter columns.
10. **Scope** — diff mode only; FullFile mode is untouched. Works identically in the
    embedded panes and the pop-out window (same `DiffContentView`/`DiffViewModel`).

## Known gotchas

- `StartHighlight`'s re-attach (`DiffViewModel.cs:564`) builds
  `new DiffRenderState.Loaded(diff, highlight)` — that would silently drop `Expansion`
  when the async highlight lands after an expand. Change to `cur with { Highlight = highlight }`.
- `CarryHighlightForward` uses `with` and only touches `Highlight`, so it's already safe.
- A pure-delete hunk at the top of the file can report `NewStart = 0`; clamp gap-0 size
  to ≥ 0 in `DiffGaps`.
- File text is capped at `DiffOptions.TruncationLineCap` (5000): clamp expansion to the
  lines actually held; a gap whose lines fall past the cap just stops expanding there.
- For unstaged diffs the working file can change between the diff load and the expand
  fetch; any real change broadcasts `WorkingTreeChangedMessage` → reload → expansion
  reset, so the inconsistency window is one frame-ish. Accept it.
- New localized string(s) must land in **all 6** `Strings/*.json` or LOC004 fails the build.
- `LucideIcons` needs an `UnfoldVertical` glyph const (lucide `unfold-vertical`); verify
  the bundled font covers it, else fall back to `ChevronsUp`/`ChevronsDown` composition.
  Icons draw fine in custom paint via `c.DrawText` with `FontFamily = LucideIcons.FontFamily`.

## Implementation plan

### Phase 1 — Gap model & VM state (`DiffGaps.cs`, `DiffViewModel.cs`)

**1.1** New `DiffGaps` static helper: from `DiffResult` (+ optional `fileLineCount`)
produce per-gap `{ int GapIndex, int NewStart, int NewEnd, int OldNewDelta }` (gap H
open-ended until `fileLineCount` known) plus the EOF heuristic
(`LastHunkReachesEof(DiffResult)` — trailing context < `ContextLines`).

**1.2** `ContextExpansion` record (next to the render states):
`(IReadOnlyList<string> Lines, bool Truncated, IReadOnlyDictionary<int, GapShown> Gaps)`
with `GapShown(int Top, int Bottom)`. Extend
`DiffRenderState.Loaded` with `ContextExpansion? Expansion = null`.

**1.3** `DiffOptions.ContextExpandStep = 20`.

**1.4** `DiffViewModel.ExpandGap(int gapIndex, GapExpandDirection dir)`
(`GapExpandDirection { Down, Up, All }`):
- Current render not `Loaded` → ignore.
- `Expansion == null` → fetch `GetFileText(oldSide:false)` via `RunBackground` on the
  default lane (Gen bump on reload drops stale fetches), `SplitLines` + cap, then apply
  the requested increment and re-emit `cur with { Expansion = ... }` **only if** the
  render still holds the same `Result` reference (mirror `StartHighlight`'s guard).
  `GetFileText` returning null (deleted underneath us) → ignore.
- `Expansion != null` → clamp against `DiffGaps(Result, Lines.Count)` and re-emit
  synchronously. `Down` → `Top += step`; `Up` → `Bottom += step`; `All` → cover the gap.

**1.5** Fix the `StartHighlight` re-attach to `cur with { Highlight = highlight }`.

### Phase 2 — Flatten & draw (`DiffRow.cs`, `DiffContentView.cs`)

**2.1** Extend the separator row:
`HunkSeparator(string Range, string? Header, GapBar? Gap)` with
`GapBar(int GapIndex, bool ShowDown, bool ShowUp, bool ShowUnfold, int? HiddenCount)`.
The EOF bar is a `HunkSeparator` with `Range = ""` (draw branch skips the `@@` text).

**2.2** `FlattenRows(r, highlight, expansion)` emission order per hunk `i`:
gap-`i` *top* segment rows → separator bar for hunk `i` (with `GapBar` when hidden
lines remain; plain when the gap is 0) → gap-`i` *bottom* segment rows → hunk lines.
After the last hunk: gap-`H` top segment → EOF bar (heuristic / exact). Fully expanded
gap ⇒ one contiguous segment, no bar. Expanded rows go through the same
tab-expand/`VisualCells`/spans path as hunk lines; `_hunkRanges` still anchors on the
separator row (or the hunk's first line row when the bar was dropped — `ButtonRowFor`'s
`Min(FirstRow+1, LastRow)` already tolerates that).

**2.3** Gutter digits: when expansion is present, include the max revealed line number
(and `Lines.Count` once known) in the `maxNew` computation so the gutter doesn't clip.

**2.4** `DrawHunkSeparatorRow`: draw the expander icons (caption-size Lucide glyphs,
hover-tinted) left-aligned over the gutter area, then the `@@` text (when non-empty)
shifted right of them, then the muted `DiffHiddenLines(n)` label after the header.

### Phase 3 — Input (`DiffContentView.cs`, `DiffMouseController.cs`, `DiffView.cs`)

**3.1** `public Action<int, GapExpandDirection>? OnExpandGap` on `DiffContentView`.
Hit-testing: `HitTestListRow` → row is `HunkSeparator { Gap: not null }` → per-icon
rect check (mirrors `HitTestButton` geometry math). Add
`TryClickExpander(PointF)`; extend `OnHunkPointerMove` with a hovered-expander state
(`SetDirty` on change) for hover tint.

**3.2** `DiffMouseController.OnMouseButtonStateChanged`:
`if (_content.TryClickExpander(p) || _content.TryClickHunkAction(p)) e.Consume();`

**3.3** Wire in `DiffView.cs` next to the hunk callbacks: `OnExpandGap = vm.ExpandGap`.

### Phase 4 — Strings & icons

**4.1** `DiffHiddenLines(count)` (e.g. "… {n} hidden lines") added to all 6
`Strings/*.json`.

**4.2** `LucideIcons.UnfoldVertical` const (+ verify glyph renders; arrows reuse
`ChevronUp`/`ChevronDown`).

### Phase 5 — Verification

- Build (`dotnet build GitBench\GitBench.csproj --artifacts-path <scratchpad>`).
- `GuiTestHarness` widget tests: synthetic two-hunk diff + fake `IGitService` file text —
  click down/up/unfold, assert emitted rows (segment placement, old/new gutter numbers,
  bar drop on full expansion, `_rowToHunk = -1` on expanded rows via stage-button absence).
- Manual via `/run`: middle gap both arrows; top gap up-only; EOF bar down-only and the
  reaches-EOF case (no bar); unfold-all on a small gap merges hunks and drops the bar;
  scroll stays anchored on both directions; expanded rows syntax-highlighted; hover
  outline + Stage/Discard ignore expanded rows; stage a hunk → expansion resets, numbers
  correct; deleted file → no expanders; pop-out window works; FullFile toggle unaffected.

## Touch list

| File | Change |
|------|--------|
| `Features/Diff/DiffGaps.cs` (new) | gap bounds/deltas, EOF heuristic |
| `DiffViewModel.cs` | `ContextExpansion`, `Loaded.Expansion`, `ExpandGap` + lazy fetch, highlight re-attach `with` fix |
| `DiffOptions.cs` | `ContextExpandStep` |
| `DiffRow.cs` | `HunkSeparator.Gap` (`GapBar`) |
| `DiffContentView.cs` | flatten segments/bars, gutter digits, bar drawing with icons/label, expander hit-test + hover, `OnExpandGap` |
| `DiffMouseController.cs` | `TryClickExpander` before hunk actions |
| `DiffView.cs` | wire `OnExpandGap` |
| `Controls/LucideIcons.cs` | `UnfoldVertical` |
| `Strings/*.json` (×6) | `DiffHiddenLines` |

## Commit slices

1. Gap model + VM state + `ExpandGap` (no UI yet; unit-test `DiffGaps`).
2. Flatten + bar rendering + input wiring (feature usable).
3. Strings/icons + edge cases (EOF heuristic, truncation clamp, gutter digits).
4. GUI harness tests + polish.
