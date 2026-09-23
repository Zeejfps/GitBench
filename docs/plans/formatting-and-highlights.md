# Formatting and symbol highlights in the editor

## What this is

Two language-server features that touch only the file on screen:

- **Reformat** — the server rewrites the whitespace and layout of the file, or of the selection,
  the way the project's formatter would (`.editorconfig`, `rustfmt`, `gofmt`, Prettier through
  tsserver). Rider: Ctrl+Alt+L. Optionally also **as you type**: the server tidies a statement when
  `;` or `}` closes it.
- **Highlight usages in file** — resting the caret on a symbol washes every other use of it in the
  file, reads and writes in two shades. Rider does this by default.

Both are the smallest step from "the editor completes code" to "the editor changes code on the
server's word": neither edits a file that is not open, and neither needs a dialog.

## Why this is affordable

- Every edit lands in the one document on screen, which already has an undo journal and a way to
  apply several edits as one step (`EditSession.Complete`, built for completions that bring an
  import with them).
- The server already holds that document's current text: `didChange` is sent before every
  question (`LanguageServerConnection.EnsurePreviewedAsync`).
- Highlights are paint, not text. They follow the pattern the find bar's hits already use.

## What already exists — verified

| Piece | Where |
| --- | --- |
| Capabilities advertised by csharp-ls (trace, 0.27): `documentFormattingProvider`, `documentRangeFormattingProvider`, `documentOnTypeFormattingProvider` (first trigger `;`, more `}` `)`), `documentHighlightProvider` | seen in an LSP trace |
| Capability parsing, one property per feature | `GitBench.Lsp/Protocol/Handshake.cs` (`ServerCapabilities`) |
| Questions about the open document, stale answers dropped by version | `GitBench.Lsp/Documents/Documents.cs` (`PreviewSession`, `StillShowing`) |
| One question out at a time, answered on the UI thread | `Features/LanguageServers/ProbeSlot.cs` |
| Several edits as one undo step, applied bottom-up, caret landing computed | `Features/Editor/EditSession.cs` (`Complete`) |
| Marks painted behind text, keyed by line | `Features/Diff/DiffSearchOverlay.cs`, drawn in `DiffContentView.SearchOnRow` |
| Overlays that know which text they describe and drop marks on lines that changed | `Features/Diff/DiffDiagnosticOverlay.cs` (`StillDescribes`) |
| Keymap and localized command names | `Input/KeyCommand.cs`, `Input/KeyMap.cs`, `Localization/Strings/*.json` (all seven) |

Ctrl+Alt+L is unbound today.

## Decisions

| Area | Decision |
| --- | --- |
| Where edits apply | The open buffer only, as one undo step. Never the file on disk: a reformat is an edit like any other and is saved like any other. |
| Stale answers | A formatting answer describes the revision it was asked about. It is applied only if the document has not moved since (`DocumentRevision`); otherwise it is dropped silently and the reader presses the key again. Never rebased. |
| Caret | Carried across the edits with `TextEdit.Shift`, so it stays on the token it was on. |
| Formatting options | `insertSpaces` and `tabSize` from `EditOptions` (the file's own indentation), `trimTrailingWhitespace`, `insertFinalNewline` left to the server's config. The project's formatter settings win over anything the app guesses. |
| Selection | Reformat with a selection asks `rangeFormatting` for it; without one, `formatting` for the file. A server offering only one of the two gets only that one. |
| Format on type | Off by default, a setting. It changes text the reader did not type, and it overlaps the typing aids that already outdent a `}` — both converging on the same answer has to be seen working before it is the default. |
| Highlights | Asked 150 ms after the caret settles, cleared on any edit, replaced wholesale on each answer. Read and write uses in two theme tokens; text uses (kind 1 or none) in the read shade. |
| No server | The command does nothing and says nothing. There is no syntactic fallback: matching identifiers by name lights up every same-named thing, which is the kind of answer that lies quietly (the reason the usage lens is server-only). |

## The hard parts

**Edits describe a text the reader may have moved past.** The flush before the question makes the
server's copy current at the moment of asking; typing during the round trip moves it on. The
revision check covers this — but it has to be the revision captured *before* the flush, on the UI
thread, not after the answer arrives.

**A reformat is a big edit.** A whole-file reformat can return hundreds of edits. They must apply
bottom-up in one transaction (the journal already coalesces a transaction into one step), and the
row projection must follow them. `EditorRowSet.Reproject` follows one edit at a time and rebuilds on
anything it cannot follow; a burst of hundreds of small edits is hundreds of reprojections. Measure:
if it is slow, apply them as one replacement of the smallest range covering every edit instead.

**Colors after a reformat.** The highlight table now shifts with each edit
(`EditorRowSet.ShiftHighlight`) and keeps colors on lines whose text is unchanged. A reformat
changes most lines' text, so most lines will draw plain until the reparse lands — a visible flash on
a large file. Acceptable for v1; if it is not, keep spans on lines whose text differs only in leading
whitespace, shifted by the indent change.

**Highlights on a moving file.** Ranges are positions in the text the server was sent. Carry the
text they describe, exactly as the diagnostics overlay does, and drop marks from any line that no
longer reads the same.

## Build order

1. Protocol: `textDocument/formatting` and `rangeFormatting` requests and their `TextEdit[]` reader;
   `FormattingSupport` on `ServerCapabilities`. Tests against recorded shapes.
2. `EditSession.ApplyServerEdits(edits, revision)` — the general form of `Complete` — returning
   whether it applied. Tests: bottom-up application, caret carried, one undo step, stale revision
   refused.
3. Reformat command (Ctrl+Alt+L) through connection → store → editor controller. Visible check
   against csharp-ls on a badly indented file.
4. `textDocument/documentHighlight` request, overlay, paint, theme tokens (both themes).
5. On-type formatting behind a setting, triggered from the characters the server names.

## Testing

- Protocol shape tests for both result shapes (`TextEdit[]`, `null`) and for highlight kinds.
- Session tests for edit application and the revision refusal.
- A controller test that a stale answer is dropped and a current one applied.
- Driven check through the GUI MCP server against csharp-ls, as the completion work was checked.

## Risks

- **Servers disagree about options.** Some ignore `tabSize` and read their own config. That is the
  right outcome, but a reader whose file is indented with tabs and whose `.editorconfig` says
  spaces will see the whole file change. The edits are one undo away.
- **Very large files.** A formatter answer for a 20k-line file is large; measure the apply path
  before calling it done.

## Deliberately not doing

- Formatting on save. It is one line once formatting exists, but it changes what a save means, and
  that deserves its own decision.
- A syntactic "highlight same identifier" fallback for files without a server.
- Formatting outside the editor (the diff view, the review window): those show files at a commit.
