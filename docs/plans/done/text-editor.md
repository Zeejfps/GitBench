# A text editor in the Files pane — from a viewer to something you can type in

> **Framing:** the Files pane already renders a file better than most editors' preview: tree-sitter
> highlighting, folding, an outline, a usage lens, LSP hover and go-to-definition, find-in-file,
> drag-select with tab-faithful copy. What separates it from an editor is not rendering. It is that
> every layer under the pixels assumes the text never changes — `FilePreview.Text` holds an
> `IReadOnlyList<string>`, `DiffRowSet` is rebuilt whole on any change, and the selection is thrown
> away whenever the row count moves.
>
> So this plan is mostly *not* about typing. It is about inverting that assumption once, carefully,
> and then letting the twelve features already built on top keep working. The keystroke handling is
> the last third and the cheapest third.
>
> The scope discipline is the other half of the plan. An editor inside a git client is how you end
> up maintaining an IDE. The target here is: **fix a typo, resolve a conflict, tweak a line before
> committing.** Not a place to write a feature. Completion, rename, formatting and refactoring are
> deliberately out, and the module boundaries are drawn so they can come later without rework.
>
> "Resolve a conflict" is the one target that needs its scope stated, because editing alone does not
> achieve it: a saved file with the markers removed is still unmerged until `git add`. So it means
> *editing the conflict markers in the Files pane and marking the file resolved from there* — the
> `MarkResolved` step is a named deliverable of module 7. It is not a three-way merge UI.

## Decisions

| Area | Decision |
|---|---|
| Where editing is possible | **The Files pane preview only.** The diff pane and the review list stay strictly read-only. The gate is a host opt-in flag plus *provenance* — "this render state is a working-tree file at this absolute path" — not `SingleGutter`, which does not discriminate (A2). *Superseded: the gate that shipped is whether a document was handed in at all — see the correction under A2, and the note above the Phases list.* |
| Buffer | Piece table over the immutable loaded text plus an append-only add buffer, with a line index. Not `string[]`, not `char[]`: both make a mid-file insert O(file). |
| The unit of change | One `TextEdit(TextRange, string)` value. Undo, the row re-projection, marker shifting, LSP `didChange` and (later) tree-sitter `InputEdit` all consume it. Getting this one type right is what stops four converters existing. |
| Positions across edits | `Anchor` objects owned by the document and shifted by every edit — carrying caret, selection, search hits and diagnostics. (Not folds: `FoldState` already keys collapsed regions by outline path, not line number, so fold identity survives edits for free.) Today's `DiffTextPos` is `(RowIndex, ExpandedColumn)` (`DiffTextSelection.cs:10`) — a *row* index into a stream that is discarded on any reshape (`DiffContentView.cs:242,273`); that cannot survive typing. Moving it to document coordinates also fixes an existing wart: the selection would stop being thrown away on every fold toggle, on the read-only surfaces too. |
| A trailing newline opens a line | `"a\n"` is **two lines** in `TextDocument`, `"a"` is one, and `EndsWithNewline` is derived from that rather than stored beside it. This is how every real editor counts and it is what lets a caret sit after the final newline — but `TextLines.Split` gives `["a"]` for both, so the editable projection shows one more row than the viewer does for the same file. Deliberate: the document is right, and phase 2's equivalence test asserts the difference is *exactly* that one row and nothing else. |
| Row projection | The editor gets its **own `EditorRowSet`**; `DiffRowSet` is not made incremental and is not touched. Duplicating ~35 lines of flattening is cheaper than a dependency edge from the mutation model into three read-only surfaces — Rule 2's "more files at the same edge count is neutral, more edges is not". They share the `DiffRow` type and `DiffRowPainter`, which is the whole point of `DiffRow` being a type. |
| Undo | An inverse-edit journal, with the framework field's coalescing *policy* ported verbatim (see below). Not full-text snapshots. |
| Text entry | `TextInputEvent` only, never decoded from `KeyboardKeyEvent`. Claimed with `ConsumeAsText`, never `Consume`. |
| IME | Preedit is a per-document overlay applied at draw time, never written to the document. A half-typed composition must not reach tree-sitter, the LSP, or the file. |
| Soft wrap | **None in v1**, matching the viewer today. But nothing new may assume `RowIndex == line`; wrap is the first thing that breaks that and it breaks folding, hit-testing and Up/Down with it. |
| Selection | **One model, `DiffSelectionModel`, on every surface, and it stays in row coordinates.** It is what the painter reads (`TryRowSpan`) and what Ctrl+C and Ctrl+A act on; a second representation beside it is Rule 1's exact prohibition. What it gains is a single `Remap` operation applied whenever the row stream is rebuilt — the *surface* supplies the mapping, because only the surface knows what changed. `EditSession` does not own a selection; it takes one and returns one. |
| Multi-caret | **Out of v1** — one `SelectionRange`. What day one buys instead is the seam that actually matters: every edit operation takes `IReadOnlyList<TextEdit>` applied in one transaction. A range list with an unenforced `Count == 1` would buy none of the hard parts (overlap merging after an edit, per-caret goal cells, multi-range paste, undo granularity across carets) and would add a representable illegal state. |
| Save | Explicit **Ctrl/Cmd+S**. No autosave, no save-on-blur — this app runs beside a build and a running program. Ctrl+S is unbound today. |
| Encoding | **Built, phase 0.** `FileWriteBack = Reversible(FileEncoding) \| Refused(WriteBackRefusal)`, produced *by* the decode rather than declared beside it. The first draft of this row asked for a `FileEncoding` record carrying a `roundTripped` bool; that is the shape a caller can write through without ever reading the flag. Holding a `FileEncoding` is now itself the evidence that writing through it reproduces the file. `FileEncoding` is `(FileCharset, LineEnding, EndsWithNewline)`, with the BOM folded into the charset tag because a BOM-less UTF-16 is not something a read here can produce. |
| Truncated files | **Never editable** — and *the same question* as the encoding one, not a second gate: `WriteBackRefusal { Truncated, Lossy }`. One refusal type means a save asks once, and "truncated but writable" stops being representable. |
| External changes | Clean buffer → silent reload. Dirty buffer → prompt, never clobber. Non-negotiable given A3. |
| Language services | `didChange` in phase 7 so hover and go-to-definition stay honest. **Completion, signature help, rename, code actions and formatting are out of v1.** |
| Out of scope for v1 | Wrap, minimap, split view, editing in a diff, staging an edited hunk, snippets, macros, vim/emacs modes, multi-file find-replace. |

## What already exists — verified

Checked against the code, not assumed.

- **The coordinate system is done, and it is the part editors get wrong for years.**
  `DiffLineText` (`DiffLineText.cs:5,13,30`) keeps raw and tab-expanded text side by side with
  `RawColumn` and `ExpandedColumn` as *distinct types* plus a `TabEdge` for a column landing inside a
  tab's spaces. `DiffText` (`DiffText.cs:24,34,63,84`) counts East-Asian double-width cells, never
  splits a surrogate pair, and gives both "where would a caret go" (`CharIndexAtCell`) and "which
  glyph is under the pointer" (`CharIndexOnCell`). A caret needs exactly this and nothing more.
- **Pixel → text position.** `DiffContentView.HitTestFilePosition` (`:1100`) and `CellAt` (`:1154`)
  already resolve a point to a `(FileLine, RawColumn)` through the gutter/fold/glyph column offsets.
- **Selection.** `DiffSelectionModel` has anchor/focus, word and line spans, whole-span select-all,
  per-row span resolution for the painter, and fold-aware copy that re-inflates hidden text.
  `DiffSelectionController` has drag-past-the-edge auto-scroll, multi-click counting, focus stealing,
  and the "press stays unconsumed so the click still does its normal job" bargain.
- **Reveal machinery.** `ScrollRowIntoView` (`:411`), `ScrollColumnsIntoView` (`:422`) and
  `RevealMarginCells` (`:445`) are already what a caret needs; `VirtualRowListView` exposes
  `EnsureRowVisible`, `TryGetRowRect`, `VisibleRange` and `InvalidateRowHeights` (`:151,224,244,207`).
- **Framework code that is reusable as-is, not as an idea:**
  `ZGF.Gui/TextBoundaries.cs` — grapheme `Snap`/`Next`/`Prev` and `NextWord`/`PrevWord`/`WordAt` over
  `ReadOnlySpan<char>`, with no dependency on the text field. `ZGF.Gui/IScrollScope.cs` —
  `EnsureVisible(RectF)`, which `DiffContentView` should implement. `KeyClaim`
  (`ZGF.Gui.Desktop/Input/KeyClaim.cs`) and `TextInputEvent`. `PreeditText` and `IImeHost`, reached
  through `InputSystem.ImeHost`.
- **`AtomicFile`** (`Infrastructure/AtomicFile.cs`) — tmp-then-rename, already the discipline for
  every store in the app. The *pattern* is reusable; the method is not. `WriteAllText(string, string)`
  (`:10`) calls `File.WriteAllText` — UTF-8, no BOM, no encoding parameter — so it cannot write the
  files module 7 exists to write, and needs a `byte[]` overload. Its `File.Move(overwrite: true)`
  (`:18`) also replaces the inode: on Unix that drops the executable bit, which git tracks, so saving
  a shell script would show as a mode change in the panel next door.
- **`GuiTestHarness`** (`ZGF.Gui.Testing`) — real input dispatch, `RecordingCanvas`, clock control.
  Every phase below is testable headless; `DiffSelectionViewTests.cs` is the shape to copy.

## Corrections — what the code actually says

**A1 — the pipeline is snapshot-shaped, and that is the whole cost of this plan.**
`DiffRowSet.Build` (`:124`) walks every line, allocates a `DiffLineText` per line, and accumulates
`MaxRowCells` over the entire file. (`GutterDigits` is not accumulated in full-file mode — it comes
from the line count up front, `:360`.) `SetRenderState` rebuilds it wholesale and
clears the selection whenever the row count changes (`DiffContentView.cs:242`); `SetFoldState` clears
it unconditionally (`:273`), with a comment conceding that remapping anchors "is more work than
folding". None of that is wrong for a viewer. All of it has to change: the row stream becomes a lazy
projection materialized for the viewport, `MaxRowCells` becomes an incrementally maintained maximum
(it can *shrink*, so a running max is not enough), and anchors replace row indices.

**A2 — `SingleGutter` is not the editability discriminator, and using it would make git blobs editable.**
The obvious gate is wrong. The diff pane has its own whole-file mode — `DiffViewMode {Diff, FullFile}`
(`DiffViewModel.cs:22`), toggled by `ToggleFullFile()` (`:400`) — and `DiffPreviewLoader.BuildFullFile`
(`:104-147`) fills it from `git.GetFileText(..., target.CommitSha, target.BaseSha)` (`:111`), which for
a commit target is **a blob out of object storage**. There is no file on disk to save it to. Since
`DiffView` is constructed twice (`CommitDiffTabsPanel.cs:184`, `DiffWindowRootView.cs:28`) alongside
`FileBrowserPreview.cs:93`, there are three live surfaces where `SingleGutter == true` and two must
never be editable.

**The gate turned out to need neither a host flag nor provenance.** Phase 4 found something better
already in the tree: the gate is simply *whether a document was handed in*, because only a
working-tree read can produce a `FileWriteBack.Reversible` to build one from. `DiffPreviewLoader`
never calls the decoder at all, so a git blob cannot reach an `EditorBuffer` — there is no field to
add to `DiffRenderState`, no flag to check in the controller, and no runtime test that could be
forgotten. The illegal case is unrepresentable rather than merely guarded, which is what this
correction was reaching for and did not find.

**A3 — the working-tree signal is repo-scoped and already re-reads the open file.**
`FileBrowserStore.OnWorkingTreeChanged` (`:88-92`) calls `browser.Invalidate()`, which is
`SyncPreview(force: true)`, which re-reads the file from disk and replaces `_preview.Value`. It is
broadcast from **24 call sites**, and `RepoReconcileService` fires it every 30 seconds on the active
foreground repo (`:20,101,119`) — the method's own comment at `FileBrowserViewModel.cs:443` says "this
runs twice a minute at idle." Today it is harmless because the loader compares the result and drops
an unchanged one (`:726`). The moment a buffer is dirty it is a silent data-loss path.

Two details matter more than the tick. **The message already has a `Path` field** —
`WorkingTreeChangedMessage(Guid RepoId, bool IndexOnly = false, string? Path = null)` — but it is only
ever populated on `IndexOnly: true` broadcasts (`DiffViewModel.cs:551`, `MutationEffects.cs:91`), and
`FileBrowserStore` returns early on `IndexOnly` (`:91`), so the browser never sees a path. And the
dominant trigger is not the tick: `RepoWatcher.cs:90` broadcasts from a `FileSystemWatcher` that on
Windows and macOS is **one recursive root over the whole working tree** (`RepoWatchRoots.cs:54`).

Which means **a save echoes back through the watcher into `Invalidate()`** and asks the user to
reconcile the file they just saved — and because `AtomicFile` writes `path + ".tmp"` beside the target,
each save also flickers a junk file through the tree (`Queue(tree => tree.Refresh())`, `:448`) and
through `git status`. Reconciliation must be self-save-aware. It is a prerequisite, not a nicety.

**A4 — `FileContentLoader` is lossy, deliberately, and cannot round-trip.**
`Decode` (`:139`) strips a UTF-8/UTF-16 BOM and returns a string, discarding which encoding it was.
`SplitLines` (`:150`) drops `\r` and drops the final partial line when truncated, and nothing records
whether the file ended with a newline. Every one of those is correct for display and fatal for save:
re-serializing today's `Lines` would rewrite CRLF files as LF, drop the BOM, and add or remove a
trailing newline — which is a whole-file diff in the panel next door.

Worse, and not fixable by a `FileEncoding` record alone: `Decode`'s fallback is
`Encoding.UTF8.GetString(bytes)` with replacement fallback, so a CP1252, Latin-1 or Shift-JIS file
decodes to U+FFFD at every non-ASCII byte. Re-serializing that through *any* encoding writes the
replacement characters back — **silent whole-file corruption**, not a whitespace diff. So the loader
needs a byte-level round-trip check at load, and editing is refused when it fails, the same way A5
refuses a truncated file. The *display* path is unchanged throughout.

(Separately: `FileContentLoader.SplitLines` does not split on a lone `\r`, while
`DiffPreviewLoader.SplitLines` (`:151`) normalizes `\r` → `\n` first. The two loaders already
disagree about old-Mac line endings — see the phase-2 test note.)

**A5 — a truncated preview is a prefix, not a file.** `MaxTextBytes` is 2 MB
(`FileContentLoader.cs:65`) and `FilePreview.Text.Truncated` already says so. Editing must be refused
outright for those, not merely discouraged — saving a prefix over the original destroys the rest.
There are two more caps behind it: `DiffOptions.TruncationLineCap` is 5000 *lines* on the diff pane's
path (`DiffPreviewLoader.cs:115`), and `TreeSitterSymbolExtractor.MaxFileBytes` — which *was* 1 MB,
so files between 1 and 2 MB were editable but never highlighted, outlined or foldable. Raised to
match `MaxTextBytes` after the first manual perf pass (2026-09-09): a 1.8 MB, 59k-line C# file
typed fine without annotations, so every file the editor opens is now also parsed.

**A6 — tab width is a compile-time constant baked into the text at flatten time.**
`DiffOptions.TabWidth` is a `const int` (`DiffOptions.cs:9`), consumed by `DiffLineText.Of` and
`DiffText.ExpandTabs`. Its neighbours in that file are mutable statics. Per-document settings
(tab width, insert-spaces, EOL) have to become a value carried on the document, which means
`DiffLineText.Of` takes them — a small change touching every construction site.

**A7 — the tree-sitter binding cannot parse incrementally.** `Parser.Parse` in
`external/cs_tree_sitter/TreeSitter/Parser.cs:87,130` has no old-tree overload, and `ts_tree_edit` is
not bound at all. So incremental highlighting is *binding work in a submodule*, not a call we are
failing to make. Incremental parsing is a later, separately scoped job.

The 750 ms `WholeFileBudget` **bounded nothing**, and this is now settled. The parse ran to
completion and *then* the elapsed time was checked (`TreeSitterSyntaxHighlighter.cs:128`); over
budget it logged once and returned null — spending the entire cost and then throwing the answer
away. A large file being edited would flicker between coloured and plain with machine load, and
`TreeSitterSymbolExtractor.cs:92` had the same shape, so the fold chevrons, the usages rows and the
breadcrumb flickered with it.

**Resolved: a result the engine computed is a result it returns.** The elapsed-time check is a
report, not a discard; what bounds the work is `MaxFileBytes`, checked before any of it starts, and
that was always the real limit. Viewport-only highlighting was rejected because the spans are keyed
by line for the whole file and three consumers read them outside the viewport; a cancellation token
was rejected because `Parser.Parse` is one FFI call with no cancellation to hand it, so the token
could only be checked between injections — where the budget already is.

**The other half of the answer is where "keep the last good one" lives, and it is not here.** The
consumer keeps what it has: `EditorRowSet.SetAnnotations` refuses a parse whose revision is not the
document's and leaves the previous one standing. Caching a previous result *inside* the engine
would be the opposite — it would hand back spans describing older text under the new text's stamp,
which is exactly the lie the stamp exists to catch. So the engine is stateless and honest about
failure, and the projection is the thing that remembers.

What is still true is that a debounced whole-file reparse costs a whole-file parse. That is
incremental parsing's problem, and `ts_tree_edit` is still unbound.

**A8 — the LSP client has no `didChange`.** `Identifiers.cs:45-59` has `DidOpen`, `DidClose`, hover,
definition, references and diagnostics — nothing else. A server told `didOpen` and then never told
about an edit keeps answering about the file on disk: hovers point at the wrong symbol, go-to-
definition jumps to the wrong line, and diagnostics decorate lines that have moved. This is not a
missing feature so much as a correctness bug the moment editing exists, which is why phase 7 is in
v1 and completion is not.

Two corrections to the obvious response. First, `didChange` alone would not fix it: the text sent to
`didOpen` is read **off disk**, at `LanguageServerConnection.cs:279` and `LanguageServerStore.cs:380`
(and `UsagesPopup.cs:159` does its own `File.ReadAllLines` for snippets). The source of truth has to
move to the document first. Second, the cheap fix already exists — `PreviewSession.Preview`
(`GitBench.Lsp/Documents/Documents.cs:223-251`) already treats "the file changed" as close-then-reopen
with new content and a fresh `DocumentVersion`, and already refuses truncated content at `:239`. A
debounced close/reopen against the document makes hover honest without touching the protocol layer at
all, which makes `didChange` with incremental sync a *performance* follow-up rather than a v1
correctness requirement.

**A9 — nothing in the tab model knows about unsaved state.** `FileBrowserTab` (`FileBrowserTabs.cs:20-41`)
has `Path`, `Name`, `Transient` and `TopLine`. Dirty state, the dot in the tab strip, close
confirmation and "transient tabs are replaced on the next click" all interact — a transient tab must
be pinned the instant it is edited, or typing into a preview and clicking another file discards it.

**A10 — the controller-ordering plan does not survive contact with `InputSystem`, and it blocks
typing.** Found building phase 4. Module 6 says `EditorController` claims its keys "before
`DiffSelectionController` sees them". That is not achievable: `InputSystem.DispatchKeyboardKeyEvent`
(`:146-160`) runs `_focusedComponent` in the **bubbling** pass *first* and returns if it consumed —
only then does the capture pass over `_focusQueue` run. So a capture-registered controller can never
beat the focused one, and `DiffSelectionController` is the focused one: it calls `StealFocus` on press
(`:194`). Worse, `_focusQueue` is rebuilt from the **hovered** component, so a non-focused controller
only sees keys while the pointer is over the body — click to place a caret, move the mouse to the file
tree, press Down, and the *tree* moves.

Phase 4 survives this only because the two controllers claim disjoint keys (arrows/Home/End/Page
versus Ctrl+C/Ctrl+A/Escape). **Phase 5 cannot**: Ctrl+C, Ctrl+A, Ctrl+V and Ctrl+Z collide head-on,
and plain-text keys have to be claimed as text or every keystroke fires an app binding. So the fix is
a prerequisite of typing, not a cleanup after it: **one focus holder per surface.** On an editable
host `EditorController` takes focus and owns every key, delegating copy and select-all to the same
`DiffSelectionModel` the mouse gestures drive; on a read-only host nothing changes and
`DiffSelectionController` keeps focus exactly as it does today. This is also the fix for
`DiffSelectionController.OnFocusLost` clearing the selection when the pointer leaves — correct for a
transient drag-selection, wrong once there is a caret worth coming back to.

## What the framework's text field teaches

`ZGF.Gui.Desktop/Components/TextInput` is a 512-char `char[]` with proportional-font relayout. None of
its storage or measurement survives at document scale. Its *policy* is the most valuable prior art in
the repo and is ported deliberately, not rediscovered.

| Take | Where it is | Why |
|---|---|---|
| Text arrives only via `TextInputEvent` | `Input/TextInputEvent.cs` | Key events carry physical, layout-independent positions. Decoding them into characters hard-codes a US layout and makes Cyrillic, accented Latin and every non-ASCII script untypable. |
| `ConsumeAsText`, not `Consume`, for typing keys | `BaseTextInputKbmController.cs:268,280` | An unconsumed Space bubbles to the app's single-key bindings — the controller's own comment cites the review loop's Space-folds-file firing on every keystroke, and that binding is real (`ReviewKeyController.cs:32,58,59`). `AppKeybindController` is all chords, so the exposure is narrower than "everywhere" — but `DiffSelectionController` already claims Ctrl+C and Ctrl+A hover-scoped on this very surface. |
| Read-only *declines* rather than consumes | same, `IsReadOnly` guards | Keeps the diff pane a selectable surface instead of "a keyboard black hole" that swallows Ctrl+V and Ctrl+Z. |
| Composition held outside the buffer | `TextInputView.cs:232,270` | `_composed` is the buffer with the preedit spliced in at the caret, and only draw/measure read it. Two invariants: half-typed pinyin never becomes the value, and the span that is measured is the span that is drawn. |
| Sealed IME teardown with a virtual hook | `BaseTextInputKbmController.cs:93,109` | So a subclass cannot forget it. The failure it guards — "IME enabled" and "field is editing" drifting apart, leaving a caret that accepts nothing — is a day-long debug. |
| Undo coalescing policy | `TextInputView.cs:158,880,860` | `EditKind {None, Insert, Delete, Boundary}`; merge only when same kind, not selecting, and `caret == lastEditCaret`. Paste, newline, word-delete and selection-replace are always `Boundary`. Any recorded edit clears redo. Undo restores caret *and* selection, and resets the run so the next edit cannot merge into a snapshot just popped. |
| Sticky goal column cleared by everything else | `TextInputView.cs:144` and every `_goalColumnX = -1f` reset through the motion and edit methods | One place sets it, every horizontal move and every edit clears it. Ours is a goal *cell* (integer) rather than a pixel x, because the grid is monospace. |
| Control characters never reach the buffer | `BaseTextInputKbmController.cs:444` | `Rune.IsControl` on the text-input path. Note it also excludes `\t`, so Tab has to arrive as a key gesture — and `EditorController` is not a `BaseTextInputKbmController` subclass, so this one is re-implemented rather than inherited. |
| One funnel that snaps the caret | `TextInputView.cs:1071` | "so a pixel position that lands inside a cluster is snapped once rather than at each call site." Extend `CharIndexAtCell` with `TextBoundaries.Snap`. |
| One source of line geometry | `TextInputView.cs:458` | Drawing, caret rect, selection rects, clicks and Up/Down all read the same line list "so they cannot disagree about where a line ends". For us that is the row projection, and the caret may never compute geometry independently. |
| Collapse-to-the-correct-end on arrows | `TextInputView.cs:914,936` | Left with a live selection collapses to its start, not one character left of the caret. |
| The platform modifier table | `BaseTextInputKbmController.cs:308,377` | Cmd on macOS for shortcuts; **Alt** on macOS for word-jump, because Cmd is line start/end. Ctrl+Y as Windows redo. Ctrl/Cmd+Enter deliberately left unclaimed for the owner's submit. |
| `IScrollScope.EnsureVisible` after every caret-moving interaction | `:29,161` | One `RevealCaret()` from typing, arrows, clicks, drag and paste, guarded on "is editing" so a blur never scrolls back to the field being left. |

**And what not to carry over.** Per-character memmove (`InsertChar`/`DeleteRange`, `:399,424`) is
O(n) per keystroke. Full-text undo snapshots — the file's own comment concedes "a diff journal would
only earn its complexity on very large buffers", and that is exactly where we are. `InvalidateLines`
throwing away all geometry on any change. Proportional-font prefix measurement: `FindIndexClosestToX`
(`:1036`) re-measures the prefix per cluster and `UpdateScrollOffset` (`:474`) measures the whole
string every draw — our monospace cell arithmetic is strictly better and the caret must not drift
back into pixel measurement. And `SetText` silently dropping undo history as "a new document": an
external reload has to reconcile, not erase.

## Modules

All new code lives in `Features/Editor`. (An earlier draft carved out the row projection to
`Features/Diff` "because both the editor and the read-only surfaces consume it" — that was the
rejected shared-incremental design, and nothing but the editor consumes `EditorRowSet`.) The one
exception already landed elsewhere: the text primitives phase 0 needed — `TextLines`, `FileEncoding`,
`FileTextDecoder` — are in `GitBench.Infrastructure`, a neutral leaf, so that neither `Diff` nor
`FileBrowser` has to depend on the other to reach them.

**1. `TextDocument` — the buffer.**
Piece table over the immutable original plus an add buffer, with a line-start index maintained per
edit. Public surface is small and total: `LineCount`, `Line(int)`, `Slice(TextRange)`,
`Apply(TextEdit) → TextEdit` (returning the inverse), and the `TextPosition`/`TextRange` value types.
Positions are `(line, RawColumn)` — the file's own characters — so nothing above has to know about
tab expansion. No canvas, no view, no async: this is the one module that is a pure data structure and
it should be the most heavily tested thing in the feature.

**2. `Anchor` — positions that survive edits.**
Owned by the document, shifted by `Apply`. Carries a bias (does a position at the edit's start stay
put or move with the insertion), which is what makes a caret at the edit point behave and a
diagnostic before it stay. Everything that today holds a `RowIndex` or a `FileLine` and is invalidated
by a reshape becomes an anchor: search hits, diagnostics. **Not the caret or the selection** — those
are remapped synchronously per edit through the same pure shift function
(`TextPosition Shift(TextEdit, TextPosition, AnchorBias)`), which keeps the hot path free of anchor
lifetimes; anchor *objects* are for positions nobody is going to remember to remap. **Not folds** —
`FoldState(string Path, IReadOnlySet<string> Collapsed)` (`Folding.cs:23`) keys collapsed regions by
outline path, and `FoldPlan.Build` (`DiffRowSet.cs:436`) re-derives the line ranges from the outline
on every flatten. Making them anchors would add a second source of truth for nothing.

**3. `EditJournal` — undo.**
Stack of transactions, each an inverse-edit list plus the selection before and after. The coalescing
predicate is the framework's, ported: same `EditKind`, no selection, caret contiguous. `MaxUndoDepth`
stays a cap. Grouping is explicit (`journal.Transaction(...)`) so a multi-caret edit is one step.

**4. `EditSession` — the edit vocabulary.**
Holds the goal cell and the document-level operations: insert text, delete backward/forward by
cluster and by word, newline with auto-indent, indent/outdent a selection, toggle comment, move caret
by cluster/word/line/page/document.

It does **not** own a selection. Each operation takes the current `SelectionRange` and returns the
new one, and expresses its change as `IReadOnlyList<TextEdit>` applied in a single journal
transaction — even when the list has one element, which it always does in v1. That list is the entire
multi-caret seam: adding carets later means passing N ranges and concatenating N edit lists into the
same transaction, with no operation rewritten. Storing a list of ranges today would instead add a
representable illegal state (overlapping ranges) to the type every operation is written against,
while leaving the genuinely hard parts — overlap merging after an edit shifts two carets together,
per-caret goal cells, multi-range paste semantics, undo granularity across carets — entirely unbuilt.

**5. `EditorRowSet` — the editor's row projection.**
`DiffRowSet` is **not touched**. `EditorRowSet` takes a `TextDocument` and produces the same
`DiffRow` stream, so `DiffRowPainter`, `DiffRowMetrics` and the whole draw path are shared unchanged —
which is what `DiffRow` being a type is for. The duplication is the ~35-line `FlattenFullFile` loop;
the alternative was a dependency edge from the editor's mutation model into three read-only surfaces
(the diff pane twice over and the review list), and Rule 2 is explicit that more files at the same
edge count is neutral where more edges is not. `DiffRowSet` keeps serving three consumers with one
shape; `EditorRowSet` serves one consumer with the shape that consumer needs.

The plan it folds by is the *viewer's* — `FoldPlan` moved out of `DiffRowSet` into `Folding.cs` and
is now the one answer both flatteners ask for which declarations fold, where a chip lands, what a
collapse swallowed and which declarations get a usages row. That is the part of the two flattens
with the most to disagree about, and it is the part that is no longer duplicated at all.

It cannot be a lazy wrapper, because several consumers are not viewport-bounded: `DiffContentView`
sets `_list.ItemCount` from `Rows.Count` (`:246,275`), so the total must be exact and cheap;
`RowForNewLine` (`DiffRowSet.cs:86`) and `RowNearestNewLine` (`:101`) are linear scans used by
scroll-to-line, search reveal and go-to-definition; `_hiddenAfter` is a dictionary built during
flatten and read by fold-aware copy; and `BuildCopyText` takes the whole `IReadOnlyList<DiffRow>`
(`DiffTextSelection.cs:125`), so select-all copies the file. So: rows materialized on demand over a
stable index space, with O(1) total count and O(log n) line→row. `MaxRowCells` is maintained
incrementally and has two inputs besides line width — usage-lens rows (`DiffRowSet.cs:372`) and fold
chips (`:385`), both derived from the async outline rather than from the edit — so it tracks more
than the document does.

**6. `EditorController` — input, over one selection model.**
A `KeyboardMouseController` over `DiffContentView`. Owns `OnTextInput`, the keymap (with the platform
modifier table copied), control-character filtering, and an `ImeSession` — see Open questions on
whether that is extracted into the framework. Calls `EnsureVisible` after every caret-moving
interaction.

There is exactly one selection: `DiffSelectionModel`. It stays what the painter reads through
`TryRowSpan` (`DiffTextSelection.cs:95`) and what Ctrl+C and Ctrl+A act on
(`DiffSelectionController.cs:275,278,296`), and `DiffSelectionController` keeps every mouse gesture it
already has right. `EditorController` adds keyboard-driven mutation of that same model, routed through
`EditSession`; it never holds a second one.

**`DiffTextPos` stays `(RowIndex, ExpandedColumn)`.** The first draft of this plan moved it to
document coordinates; that does not survive contact with a hunk diff, where a removed line has no
after-side `FileLine` at all, so "the document" is ambiguous. Row coordinates are also what the two
hot paths actually want — the painter needs an ordered comparison per visible row, and `RowKey`s
cannot be ordered without consulting the row set.

What the model gains instead is **`Remap(Func<DiffTextPos, DiffTextPos?>)`**, called by the surface
whenever the row stream is rebuilt, replacing today's unconditional `Clear()`. Two suppliers:

- **Read-only surfaces** map by *row identity* — **built, phase 3**. Not the `(old, new)` pair this
  plan first proposed: one side is enough, and naming a row by both would give a removed line and the
  addition replacing it half a name each, and would break identity across the hunk↔whole-file toggle.
  So `DiffRowKey` is a tagged one-of — after-side line where a row has one, before-side where it does
  not — and a row the git layer numbered on neither side is unnameable *by construction* rather than
  by comment. Chrome (banners, bars, tears, lens rows) names no line but a drag can end on it, so
  `DiffRowAnchor(Line, RowsBelow)` hangs it off the text row above and clamps to that run of chrome:
  walking past the run's end is what would slide an endpoint onto a text row nobody selected.
  A fold toggle, a gap expansion, the async highlight re-emit and the mode toggle now all remap.
- **The editor** maps through the *edit*, not through row identity — a line number is not stable
  across an insertion. That mapping is the same pure position-shift the anchors use
  (`TextPosition Shift(TextEdit, TextPosition, AnchorBias)`), so it is one function with two callers
  rather than a second implementation.

Anchors as *objects* are then only for positions that must survive many edits without anyone
remapping them — diagnostics and search hits. The caret and selection are remapped synchronously per
edit, which keeps the hot path free of anchor lifetime and leak questions entirely.

Controller ordering is settled here too: `EditorController` claims Ctrl+C/X/V/A/Z/Y on an editable
host *before* `DiffSelectionController` sees them (which already steals focus at `:194` and claims
Ctrl+C/Ctrl+A), and declines all of them on a read-only host so they keep bubbling. Arrows are free —
`VirtualRowListController` is wheel-only.

**7. `FileEncoding` + `DocumentWriter` — save.**
`FileEncoding` is `(Encoding, hasBom, Eol, endsWithNewline, roundTripped)`, captured by
`FileContentLoader` and returned alongside the text. Save serializes the document through it and
writes via a new `AtomicFile` byte overload (the existing string one is UTF-8-only — see
*What already exists*). Refuses when the preview was truncated (A5) or did not round-trip (A4), and
preserves the file mode across the atomic replace.

**Conflict resolution rides here.** A conflicted file opened in the Files pane is its raw text,
markers and all, and editing it is ordinary editing — but saving it leaves the file unmerged. So
saving a file git reports as conflicted offers to mark it resolved, calling the `MarkResolved` that
already exists (`GitService`, used by `ConflictTools.cs:337` and after `TakeBoth` at
`GitService.cs:1396`). Offered, not automatic: a save mid-edit is not a claim that the conflict is
finished. This is the whole of "resolve a conflict" in v1 — no three-way merge UI, no `Conflict`
render state changes in the diff pane.

**8. `IDocumentStore` — buffers that outlive the view.**
Per repo, keyed by absolute path, following the `FileBrowserStore` shape (`:54-92`). A document is
created on first edit, not on open — reading a file must stay free. Holds the document, journal,
dirty flag and `FileEncoding`; survives tab switches, mode switches and `KeepAlive` remounts. This is
also the thing that answers "is anything unsaved" for close confirmation and for module 9.

**9. Reconciliation — the git-shaped half.**
`WorkingTreeChangedMessage` already has a `Path` field (A3); the work is populating it on the flows
that can name a file, and `FileBrowserStore` reading it. But most of the dangerous flows — branch
switch, rebase, stash apply, reset — legitimately cannot name one file, so the primary mechanism is a
**per-open-document mtime/size check** on any invalidate, not a path-level signal. Clean buffers
reload silently; dirty buffers prompt; **the app's own save is recognised and never prompts** (A3).
Separately, the destructive git flows — discard, checkout through `IRepoHeadStore`, stash apply, and
the conflict resolvers that write files directly (`ConflictTools.cs:330`, `GitService.cs:1394`) — ask
`IDocumentStore` for unsaved buffers under their paths before they run.

The other half is the view binding. `FileBrowserPreview.cs:103-109` is a plain
`content.Bind(browser.Preview, …)`, and `DiffContentView` is a pure sink of whatever
`FilePreview.Text` the loader last produced — so an `IDocumentStore` beside the view model does not
by itself stop the next emission from replacing the buffer. `FilePreview` is already a sum type
(`FileContentLoader.cs:25-48`); giving it an edited case is the Rule-1-shaped answer.

## Phases

Each phase names its tests, per the discipline in `terminal.md` and `file-browser.md`.

**Phases 5–7 are one release, not three.** A dirty buffer without reconciliation is not shippable
even behind a flag, given A3. Phases 1–4 are independently mergeable; 5, 6 and 7 ship together.

**Every phase is built.** 5–7 shipped as one release, and `DiffOptions.EditableFilesPane` came
down with them: the Files pane is editable for every text file the document store will open, and
the only gate left is the one phase 4 found — whether a `FileWriteBack.Reversible` exists to build
a buffer from. What this plan still owes is listed under Open questions and Risks; everything not
struck through there is still true.

0. **Unify line splitting — BUILT.** `FileContentLoader.SplitLines` splits on `\n` only; `DiffPreviewLoader.SplitLines`
   normalizes lone `\r` first (A4). Until they agree, phase 2's headline test compares two different
   answers. Small, and it has to come first. Test: a file with lone CR, CRLF and LF splits identically
   through both paths.
1. **Document, anchors, journal — BUILT.** Modules 1–3, 58 tests. The buffer is a treap of pieces
   carrying subtree (chars, breaks) sums over the immutable original plus an append-only add buffer,
   so lookup, offset↔position and edits are all O(log pieces): an insert at line 10 000 of a 20 000-line
   document leaves 3 pieces and appends 1 character. Undo/redo is symmetric around one helper —
   applying a list returns the list that undoes it — so a redo step is just what an undo handed back.

   Two findings worth carrying forward. **A CRLF pair can straddle two pieces** (type `\n` at the
   start of a line whose predecessor ends in a lone `\r`) and would then be counted as two breaks in
   a document that has one; `Apply` widens such an edit so the pair lands in one piece, which means
   the returned inverse describes the *applied* edit, not literally the range the caller passed. And
   **`TextDocument` is not thread-safe** — it belongs to the UI thread, and `Revision` is the stamp an
   off-thread annotation must be checked against, which is the mechanism the stale-annotation risk
   below has been waiting for.
2. **`EditorRowSet` — BUILT.** Module 5, 25 tests. The binding test is corpus-wide equivalence
   against `DiffRowSet.Build`, and it bites: collapsing tabs to one space in the projection fails it
   with the offending line of the offending file named.

   `MaxRowCells` is a **multiset keyed by width**, not by line — a running max cannot shrink, a heap
   needs stable per-line identities, and lines renumber on every insert, so keying by width sidesteps
   the identity problem entirely. Re-projection consumes the inverse edit `TextDocument.Apply` hands
   back, because that is the only description of the change in the coordinates the document now uses;
   two guards throw if it is called out of order or handed the forward edit. Rows above an edit are
   returned *by reference* and the tail is invalidated lazily, so only rows something asks for get
   rebuilt.

   Line→row is O(1) rather than the O(log n) this plan budgeted: with no folds and no lens rows, a row
   index *is* a line index. That is the debt to repay when they come back.

   **They came back, and it is still O(1) both ways.** Two `int[]`s — line→row and row→line — are
   built whenever the fold plan changes and are simply absent while it is empty, which is when the
   identity still holds and nothing is allocated at all. So a file with nothing to fold costs
   exactly what it did, whatever the edit; a file with foldable declarations costs one pass over its
   lines per fold toggle, per reparse, and per edit that changes the line count, which is the same
   O(lines) the per-line list already spends memmoving itself. Rebuilding rather than patching is
   deliberate: the alternative is a second incremental structure to keep in step with the first, and
   the file it runs over is capped at 2 MB by the outline extractor (the same cap the editor opens
   under), because a file with no outline has no folds to index.

   The one honest cost left is that the pass rebuilds the width multiset too, so **typing a newline
   into a foldable file is O(lines) of dictionary work** where typing a character is O(1). A shift
   moves which lines the widths belong to without changing the widths themselves, so this is
   avoidable — but only by patching what is currently derived, which is the shape the fold-toggle
   bug above came from. Left until someone measures it, per Rule 2.

   **Two divergences from the viewer, both deliberate and both pinned by tests.** The trailing empty
   row (see the Decisions table), and — a second-order consequence this plan missed — **gutter digits**:
   a 999-line file ending in a newline is a 1000-line document, so the editor draws a row numbered 1000
   and reserves four digits where the viewer reserves three. Correct, and exactly the kind of thing a
   loosely written equivalence test papers over.
3. **Selection remapping — BUILT.** `DiffSelectionModel.Remap` plus `DiffRowKey`/`DiffRowAnchor` and
   `DiffRowSet.MapTo`. Note the clear sites were not the four this plan named: `ReviewDiffList.cs:348,368`
   are different-*document* clears and correctly stay clears; the actual reflow sites were
   `OnSectionRender` and `SetFolded`. Folding a review card turned out not to rebuild its row set at
   all, so its clear was gratuitous — removing it means marking a file Viewed no longer drops a
   selection inside it, which is a behaviour change consistent with the app's existing
   "text the reader could not see is still text they selected" rule.
   28 new tests, 484 in the affected suites green.
   **Known gap:** the review surface has no view-level test coverage, because constructing
   `ReviewDiffListView` needs `IReviewSurfaceModel` plus a git-backed `CommitDetailsViewModel` and no
   test in the repo builds either. Its logic is covered where it lives (`MapTo` over the exact row-set
   transitions, plus the scope guard), but the wiring is not.
4. **Caret and motion — BUILT** (25 tests; 4038 green). The seam is `IDiffRowSource` plus a nullable
   `IDiffHunkRows`, both declared at the consumer: `DiffRowSet` implements both, `EditorRowSet`
   returns null for hunks, so "which hunk owns this row" is a question you structurally cannot ask a
   document rather than one answered with `-1`. `EditorBuffer` owns the single `DiffTextPos` ↔
   `TextPosition` conversion and the single caret-snapping funnel.

   **The width model: a combining mark is 0 cells wide**, because it draws on top of its base rather
   than beside it. Three places in the codebase already agreed and the old code did not — `ICanvas`
   documents "a combining mark has zero advance", `BidiShapingTests` asserts the caret x is unchanged
   across one, and the terminal folds marks into their base cell. Making `DiffText` agree is what
   removes the conflict the plan predicted between cell columns and grapheme boundaries: a mark is
   never offered as a landing site, so `TextBoundaries.Snap` can no longer refuse a column the painter
   drew at. An ASCII fast path made the shared per-line path cheaper than before.

   **Shipped behind `DiffOptions.EditableFilesPane`, default off** — *the flag is gone since 5–7
   shipped; see the note above the list.* It stayed off through phase 4
   because the Files pane is the one surface with *both* folding and the usage lens and the editable
   projection had neither. It now has both, and the reason to keep the flag down is the document
   store: there is nowhere to put what is typed, so the twice-a-minute reconcile tick replaces the
   buffer. Everything is wired and tested through the view regardless.

   *(Original phase-4 scope, for reference:)* Caret rendering (blink, and the caret rect for the IME), click-to-place,
   arrows, word jump, Home/End/Page, goal-cell preservation, `EnsureVisible`. No mutation yet.
   Tests via `GuiTestHarness` with real key dispatch (`SendText`, `Compose`, `Advance`): goal cell
   survives a pass through a short line; caret never lands inside a tab's expansion; caret column is
   correct on a line with tabs and on a CJK line.

   **Sub-task, not a test line: decide the cluster/cell conflict.** `DiffText.StepCells` (`:100`)
   advances by *code point* and gives a combining mark its own cell; `TextBoundaries.Snap` snaps to
   *grapheme clusters*. Bolting Snap onto `CharIndexAtCell` makes the caret refuse a column the
   painter is drawing a glyph at. This changes the width model, which the review window's painter
   shares, so it needs a stated answer for "how wide is a combining mark" before phase 5.
5. **Typing — BUILT** (`EditorTypingViewTests`, `EditorSessionEditTests`). `OnTextInput`, backspace/delete, Enter with auto-indent, Tab, paste of multi-line
   text, cut. Dirty state, tab-strip dot, transient-tab pinning (A9), close confirmation. Tests:
   a plain-text key is claimed as text and does not fire an app binding; a control character never
   reaches the buffer; paste is one undo step; a read-only host refuses every one of these and
   leaves the keys unconsumed.

   **Folds, the usages rows and annotation versioning landed here**, which is what the flag was
   waiting on and is written up under A7, the Risks and the settled open question above. Three
   things worth carrying forward that this plan did not anticipate:

   - **`Reproject` may not throw, and used to.** It runs from inside the transaction that made the
     edit — after the document has moved and before the reversal that undoes it has been recorded —
     so its three guards left a document nothing could put back and a pane that failed on every
     subsequent keystroke. It now rebuilds itself from the document, which is always possible
     because that is all it ever was, and counts the fact so a test can insist a normal edit never
     costs one. The middle guard was dead code besides: `undo.Range.End` is computed against the new
     document, so every bound it checked was already implied.
   - **A default argument was the whole of the bug in `SetRenderState(state, document = null)`.** One
     call site that forgot the second argument would silently clear the caret and discard the open
     document. It is required now, and the two fields it drove — the row source and the buffer — are
     one `DiffBody` sum type, so a caret outliving the file it was placed in is no longer a state the
     view can be in.
   - **`MapTo` existed twice**, as a default interface method and as a public method on `DiffRowSet`
     with the same signature and no `override`, `new` or warning — so which one ran depended on the
     static type of the variable. Now one extension method, which cannot be shadowed.
6. **IME, save, conflicts — BUILT** (`ImeSession`, `DocumentWriter`, `DocumentSaves`,
   `MarkResolvedDialog`; tests in `EditorImeSessionTests`, `EditorCompositionViewTests`,
   `DocumentWriterTests`, `DocumentSaveConflictTests`). Composition overlay and the sealed teardown; `FileEncoding` capture and
   atomic save; refusal on truncated and non-round-tripping files; the `MarkResolved` offer on saving
   a conflicted file. Tests: the document is unchanged during composition and the observable text
   never sees the preedit; blur discards rather than commits; saving a conflicted file offers to mark
   it resolved and does not do so unasked; a CRLF file with a BOM and no trailing newline
   round-trips byte-identical through load → no-op edit → save (this fails until `AtomicFile` grows
   its byte overload, which is the point); **and a CP1252 file with an accented character either
   round-trips or is refused** — the UTF-8 fixture cannot catch the corruption case in A4.

   Every test on that list exists and passes, including the CP1252 refusal and a UTF-16 round-trip
   the list did not ask for. Two things the plan got slightly wrong, both cheaper than predicted:

   - **The round-trip did not need the writer to re-read the file.** `FileText` — the type
     `FilePreview.Text.Lines` now is — carries the decoded text whole beside the lines it splits
     into, so the document is seeded from the file's own characters, terminators and all, and a
     file whose line endings disagree keeps every one of them. `Options.EolText` only decides what a
     *typed* newline becomes. The final newline is the document's, not the one the file was found
     with: a file that ended in one keeps it and one that did not does not gain one.
   - **`AtomicFile.WriteAllBytes` is the byte overload**, going through the same tmp-then-rename as
     every other writer in the app, which is what makes the staging-file test in phase 7 the same
     test for every writer. A file that was executable stays executable across a save.
7. **Reconciliation and the language server — BUILT** (`DocumentStore`, `FileStamp`,
   `UnsavedEditsGuard`, `UnsavedDocumentsExitGate`; tests in `FileBrowserPreviewRefreshTests`,
   `DocumentStoreTests`, `UnsavedEditsGuardTests`, `UnsavedEditsGuardedFlowsTests`,
   `LanguageServerFileTextTests`). Module 9, plus moving the server's source of truth off
   disk and onto the document via `PreviewSession`'s existing close/reopen (A8). Tests, extending
   `FileBrowserPreviewRefreshTests.cs`, which already drives a real temp directory: a repo-wide
   invalidate does not touch a dirty buffer; a clean buffer reloads; **saving does not prompt about
   the file just saved**; the `.tmp` from an atomic save never appears in the tree; discard-with-
   unsaved-changes prompts; a hover after three edits resolves against the edited text. That last one
   needs a fake server across `GitBench.Lsp.Tests` — the most expensive test in the plan, and worth
   knowing that up front.

   All of it exists and passes. The shape that landed, for the parts the plan left open:

   - **The mtime/size check is `FileStamp`, kept per open document.** `Reconcile(path, expected,
     found)` settles what a fresh look at the file found against what the document last knew, and a
     session re-stamped since `expected` was read answers *unchanged* — which is how the app's own
     save is recognised: `MarkSaved` re-stamps in the same UI turn the write finished, so the
     watcher echo that follows finds nothing to ask about. Typing again right after a save still does
     not ask. The same external change is asked about once, not on every tick.
   - **`UnsavedEditsGuard` is consent only.** It closes no document and writes no file; it asks
     before an operation rewrites files the reader has typed into, by exact paths or by whole
     working tree, and the destructive flows — discard, checkout through `IRepoHeadStore`, stash,
     the conflict resolvers — run through it. App quit is the same question through
     `UnsavedDocumentsExitGate`, which wraps the inner exit gate rather than replacing it.
   - **The server reads the document, not the disk, through one seam.** A file nobody has edited is
     read once, byte for byte, however many questions are asked; a file being typed into is
     re-opened on the server after a half-second debounce, so a burst of edits is one reopen and
     edits either side of a pause are two. A file edited past the size cut-off is closed rather than
     sent truncated. Usages snippets and hover quote the edited line.
   - **Diagnostics are stamped by whether the read is current**, so squiggles go away while there
     are unsaved edits and come back after the file is saved and re-read, without the server having
     to say anything new.
   - **Highlighting and the outline update while typing**, which A7 ruled out for v1: tree-sitter
     trees and the normalized UTF-8 buffer survive across edits, edits queue on a worker, publication
     is debounced, stale results are dropped by revision, and an incremental tree that comes back
     invalid falls back to a full parse. The revision discipline under Risks is what made this safe
     to add.
   - **Language server conversations can be traced** — protocol messages and process faults — behind
     an opt-in launch setting, so a server failure can be investigated from a persisted trace rather
     than reproduced.

## Open questions

Four design calls that were open in the first draft are now settled in the Decisions table and the
modules: one selection model in document coordinates, no multi-caret in v1 (but `IReadOnlyList<TextEdit>`
from day one), a separate `EditorRowSet` rather than an incremental `DiffRowSet`, and conflict
resolution kept in scope with an explicit `MarkResolved` step. What remains open:

- **~~Does `ImeSession` get extracted into the framework, or copied?~~ — copied, for now.**
  `Features/Editor/ImeSession.cs` owns the editor's composition state as its own state machine
  (off, ready, composing) over the framework's `PreeditText` and `IImeHost`; the preedit lives there
  and never reaches the document. The sealed-teardown discipline in `BaseTextInputKbmController` is
  still the thing that gets re-implemented and re-broken, so a shared helper in the submodule
  remains the right long-term answer — it was not worth blocking the release on a framework review
  path. Two implementations to keep in step until then.
- **~~Where does the caret's row live during a fold?~~ — settled.** Nowhere: a collapsed region has
  no rows, so a caret cannot be inside one. The draft answer was "editing a line inside a collapsed
  range expands it first", which is right and too narrow — it names one of the four ways a caret
  reaches a hidden line, the others being vertical motion, a jump to a definition or a search hit,
  and an undo restoring text inside the fold. So the rule is stated once, at the one funnel all four
  pass through: **`EditorBuffer.Write` opens whatever collapsed declaration hides the caret's line
  before the caret is written.** The caret end only — a selection may legitimately run across a fold
  (that is what `HiddenText` re-inflation on copy is for), and a select-all that sprang every fold in
  the file open would be obeying a gesture nobody made. The path that opened is reported back through
  `FoldExpanded` → `OnToggleFold` → the view model, so the fold set the reader owns stays the one
  source of truth rather than the projection quietly disagreeing with it.
- **~~Does the assistant get an edit tool?~~ — refused while dirty.** `ConflictTools` still writes
  the file itself, but `resolve_conflict` first asks the document store, on the UI thread, whether
  that path has edits not on disk, and returns a tool error telling the model to have the user save
  or discard first. It does not route through `IDocumentStore`, so the assistant still cannot edit
  what the reader is typing into — a lost-update bug is now a refusal, which is the smaller answer.
  An assistant that edits the open document is a later project.
- **RTL.** The viewer's cell grid is LTR by construction and the caret arithmetic assumes it. A code
  file is overwhelmingly LTR-directional, but a string literal in Arabic is not, and the framework
  field already made the "arrows move visually" decision (`IsContentRtl`). This plan is silent on it
  and should not stay silent past v1.
- **Do edits belong in the diff pane eventually?** The payoff is real — edit a hunk, then stage it;
  live change bars against HEAD as you type; conflict resolution in place. It is also the thing that
  would make this GitBench's editor rather than a worse VS Code. Explicitly deferred, not dismissed.

## Risks

- **~~Stale annotations after an edit~~ — built.** `Highlight` and `Outline` ride on
  `FilePreview.Text` and are computed off-thread (`FileContentLoader.cs:93-95`), keyed by **line
  number**, so a parse started at edit N and landing after edit N+3 paints, folds and counts by
  numbers that have moved. The stamp is now a type rather than a check: `DocumentRevision` can only
  be obtained from a document, `DocumentSnapshot` pairs the text with the revision it *is*, and
  `Revised<T>` — whose only constructor takes that revision — hands its value over only through
  `TryReadFor(document)`. There is no accessor that yields the value without the document to check
  it against, so applying a stale annotation is not something a caller can write. This is also what
  let the editable projection grow folds and usages rows, which the first draft of this plan ruled
  out of v1 for exactly this reason.

  Between an edit and the parse that answers for it the projection **keeps the outline it has,
  shifted by the lines the edit moved** — a declaration below the edit moves whole, one the edit
  landed inside grows or shrinks — rather than springing every fold in the file open a keystroke
  before the reparse closes them again. The *outline* and not the plan it produces, which is a
  distinction with a bug behind it: a plan shifted into place is right until the next fold toggle
  rebuilds it from an outline still describing the file as parsed, and the folds then land wherever
  the reader has typed since. That is a failure one gesture removed from its cause and it looks like
  a folding bug. Everything is therefore derived, never patched — the only thing carried is the
  outline, and it is dropped outright on the one path where what moved is unknown, which is the
  rebuild `Reproject` falls back to.

  The find bar's hits and the server's diagnostics are stamped the same way but with the *opening*
  revision, because that is what they honestly describe: both are computed from the lines the reader
  was handed, so one keystroke is enough to make their line numbers wrong. They stop painting until
  the search re-runs and the server answers again. Read-only surfaces are untouched — they have no
  document, so nothing about them can have moved.
- **Two flattening implementations can drift.** `EditorRowSet` buys a clean dependency graph at the
  cost of a second copy of the full-file flatten. The only thing holding them together is phase 2's
  equivalence test, so that test has to run over a corpus and has to be maintained as a contract —
  not quietly narrowed to one fixture the day it goes red.

  Two things reduced the surface rather than merely watching it. `FoldPlan` — which declarations
  fold, where a chip lands, which get a usages row, what a collapse swallowed — was the part of the
  flatten with the most to disagree about, and it is now one class both flatteners call rather than
  two walks of the same outline. And the equivalence test grew a second pass that folds every
  declaration of thirty corpus files shut, one at a time, with the usages rows on, comparing the row
  stream, the widths, the anchors, the line↔row mapping and the re-inflated hidden text. What is
  left to drift is the per-line loop, which is what the first pass already covers.
- **Moving the selection to document coordinates touches the read-only surfaces.** It is the one
  change here that can regress the diff pane and the review window, which is why it is phase 3, alone,
  before any editing exists to blame.
- **Keystroke latency is the thing users judge — and it has not been measured.** Every phase is
  built and no benchmark or recorded measurement exists for this; it is the one item on this plan's
  own checklist that was skipped rather than done. A document that re-highlights on a debounce and
  re-projects only the viewport should be fine, but nothing here establishes that, and A7 says the
  reparse cost is unbounded. Measure on a 10k-line C# file before phase 5 is called done, not after —
  and specifically measure the one honest O(n) left in the projection: inserting or deleting a line
  memmoves `EditorRowSet`'s per-line list (pointers and ints, no re-measure, no row rebuild), which is
  ~1.6 MB moved per newline typed in a 100k-line file. A balanced sequence fixes it and is Rule 2's
  "structure you haven't been forced into" until someone measures.
- **~~`FilePreview.Text` carries `Lines`, not the decoded text~~ — fixed in phase 6.** `Lines` is
  now a `FileText`, which carries the text exactly as it decoded beside the lines it splits into, so
  the document is seeded from the file's own characters and a file with mixed endings round-trips
  byte-identical. `DocumentWriterTests` pins it.
- **~~`EditSession.ColumnAtCell` conflates cell space with expanded-character space on tabbed lines~~
  — fixed.** It now asks `DiffText.CharIndexAtCell` for the expanded column the cell names and maps
  that to raw through `DiffLineText.ToRaw` on both tab edges, taking the nearer one, so a line with
  both a tab and a CJK character resolves Up/Down's goal column the same way a click does.
- **~~`Features/Diff` and `Features/Editor` now depend on each other~~ — broken.** Nothing under
  `Features/Diff` references the `Editor` namespace any more; the dependency runs one way, from the
  editor to the diff primitives it projects onto.
- **~~`EditorRowSet` mutates on read and is not thread-safe~~ — enforced.** `Rows[i]` writes the
  materialization cache, so there is no such thing as a concurrent reader here. The set now captures
  the thread that built it and every public member checks it, throwing a sentence that names both
  threads and says where the hand-over belongs. Not a lock: locking would make the race disappear
  rather than the bug, and the bug is an annotation pass reaching the projection from the lane it was
  parsed on. `IUiDispatcher` has only `Post`, so it cannot answer "am I on the UI thread" — the
  captured id is what can.
- **~~A3 is a data-loss path that already exists~~ — closed in phase 7.** A dirty buffer survives the
  reconcile tick and any repo-wide invalidate; an external change under it asks, once, before
  anything is lost; the app's own save is recognised by its `FileStamp` and never asks. The
  `.tmp` beside a save never reaches the tree. All pinned by `FileBrowserPreviewRefreshTests`.
- **~~Unsaved work does not survive a quit~~ — quit asks.** `UnsavedDocumentsExitGate` holds the
  exit open and shows the same unsaved-changes dialog tab close does, listing every file. The
  buffers are still in memory only: a crash, as opposed to a quit, still loses them, and persisting
  dirty buffers is the remaining half of this item if it is ever wanted.
- **`IsBinary` sniffs only the first 8 KB** (`FileContentLoader.cs:67,131`). A file with NUL bytes
  past that is treated as text and would be editable.
- **~~Every UTF-16 file is shown as binary~~ — found and fixed in phase 0.** `IsBinary` ran *before*
  the decode, and UTF-16 ASCII text is roughly half NUL bytes, so no UTF-16 file ever reached the
  decoder: the old `Decode`'s UTF-16 branches had been unreachable for as long as they had existed.
  A byte order mark now outranks the NUL sniff. This is a visible behaviour change — UTF-16 files
  that read as "binary file" now render as text — and it is what makes phase 6's round-trip test
  achievable end to end.
- **Anchors are subtle and their bugs are silent.** A wrong bias does not crash; it puts a
  diagnostic one character off, or a selection that grows when it should not. The phase-1 test list
  is deliberately longer than the module.
- **Scope.** Every one of completion, formatting, rename and multi-file replace will look like a
  small addition once the document exists. Each is a project. The module boundaries make them
  possible later; the decision table above is what says they are not v1.
