# Rename symbol

## What this is

Visual Studio's Ctrl+R, R: rename the symbol under the caret — a local, a parameter, a method, a type — and
every reference to it, in every file of the project, changes with it. The language server decides
what is a reference; the client applies the edits.

It is the first feature that edits files the reader does not have open, which is what makes it the
largest of the three editing plans and the one that builds the shared piece: applying a
**workspace edit** across files.

## Why this is affordable

- The protocol does the semantic work: `textDocument/prepareRename` says whether the thing under
  the caret can be renamed and where it is; `textDocument/rename` returns every edit.
- GitBench is a git client. A rename lands as ordinary changes in the working tree, so the diff is
  its preview and Discard is its escape hatch — the reader can review exactly what changed, file by
  file, with the tools they already use.

## What already exists — verified

| Piece | Where |
| --- | --- |
| csharp-ls advertises `renameProvider: true` (trace, 0.27) | LSP trace |
| Unsaved buffers, keyed by path, per repository; what an unsaved file holds right now | `Features/Editor/DocumentStore.cs` (`IDocumentStore`, `IRepoDocuments`) |
| Writing a document back in the encoding and line endings it was read in | `Features/Editor/DocumentSaves.cs`, `DocumentWriter.cs` |
| A file changing on disk under an unsaved buffer is caught and the reader asked | `FileBrowserViewModel.Reconcile` / `Settle` |
| One undo journal per open document — nothing spans files | `Features/Editor/EditJournal.cs` |
| Toasts with an action button, for Undo | `Features/Notifications/Toast.cs` (`ToastAction`) |
| The server is only ever shown the one previewed file; every other file it reads from disk | `GitBench.Lsp/Documents/Documents.cs` (`PreviewSession`) |

Ctrl+R is unbound today (Refresh is F5). But the keymap has no two-stroke chords: a binding is one
`KeyGesture` — one key and its modifiers (`Input/KeyGesture.cs`) — and `IKeyMap.Matches` judges a
single key press with no memory of the one before. Parsing and display (`KeyGesture.TryParse`,
`Display`) and the shortcut recorder in settings (`Features/Settings/ShortcutRecorderController.cs`)
all assume one stroke.

## Decisions

| Area | Decision |
| --- | --- |
| Shortcut | **Ctrl+R, R**, as in Visual Studio: Ctrl+R, then R. The second stroke is accepted with or without Ctrl still held, since holding it through both is how the chord is usually typed (Visual Studio's own binding is Ctrl+R, Ctrl+R). Cmd+R, R on macOS, through the primary modifier like every other shortcut. Rebindable in settings like any other command. |
| Chords in the keymap | A binding becomes one stroke or two: a sum type (`Single(KeyGesture)` / `Chord(KeyGesture First, KeyGesture Second)`), never a list of gestures a handler has to interpret. Parsed and displayed as `Ctrl+R, R`, and persisted in the same string form, so an existing preferences file still reads. |
| A pending first stroke | Waits for the next key with no timeout, as Visual Studio does, and says so in the status bar ("Ctrl+R was pressed — waiting for the second key"). A second key that completes no chord cancels it and is **swallowed**, not typed: a stray R landing in the file is worse than a key that did nothing. Esc cancels. |
| Asking | `prepareRename` when the server supports it (its range and placeholder are the truth); otherwise the identifier under the caret. A refusal is shown as the server's own message. |
| Entering the name | A small popup at the symbol, prefilled and selected, Enter to rename, Esc to cancel. Not Rider's in-place live rename: that needs every reference in the file edited as you type, which is v2. |
| The workspace edit | Both shapes: `changes` and `documentChanges` of text edits. **No resource operations** — the client advertises none, so no server renames or creates files in response. |
| Unsaved buffers other than the one on screen | The server computed their edits from **disk**, because only the previewed file is ever sent to it. So each edit is checked against the buffer before anything is written: the text at each range must still be what the edit expects to replace (for a rename, the old name). Any mismatch refuses the whole rename and names the file. |
| Atomic | Validate every file first, then apply. Nothing is written until everything has been checked. |
| Where each file's edits go | Open file with unsaved edits → its buffer, left unsaved. Open file without edits → its buffer, then saved, so disk and buffer agree. Closed file → read, edited, written back in its own encoding; a file that cannot be decoded reversibly refuses the rename. |
| Undo | The editor's Ctrl+Z is per document and cannot undo a rename across files. The rename answers with a toast — "Renamed `Login` to `SignIn` in 7 files" — whose Undo applies the inverse edits, validated the same way. After that, the diff and Discard are the way back. |
| Telling the server | Files written to disk are announced with `workspace/didChangeWatchedFiles`, or the server's index keeps the old name and the next rename is computed against stale text. |

## The hard parts

**Chords are state, and the keymap is not.** `Matches(command, key, modifiers)` answers from one key
press. A chord needs a pending first stroke held somewhere between two key events — and in one place,
so a Ctrl+R pressed in the editor is not also taken by an app-wide handler as the start of something
else, or ignored by the editor because the second stroke arrives after focus moved. The keymap owns
the pending state and every dispatcher asks it, so that stays true for every chord added later.
Conflict detection in the shortcuts dialog grows a new case: a single stroke that is the first
stroke of some chord shadows it, and has to be reported as a conflict.

**Three kinds of target, three different truths.** The file on screen is in sync with the server.
Other open files with unsaved edits are not — the server saw their disk text. Closed files are only
on disk. The validation step is what makes this safe: check the old text at every range in the
place the edit will land, and refuse rather than guess.

**Writing files the reader is looking at.** The repository watcher will see every write. For a file
open and saved by the rename, the reconcile must find nothing diverged; for one written to disk
while closed, nothing is open to diverge. A rename must never raise the "file changed on disk"
prompt for its own writes. Verify both paths before building on them.

**Stale server index.** A server watching files itself picks up the writes; one that relies on the
client does not. `didChangeWatchedFiles` is normally sent only for patterns the server registered
(`client/registerCapability`), which this client does not support yet. Check per server whether an
unregistered notification is honoured; if not, support the registration.

**Partial failure.** A write can still fail after validation (a lock, a full disk). Stop, report
which files were written and which were not, and leave the written ones — they are visible in the
diff, and Discard undoes them one file at a time.

## Build order

1. Two-stroke chords in the keymap: the binding sum type, parse and display, the pending stroke
   and its status-bar hint, the recorder capturing a second stroke, conflict detection. Tests for
   matching, cancelling, swallowing the stray second key, and round-tripping preferences.
2. Protocol: `prepareRename`, `rename`, the `WorkspaceEdit` reader (both shapes). Shape tests.
3. The workspace edit applier, on its own: validate across open buffers and disk, then apply, with
   the undo record. Tests on temp directories covering all three kinds of target and each refusal.
4. The rename popup on Ctrl+R, R. Driven check against csharp-ls: rename a method used in two
   files, one of them open with unsaved edits elsewhere in it.
5. Undo from the toast.
6. `didChangeWatchedFiles` for files written, after checking what csharp-ls and rust-analyzer need.
7. Hand the applier to code actions, which enables their multi-file actions (`code-actions.md`).

## Testing

- Applier tests: an edit whose expected text is missing refuses everything; a closed file keeps
  its encoding, BOM and line endings; an open clean file ends saved; an open dirty file ends dirty
  with the edit in it.
- A reconcile test: the rename's own writes raise no prompt.
- Undo tests: the inverse applies; a file changed since refuses the undo.
- Driven check against csharp-ls through the GUI MCP server.

## Risks

- **Renames that reach generated or vendored files.** The server decides what is a reference.
  Everything it changes shows in the diff, which is the review step; no filtering in v1.
- **Large renames.** A widely used name can touch hundreds of files. Apply off the UI thread in the
  write phase, with progress, and keep validation cheap (one read per file).

## Deliberately not doing

- In-place live rename (typing the new name at every reference in the file at once).
- Renaming files along with types (resource operations).
- A conflicts dialog. The server refuses renames that would clash; anything else is reviewed in the
  diff.
