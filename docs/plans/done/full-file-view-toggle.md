# Full-File View Toggle for `DiffView`

> Recovered from session `c27168db` (2026-06-02). Per-pane toggle switching the
> diff body between the normal diff and the **after-side full file** with changed
> lines tinted — read a change in full context without losing change highlighting.

## Locked decisions

1. **Content** — after-side full file via `GetFileText(repo, path, side, oldSide:false, commitSha)`
   (working tree / index blob / commit version). No new git plumbing.
2. **Render** — full file with **added lines tinted** (from `DiffResult.Hunks` → every
   `Added` line's `NewLineNumber`); removed lines absent (correct for "current state").
3. **Architecture** — reuse `DiffContentView` via a new `DiffRenderState.FullFile(...)` variant
   + `FlattenFullFile` branch. Set `_hunksPatchable=false` / empty hunk ranges so
   buttons/outlines/separators vanish automatically. Lazy fetch (only when toggle on).
   Reuses `DiffHighlight._newLines` (already tokenizes the whole new-side file).
4. **Scope** — sticky per-`DiffViewModel` via `DiffViewMode { Diff, FullFile }`; panes
   independent; pop-out windows start fresh in `Diff`. `StartLoad` becomes mode-aware.
5. **Invocation** — both a header/toolbar button (`DiffPaneHeader` + `DiffWindowToolbar`,
   reuse `BuildOpenInWindowButton` pattern) **and** the `F` key scoped to the focused file list.
6. **Edge cases** — binary → "Binary file not shown"; deleted/no after-side →
   "File has no current version"; error/null → message placeholder; pure-add → all tinted;
   cap at `TruncationLineCap` (5000) + truncation banner.
7. **Scroll** — toggle (same file) preserves viewed line by mapping top-visible `NewLineNumber`
   across modes; fresh load while sticky-on lands on first changed line + lead-in context;
   fallback top. Adds `TryGetTopVisibleNewLine()` / `ScrollToNewLine(int, leadIn)`.
8. **Rendering details** — single gutter (file line numbers only). Toggle button reflects
   **active state** (tinted when on) with a distinct Lucide icon (e.g. `FileText`).

### Known gotcha
`DiffViewModel.StartHighlight` (`DiffViewModel.cs:278`) currently re-attaches spans only to
`Loaded` states. Extend it to also target the `FullFile` state, or the full file won't
syntax-highlight (match on `Path` + `Side` since `FullFile` has no `Result` reference).

## Confirmed wiring
- `LocalChangesViewModel` and `CommitDetailsViewModel` each own their `DiffVm`; their content
  views (`LocalChangesContentView`, `CommitDetailsView`) own the `ListArrowKbmController` and
  hold `_vm`, so the `F` key can call `_vm?.DiffVm.ToggleFullFile()`.
- `CommitsView` (history list) also uses `ListArrowKbmController` but isn't a file list — leave
  its `F` hook unwired.
- `FullFile` render state flows to the body through the existing
  `vm.RenderState.Subscribe(_content.SetRenderState)` in `DiffView.cs:44` — so **`DiffView.cs`
  needs essentially no change** (only Phase-4 scroll work, if done view-side).

## Implementation Plan

### Phase 1 — Data model & render state (`DiffViewModel.cs`)

**1.1** Add the mode enum and the new render-state variant:
```csharp
internal enum DiffViewMode { Diff, FullFile }

internal abstract record DiffRenderState
{
    public sealed record Placeholder(string Text) : DiffRenderState;
    public sealed record Loaded(DiffResult Result, DiffHighlight? Highlight = null) : DiffRenderState;
    public sealed record FullFile(
        string Path,
        IReadOnlyList<string> Lines,
        IReadOnlySet<int> AddedLineNumbers,
        DiffSide Side,
        bool Truncated,
        DiffHighlight? Highlight = null) : DiffRenderState;
}
```

**1.2** Add mode state to `DiffViewModel`:
- `State<DiffViewMode>` in the `DiffState` record, exposed as
  `public IReadable<DiffViewMode> Mode { get; }` via `Slice`.
- `public void ToggleFullFile()` → flips mode, then calls `StartLoad()` to rebuild the render
  state for the current target.

**1.3** Make `StartLoad` mode-aware. Keep loading the diff (needed for the added-line set and to
drive highlight), then branch on mode in `onResult`:
- `Diff` mode → existing path (`Loaded` + `StartHighlight`).
- `FullFile` mode → from the loaded `DiffResult`: if binary/error/deleted, emit the appropriate
  `Placeholder`; otherwise fetch `GetFileText(repo, path, side, oldSide:false, commitSha)` on the
  background lane, split into lines, cap at `DiffOptions.TruncationLineCap` (set `Truncated`),
  compute `AddedLineNumbers` from `Hunks` (`Added` lines' `NewLineNumber`), emit `FullFile`, then
  `StartHighlight`.
- Deleted/null-text case → `Placeholder("File has no current version")`.

**1.4** Fix `StartHighlight` re-attach: after computing the highlight, attach to **either** a
matching `Loaded` **or** the current `FullFile` state (match on `Path` + `Side`).

### Phase 2 — Rendering (`DiffContentView.cs`)

**2.1** `SetRenderState`: add a `case DiffRenderState.FullFile ff` branch calling a new
`FlattenFullFile(ff)`. Set `_hunksPatchable=false`, leave `_hunkRanges`/`_rowToHunk` empty.

**2.2** `FlattenFullFile`: one `DiffRow.Line` per line — `Kind = ff.AddedLineNumbers.Contains(n)
? Added : Context`, `NewNumber = n.ToString()`, `OldNumber = ""`, `Spans = ff.Highlight?.ForLine(
DiffLineKind.Context, null, n)`. If `ff.Truncated`, append the truncation banner.

**2.3** Single-gutter mode. Add `bool _singleGutter` set true in `FlattenFullFile`, false
otherwise. In `DrawLineRow` and `ComputeNaturalContentWidth`/`EnsureMetrics`, when `_singleGutter`
skip the old-number column. Keeps diff mode pixel-identical.

**2.4** Scroll APIs:
- `public bool TryGetTopVisibleNewLine(out int lineNumber)` — from `_list.ScrollY`/`_lineHeight`
  find the top visible row, read its `DiffRow.Line.NewNumber`.
- `public void ScrollToNewLine(int lineNumber, int leadIn)` — find the row whose
  `NewNumber == lineNumber`, set `_list.SetScrollY((rowIndex - leadIn) * _lineHeight)` clamped.

### Phase 3 — Toggle affordances

**3.1 Button** — `DiffPaneHeader.cs` and `DiffWindowToolbar.cs`: add a toggle button mirroring
`BuildOpenInWindowButton`. Icon `LucideIcons.FileText`; bind background/tint to active state via
`vm.Mode`. Click → `vm.ToggleFullFile()`.

**3.2 Keyboard** — `ListArrowKbmController.cs`: add `public Action? OnToggleFullFile { get; set; }`;
in `OnKeyboardKeyStateChanged` handle `KeyboardKey.F` → invoke + `e.Consume()`. Wire in
`LocalChangesContentView.cs` and `CommitDetailsView.cs`:
`_arrowController.OnToggleFullFile = () => _vm?.DiffVm.ToggleFullFile();`. Leave unset in `CommitsView.cs`.

### Phase 4 — Scroll preservation (`DiffView`/`DiffContentView`)

**4.1** In `DiffView.Bind`/`DiffContentView`, when a new render state arrives that is a mode
*switch* for the same path, call `ScrollToNewLine(previousTopLine, leadIn:3)` using
`TryGetTopVisibleNewLine()` captured just before. Track "previous top line" and "previous path"
in `DiffContentView` across `SetRenderState`.

**4.2** Fresh-load landing: when `FullFile` arrives for a *different* path, scroll to the first
`AddedLineNumbers` entry (min) with `leadIn:3`; if none, top.

(Mapping keyed on `NewLineNumber`, shared by both modes; removed lines never participate.)

### Phase 5 — Verification
- Build.
- Manual via `/run`: Local Changes & Commit Details — toggle button + `F`; confirm full file
  with tinted adds, single gutter, no hunk buttons, syntax highlighting, position preserved
  across toggle, first-change landing when switching files in full-file mode. Pop-out window —
  button present, starts in Diff. Edge cases — binary, deleted (placeholder), pure-add (all
  tinted), huge file (truncation banner). Confirm diff mode is visually unchanged.

## Touch list
| File | Change |
|------|--------|
| `DiffViewModel.cs` | `DiffViewMode`, `FullFile` state, `Mode`, `ToggleFullFile`, mode-aware `StartLoad`, `StartHighlight` re-attach fix |
| `DiffContentView.cs` | `FlattenFullFile`, single-gutter, `TryGetTopVisibleNewLine`/`ScrollToNewLine`, full-file placeholders/truncation |
| `DiffView.cs` | Scroll preservation/landing on render-state change (small) |
| `DiffPaneHeader.cs`, `DiffWindowToolbar.cs` | Active-state toggle button |
| `ListArrowKbmController.cs` | `OnToggleFullFile` + `F` key |
| `LocalChangesContentView.cs`, `CommitDetailsView.cs` | Wire `F` to `DiffVm.ToggleFullFile()` |

## Commit slices
1. Model + render state + flatten (no toggle UI yet; verify via a temporary default).
2. Toggle button + keyboard.
3. Scroll preservation/landing.
4. Edge cases + truncation polish.
