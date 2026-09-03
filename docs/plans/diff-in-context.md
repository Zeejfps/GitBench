# Reading a diff in full context

## What this is

Reviewing a change in the Changes tab shows three lines either side of each hunk. That is enough to
see *what* changed and not enough to decide whether it is right. The questions a reviewer actually
has — what calls this, what is the rest of this function, does this still compile — are all about
the file the hunk sits in, and the diff shows almost none of it.

Today's escape hatch is the full-file toggle (`F`, or the header's `FileText` button). It is the
right instinct and the wrong shape: it drops removed lines, so it stops being a diff; it costs a
full reload; and the file it shows is inert — no hover, no definitions, no diagnostics, because the
Files pane is the only surface in the app wired to a language server.

This plan closes that gap in four steps, each shippable on its own:

1. **Context in place.** Expand a diff until it is the whole file, removed lines and all.
2. **Answers in place.** Hover, diagnostics and go-to-definition on diff lines that are disk lines.
3. **References in place.** Find-references from a diff line, in a panel.
4. **The file beside the diff.** A scroll-locked full-file pane, for when reading around is the job.

Steps 1–3 add no layout. Step 4 is the only one that spends screen width, and it is last on purpose.

## Why this is affordable

**The diff view and the file view are the same widget.** `FileBrowserPreview` builds a bare
`DiffContentView` and pushes a `DiffRenderState.FullFile` into it (`FileBrowserPreview.cs:96`). There
is no second renderer to write, no second highlighter, no second gutter. "Show the file" is a render
state, not a component.

**The diff already knows which of its lines are disk lines.** `FilePositionUnder` answers only on
rows that carry a new-side line number (`DiffContentView.cs:894-904`), and for an unstaged or
working-tree diff the new side *is* the file on disk. Removed rows have no new-side number, so they
fall out of the hit-test by construction. The position we would send a language server is already
exact, and already typed — `FileLine` and `RawColumn` are distinct types that cannot be confused with
row indices.

**Find-references is already built.** `IReferenceSource` and `ReferenceReply` are declared
(`IHoverSeams.cs`), `LanguageServerStore` implements them (`:143`, `:153`),
`LanguageServerConnection` gates on the server's advertised capability (`:104-114`),
`textDocument/references` is in the protocol (`Identifiers.cs:52`), the handshake advertises it
(`Handshake.cs:48,92`), and `PreviewSessionTests` covers it. Nothing in the UI has ever called it.
Step 3 is a panel and a menu item, not a feature.

The one thing genuinely missing is the reason LSP was kept out of diffs, and it is narrower than it
was written. `FileBrowserPreview.cs:130` says the diff pane and review window "show a file as it was
at a commit, and a server asked about one would answer about the file on disk". True for `Commit`
and `Range`. False for `Unstaged` and `WorkingTree`, where the after-side content *is* the file on
disk. The rule was right; it was applied one level too broadly.

## Decisions

| Area | Decision |
|---|---|
| Where full context comes from | The existing gap expanders, driven to completion — not the full-file toggle. Expansion weaves context rows into the row stream without touching the `DiffResult`, so removals, hunk outlines and staging pills all survive. The toggle's render state cannot do that: it has no removed lines. |
| The expand-all control | One control in the diff header with three states — collapsed, expanded, and expanded-but-truncated — not a fourth kind of expander bar. Per-gap bars stay exactly as they are. |
| Cost of expanding | One `NewSideLines` fetch, then synchronous. This is already how the first per-gap click works (`DiffViewModel.ExpandGapBy:310`); expand-all just applies `GapExpandDirection.All` to every gap after it. |
| Where LSP is allowed | `DiffSide.Unstaged` and `DiffSide.WorkingTree` only, and only on rows with a new-side line number. `Staged` is excluded: its after-side is the index blob, which may differ from disk. `Commit` and `Range` stay excluded for the original reason. |
| How the gate is expressed | A single predicate on the render state, not a check scattered through three controllers. A surface either offers a document to ask about or offers null, and the probes already handle null (`DefinitionProbeController._document`). |
| Go-to-definition from a diff | Navigates the Files pane and switches mode. `DefinitionProbeController` already takes an `IFileNavigator`, and `FileBrowserViewModel` is the only implementation — so the handoff a reviewer wants and the wiring the controller demands are the same thing. It records history, so the way back already exists. |
| Mode switching | An `OpenFileMessage(Path, Line, RepoId?)` on the bus, mirroring `OpenDiffWindowMessage`. Nothing navigates into Files today; a bus message keeps the diff pane from taking a dependency on the file browser's VM. |
| Coming back | The Files pane's back button returns to the previous file. Returning to the *diff* is a separate affordance: the message carries where it came from, and the Files tab shows a "back to review" chip while that origin is live. Without it, go-to-definition is a one-way door out of a review. |
| References UI | A panel below the diff, not a popup. A hover card is for one answer; a reference list is something you work through, and popups here close on outside click. |
| References scope | The declaration is excluded from the list, as `ReferenceReply` already documents. The count shown is the number of sites a reader can visit. |
| Step 4's split | Horizontal, inside the diff region, collapsed by default, with a persisted fraction. Collapsed-by-default because the Changes tab already spends width on three rails; a fourth column that is always there makes the default worse for the majority who never open it. |
| Scroll locking | One-directional per gesture: whichever surface the pointer is over drives, the other follows, and the follower's own scroll events are suppressed for that frame. Two-way binding on a shared line number oscillates. |
| What step 4 shows | The file on disk, through the Files pane's own loader, not a `FullFile` render of the diff's after-side. It is the pane where LSP is unconditionally legitimate; making it a diff render state would re-import the gating problem for no gain. |
| Off switch | Steps 2 and 3 ride the existing language-server subsystem's flag. No new setting. |
| Not in this plan | Side-by-side diff, editing, rename, completion, inline blame, a references panel in the Review *window* (see Risks). |

## What already exists — verified

- **Gap expansion, complete.** `DiffGap` / `DiffGaps.Compute` (`DiffGaps.cs:11,25`), `ContextExpansion`
  and `GapExpandDirection { Down, Up, All }` (`DiffViewModel.cs:27,35`), `ExpandGap` /
  `ExpandGapToDeclaration` (`:292,307`), row weaving at `DiffRowSet.EmitExpandedRows`, and the rule
  that expanded rows are emitted *outside* every `HunkRowRange` so hunk hover and staging pills
  ignore them. `DiffOptions.ContextExpandStep = 20`, `TruncationLineCap = 5000`.
- **Both hover seams, on the right class.** `DiffContentView` already declares
  `IHoverSurface, IDefinitionSurface` (`DiffContentView.cs:29-30`) and implements
  `HitTestFilePosition` (`:867`), `HitTestIdentifier` (`:871`), `ShowDefinitionLink` (`:887`),
  `SetDiagnostics` (`:567`). Nothing constructs the probes against it outside the Files pane.
- **The probes themselves.** `HoverProbeController` (350 ms dwell), `DefinitionProbeController`
  (120 ms dwell, capture phase, Cmd/Ctrl+click and F12, Cmd/Ctrl+`[`/`]` for history). Both take a
  `Func<(string Root, string Path)?> document` and do nothing when it returns null — which is exactly
  the gate this plan needs.
- **References, end to end, unused.** See "Why this is affordable".
- **Line/row mapping in both directions.** `DiffRowSet.RowForNewLine` (`:80`),
  `RowNearestNewLine` (`:95`), `NewLineAt` (`:71`); `DiffContentView.TopVisibleNewLine` (`:339`),
  `RequestScrollToNewLine` (`:352`), `ScrollToNewLine(line, leadIn)` (`:368`), and the
  `TopVisibleLineChanged` event. Step 4's scroll lock is these, wired to each other.
- **Navigation into a file, with history.** `IFileNavigator.NavigateTo(path, line)`,
  `NavigationHistory<FileBrowserPlace>` (capacity 64), `FileBrowserViewModel.Unfold(line)` which
  opens every ancestor fold before scrolling, and `_pendingReveal` for a file still loading.
- **Splitting.** `SplitterController` (axis-agnostic, 5 px, double-click hook),
  `VerticalSplitContainer` (fraction, min/max, `SetBottomCollapsed`), `ResizableSidebar`. There is no
  X-axis split *container*; `CommitHistory.cs:51` is the ~40-line hand-rolled precedent, RTL mirroring
  included.
- **Preference plumbing.** `Preferences` + `PreferencesService.Mutate` with a 500 ms debounce; adding
  a persisted fraction is one field and one setter.

## The hard parts

**1. The hover seam assumes one document per surface, and the Changes tab's default surface holds
many.** `FilePositionHit` is `(FileLine, RawColumn)` — no path — and `document` is a zero-argument
callback. That is correct for `DiffContentView`, which shows one file. The Changes tab's default
layout is not `DiffContentView`: it is `ReviewDiffListView`, every file's diff stacked in one
virtualized scroll (`ReviewDiffList.cs:58`), which implements `IDiffSelectionSurface` but neither
hover seam.

Two ways out, and the choice is the main design decision in step 2:

- *Widen the seam.* `HitTestFilePosition` returns the path alongside the position, and `document`
  becomes a function of the hit rather than of the surface. Touches every implementation and both
  probes, but leaves one code path for all surfaces.
- *Narrow the surface.* `ReviewDiffListView` answers `document` from whichever section the pointer is
  in — it already binary-searches rows to sections in `Locate` (`:394`) — and the seam is unchanged.
  Less churn, but the "one document" assumption becomes a lie the review list quietly works around.

Take the first. The assumption is load-bearing in the probes' single-entry answer cache
(`_asking` / `_answered` keyed by path and span), and a surface that lies about it will produce a
stale answer for the wrong file the first time a reader hovers two files in one scroll.

**2. Expanded context and the 5000-line cap.** `TruncationLineCap` truncates the fetched file, and
`ContextExpansion.Truncated` says so. Expand-all on a 12,000-line file therefore cannot mean "the
whole file". It must mean "as much as we have", with the truncation stated where the rows stop —
the existing full-file banner is the precedent. Silently ending the file at line 5000 in a view that
claims to show all of it is worse than not offering the control.

**3. Expansion is reset by everything, for a good reason.** `Expansion` is cleared on reload, on
target change, and on an optimistic hunk apply, because "gap indices and line numbers may have
shifted" (`DiffViewModel.cs:44-47`). That is correct: staging a hunk renumbers the gaps below it, so
replaying a remembered `Dictionary<int, GapShown>` after a refresh restores the wrong ranges — with
no visible error, because the rows it produces are real lines in plausible places.

But a reviewer who expands a file, stages a hunk in it, and watches their context vanish will not use
the feature twice. So expand-all must be sticky *as an intent*, not as state: remember "this file is
fully expanded" per path, and re-derive the gaps from the new `DiffResult` after each reload. That is
the one case where replay is safe, because "all gaps, fully" survives renumbering by definition. It
is also the argument for shipping expand-all before any partial-expansion persistence: the total case
is sound and the partial case is a data-migration problem in disguise.

**4. Scroll locking without oscillation.** Both surfaces report their top line and both accept a
scroll-to-line. Bound naively they fight: A moves, B follows, B reports, A follows. The follower must
ignore its own `TopVisibleLineChanged` for the frame in which it was driven, and the driver must be
whichever surface the pointer is over — not whichever moved last, because inertial scroll keeps
reporting after the gesture ends. Lines with no counterpart (a removed line has no disk line) hold
the follower where it is rather than jumping it to the nearest.

**5. `ToggleFullFile` re-loads.** It flips the mode and calls `StartLoad` (`:400`), re-fetching text
and re-tokenizing. Step 1 makes that toggle largely redundant in the Changes tab, but the toggle
stays for commit and range diffs, where expansion still fetches and the after-side file is the only
whole-file view available. Do not delete it; do stop pointing new affordances at it.

**6. A server started by a diff.** `FileShown` is what launches a language server, and it currently
fires only from the Files pane. Firing it from a diff means a review of a Rust repo can start
rust-analyzer — 32 seconds and 1.7 GB on a cold project. The status chip must be visible on the diff
surface too, or a reviewer gets an unexplained pause and a fan. Reuse `LanguageServerStatusChip`; do
not invent a second indicator.

## Build order

Each phase ends somewhere shippable.

**Phase 1 — expand all context.** `ExpandAll()` on `DiffViewModel`: one `NewSideLines` fetch, then
`All` on every gap. Header control with the three states. Stickiness across refresh and stage.
Truncation stated in the row stream. No layout change, no new surface, no LSP.
*Done when:* a reviewer can read any changed file end to end without leaving the review, stage a hunk
in it, and still be reading it.

**Phase 2 — LSP on working-tree diffs.** Widen `HitTestFilePosition` to carry a path (hard part 1);
implement both hover seams on `ReviewDiffListView`; add the disk-backed predicate; construct
`HoverProbeController` and `DefinitionProbeController` against the diff surfaces where it returns
true; bind diagnostics; add `OpenFileMessage` and the mode switch behind go-to-definition; add the
"back to review" chip; put the status chip on the diff header.
*Done when:* Ctrl-hovering a symbol in an unstaged diff underlines it, Ctrl-click lands in the Files
pane on the declaration, and the way back is one click. Commit and range diffs are unchanged.

**Phase 3 — references.** A `ReferencesPanel` under the diff, fed by `ILanguageServerStore
.ReferencesAsync`. Entry: a context-menu item on a symbol, and a keystroke. Rows group by file, click
navigates through `OpenFileMessage`, and the panel survives the navigation so a reviewer can walk the
list. Empty, unsupported and still-indexing are three different messages.
*Done when:* "what calls this" is answerable from a diff line without typing the symbol anywhere.

**Phase 4 — the file beside the diff.** X-axis split container in the Changes tab's diff region,
collapsed by default, fraction in `Preferences`. Right pane hosts the Files pane's preview body on
the active diff file. Scroll lock per hard part 4. A keystroke opens and closes it.
*Done when:* a reviewer can read the diff and the surrounding file at once, at a window width that
still fits the branches rail.

## Testing

**The disk-backed predicate gets a test per `DiffSide`**, and the two that must be false — `Staged`
and `Commit` — matter more than the two that must be true. A hover that answers about disk while
showing an index blob is the exact bug the original exclusion existed to prevent, and it is
invisible: the card is plausible and wrong.

**Row-to-path resolution in the review list is tested on a scroll holding at least three files**,
with the pointer moved across a section boundary in one gesture. A fixture with one file cannot catch
the cache returning the previous file's answer.

**Positions keep the fixture rule from the LSP plan:** no fixture whose row index equals its file
line. The diff surface makes this easier to get wrong than the file view did, because chrome rows —
banners, hunk separators, tears — put a permanent offset between the two, and expansion changes that
offset while the reader is on the same line.

**Expansion is tested as a property**, not a case: for every gap, expanding it and reading the row
stream must yield the file's lines in order with no duplicates and no holes, and every `HunkRowRange`
must still bound the same hunk it did before. That property is what keeps staging pills correct after
expansion, and it is cheap to check exhaustively over the existing diff fixtures.

**Stickiness is tested through a stage.** Expand, stage a hunk, let the working-tree refresh land,
assert the expansion is still there and the scroll has not moved. This is the regression that will
actually reach a user.

**Scroll locking is tested without pixels** by driving the two surfaces' line reports directly and
asserting the follower converges and stops — specifically that a driven follower emits nothing that
drives the driver back.

## Risks

1. **Starting a language server from a review.** The cost is real and the trigger becomes much more
   common: every reviewer of a Rust repo, rather than every reader of a Rust file. Mitigated by the
   visible status chip, the existing active-repo-only policy, and the existing off switch — but it is
   the change most likely to be felt as "the app got slower".
2. **Widening the hover seam touches working code.** Both probes and every surface implementation
   change in phase 2 for a benefit that only the review list needs. The alternative leaves a latent
   wrong-file bug. Worth doing, worth doing behind the existing probe tests first.
3. **The Review window will want all of this and cannot have phase 2 or 3.** It shows a base→head
   range: not disk, not askable. A reviewer who learns these affordances in the Changes tab will find
   them missing in the window with no explanation. The affordances must be absent, not disabled-
   looking, and the window's own answer is phase 1 — which does work there.
4. **Phase 4's width.** The Changes tab at 1400px default already carries the repo rail, the branches
   rail and the file tree. Collapsed-by-default is the mitigation; if it still does not fit, the
   honest fallback is the existing pop-out window pattern rather than a cramped fourth column.
5. **Expansion makes big diffs big.** A 5000-line row stream is well within what the virtualized list
   handles, but expand-all across a 200-file review stack is not one file's worth of rows. Expand-all
   is per file, never per stack.

## Deliberately not doing

**Merging the Files pane and the Changes tab into one surface.** Files mode could grow a changed-files
filter and a per-tab Diff/File toggle, which would put everything in one place. It would also dissolve
the stacked review flow — one scroll, all files, fold-on-viewed, `j`/`k` — that `ReviewDiffPanel`
gives today, and tab identity would have to become path-plus-revision. That trades a good review
surface for a good browsing surface. If it is ever wanted, it is additive to the review list, not a
replacement for it.

**A floating context panel.** The assistant overlay is the template and it works, but a panel that
floats over the diff occludes the thing being read, and phase 4 gives the same content without
covering anything. Reconsider only if phase 4's width problem proves unsolvable.

**Peek-definition inline in the diff.** An inline drawer between diff rows is the nicest version of
"look without leaving", and `ReviewDiffListView` already has the variable-height rows and the
re-anchoring to support it. It is deferred because it needs phases 2 and 3 to have anything to show,
and because a reader who has those two rarely needs it. Revisit after phase 3, with usage as evidence.

**Side-by-side diff.** Unrelated to context, a large amount of new painting, and it makes the width
problem in phase 4 strictly worse.
