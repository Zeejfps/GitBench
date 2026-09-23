# Code actions: quick fixes and refactorings (Alt+Enter)

## What this is

Rider's Alt+Enter: at the caret, a menu of what the language server can do here — fix the error
under the caret ("Add using", "Generate method"), clean up ("Remove unnecessary usings"), or
refactor ("Extract method", "Inline variable", "Convert to expression body"). Pick one, and its
edit lands in the file as one undo step.

Plus the one action that deserves its own key: **optimize imports** for the whole file (Rider:
Ctrl+Alt+O), which is a code action of the kind `source.organizeImports`.

## Why this is affordable

- The menu already exists: the searchable popup menu takes the keyboard now
  (`RepoBarContextMenu.ShowSearchable`), and the usages list uses it the same way.
- Most actions edit only the file on screen, and applying server edits to it as one step is the
  same machinery formatting needs (see `formatting-and-highlights.md`).
- The server does all of the thinking; the client lists, picks and applies.

## What already exists — verified

| Piece | Where |
| --- | --- |
| csharp-ls advertises `codeActionProvider: true` (trace, 0.27) | LSP trace |
| Diagnostics for the open file, with range, severity, message, source, code | `GitBench.Lsp/Protocol/Messages.cs` (`Diagnostic`) |
| Requests the server sends the client, answered on a handler; anything unknown answered `MethodNotFound` | `GitBench.Lsp/Protocol/LspConnection.cs` (`Answer`, the `NotHandled` case) |
| No `workspace` capabilities advertised at all — so servers never send `workspace/applyEdit` today | `GitBench.Lsp/Protocol/Handshake.cs` (`Initialize`) |
| Opaque handles that hand a server's JSON back unchanged | `CompletionItemHandle` in `GitBench.Lsp/Protocol/Completion.cs` |
| Searchable, keyboard-driven popup menu; disabled items; checked items | `Features/Repos/RepoBarContextMenu.cs` |

Alt+Enter and Ctrl+Alt+O are unbound today.

## Decisions

| Area | Decision |
| --- | --- |
| What is asked | `textDocument/codeAction` for the selection, or the caret's position when there is none, with the diagnostics that overlap it. |
| Diagnostics sent back | **Exactly as the server published them.** `Diagnostic` today keeps five fields and drops the rest, including `data`, which Roslyn-based servers use to find the fix again. Keep the raw JSON beside the parsed record, the way completion items keep a `CompletionItemHandle`, and echo that. |
| Client capabilities | `codeActionLiteralSupport` with the kinds we group by (without it servers answer with bare commands), `isPreferredSupport`, `disabledSupport`, `dataSupport`, `resolveSupport` for `edit`. |
| Resolve | An action without an edit is resolved (`codeAction/resolve`) when picked, not when listed — listing stays one request. |
| Commands | An action that is a command is sent back with `workspace/executeCommand`. The server applies it by sending `workspace/applyEdit`, which the client must now answer: advertise `workspace.applyEdit`, handle the request on the UI thread, answer `{ applied, failureReason }`. |
| Menu order | Preferred fix first; then quick fixes for diagnostics on the line; then `refactor.*`; then `source.*`. Disabled actions shown dimmed with their reason, not hidden — "why can't I" is an answer. |
| Scope in v1 | Actions whose edits touch only the open file. An action that edits other files, or creates, renames or deletes one, is listed but disabled with "edits other files" until the workspace edit applier from the rename plan exists (`rename-symbol.md`). |
| Optimize imports | Ctrl+Alt+O asks with `only: ["source.organizeImports"]` for the whole file and applies the first action without a menu. When the server offers none, says so in a toast rather than doing nothing. |
| Lightbulb | Not in v1. A gutter bulb means asking on every caret move over a diagnostic; the menu on request is the whole feature, the bulb is discoverability. |

## The hard parts

**The applyEdit handshake inverts the usual direction.** Every other feature asks and waits. Here
the client asks the server to run a command, and before that request returns the server asks the
client to apply an edit. The client's handler runs on the connection's reader; it has to hop to the
UI thread, apply against the current document, and answer — without waiting on the outstanding
`executeCommand` it is nested inside. Deadlock is the failure mode to test for.

**Which text the edit describes.** `TextDocumentEdit` carries the document version it was computed
for. Apply only when it is the version this client sent last and the buffer has not moved since;
otherwise refuse (`applied: false` for applyEdit, a toast for a picked action). `changes` without
versions are treated as describing the version sent last.

**Echoing diagnostics faithfully** changes `PublishedDiagnostics` parsing, which the diagnostics
overlay and the hover card read. The parsed fields stay as they are; the raw element is an addition.

**Server variety.** "Remove unused usings" in csharp-ls is offered only where the compiler reports
the unnecessary-using diagnostic (IDE0005), which depends on the project's analyzer settings.
tsserver and rust-analyzer offer `source.organizeImports` directly. Unverified per server: check
each against a trace before promising Ctrl+Alt+O works for it.

## Build order

1. Keep diagnostics' raw JSON; capability parsing; `codeAction` request and the
   `(Command | CodeAction)[]` reader; `codeAction/resolve`. Shape tests.
2. Menu: Alt+Enter → ask → searchable menu at the caret → pick → resolve if needed → apply the
   single-file edit as one undo step (the edit applier shared with formatting).
3. Commands: `workspace/executeCommand`, advertise `workspace.applyEdit`, answer the server's
   `workspace/applyEdit` on the UI thread. A scripted-server test for the nested round trip.
4. Ctrl+Alt+O.
5. Once the rename plan's workspace edit applier lands: enable multi-file actions.

## Testing

- Reader tests for every shape: bare commands, literals with and without `edit`, `disabled`,
  `isPreferred`, kinds.
- A scripted-server test: pick a command action, the server sends `applyEdit`, the client applies
  and answers, then `executeCommand` returns — in that order, without a hang.
- A version test: an edit for an older version is refused and the reader is told.
- Driven checks against csharp-ls: "Add using" on an unresolved type; "Remove unnecessary usings"
  on a file with one.

## Risks

- **Menus that list nothing useful.** Servers return many refactorings everywhere; ordering by kind
  and preferring fixes for the diagnostic under the caret is what keeps the first entry the right
  one. Check with real files before tuning.
- **Actions that also want to move the caret** (rust-analyzer's snippet edits). Not advertised; the
  edit lands and the caret stays where the edit carries it.

## Deliberately not doing

- A gutter lightbulb (see Decisions).
- Previewing an action's diff before applying it. The change is one undo away, and the diff view is
  one click away.
- Actions in the diff view or review window: those show files at a commit.
