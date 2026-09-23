# Search everywhere

## What this is

Rider's double Shift: tap Shift twice and a popup opens over the window. Type part of a file name
or a type or member name, and the results show files, types and members from the whole repository,
ranked as you type. Enter or a click opens the file at that definition.

Symbols come from two sources:

- **A tree-sitter index** of the whole repository, built in the background. It needs no language
  server and covers every language that has an outline query, so the popup always has symbol
  results.
- **`workspace/symbol` from language servers that are already running.** When one has answered for
  a language, its rows take priority over the index's rows for that language, because the server
  knows about partial classes, generated code and things tree-sitter only guesses at.

## What already exists — verified

| Piece | Where |
| --- | --- |
| Every file in the working tree, tracked and untracked but not ignored (`git ls-files --cached --others --exclude-standard -z`) | `Git/GitService.cs` (`ListWorkingTreeFiles`) |
| Fuzzy path ranking: exact path, then file name, glob, near-miss, subsequence | `Infrastructure/PathSearch.cs` (`PathSearch.Rank`) |
| Find file (Primary+P): lists once per open off the UI thread, ranks off the UI thread, caps results, drops stale answers | `Features/FileBrowser/FileFinderViewModel.cs` |
| Per-file outline: name, `SymbolKind`, parameter types, `NameLine`/`NameColumn`, children | `Features/CodeIntel/` (`ISymbolExtractor`, `FileOutline`, `OutlineNode`) and `Assets/Queries/*.scm` |
| Open a file at a line and pin its tab, which also switches to Files and records back/forward | `IFileNavigator.NavigateTo`, `FileBrowserViewModel.PlaceCaret`, reached through `IFileBrowserStore.Active` |
| Back/forward history per repository, across files, terminals and views | `App/ContentNavigator.cs` |
| Language servers per (repository, language), with their state | `GitBench.Lsp/Lifecycle/LanguageServerSupervisor.cs` (`StateFor`, `ProcessFor`, `Status`) |
| File changes in a repository | `Features/Repos/RepoWatcherService.cs` |
| An overlay with a backdrop inside the main window | `ScrimController` in `Features/Review/ReviewCheatsheetOverlay.cs` |

What does **not** exist:

- **A shortcut made of a modifier alone.** A binding is one `KeyGesture`, a key plus modifiers.
  `KeyGesture.IsModifierKey` treats Shift as a modifier only, `TryParse` refuses a bare modifier, and
  the shortcut recorder ignores one.
- **Key-ups in the app keymap.** `AppKeybindController` returns early on anything that is not a
  press. The framework does send bare Shift presses and releases (`DesktopInputSystem.HandleKeyEvent`,
  `KeyboardKeyEvent`), and a held key repeats as extra presses.
- **A repository-wide symbol index.** Outlines are computed per file, on demand, and cached only
  in the tree for expanded files.
- **`workspace/symbol` in the LSP client.** `Protocol/Identifiers.cs` has definition, references,
  hover, completion, signature help and semantic tokens. `ServerCapabilities` does not read
  `workspaceSymbolProvider`.
- **A popup whose results change as you type.** `ContextMenu.ShowSearchable` filters a fixed list
  by substring and sizes itself once, so it cannot show results that arrive later or re-rank them.
- **A symbol-name matcher.** `PathSearch` scores paths. Nothing does CamelHumps (`FBVM` →
  `FileBrowserViewModel`).

## Decisions

| Area | Decision |
| --- | --- |
| Shortcut | **Double Shift**, a new kind of binding, plus **Primary+Shift+A as a second default** so there is a chord for people who turn double Shift off. Rebindable in settings like any other command. Primary+P stays Find file in the sidebar. |
| Double-tap in the keymap | A binding becomes a sum type: `Stroke(KeyGesture)` or `DoubleTap(Modifier)`, never a gesture with a flag on it. Displayed and saved as `Double Shift`, so preferences files stay strings. The recorder records a double tap when a modifier is pressed and released twice with nothing in between. If rename-symbol's chords land first, this is a third case of the same type. |
| What counts as a tap | Shift pressed and released within 300 ms, with no other key pressed in between and no mouse button pressed. The two taps must start within 400 ms of each other. Repeats while Shift is held are ignored. Shift used for Shift+A, or held for a Shift-click, cancels. Times come from the injected clock, so tests control them. |
| Where it works | The main window, whatever has focus: editor, terminal, a text field, a list. The detector lives in `AppKeybindController` and has to see key releases. **Verify** that the editor, terminal and text inputs let a bare Shift through. Secondary windows (diff, review) are v2. |
| The popup | An overlay inside the main window, like `ReviewCheatsheetOverlay`: centred near the top, with a backdrop. Esc or a click outside closes it. It is not a separate window, so keyboard focus and RTL come with the main window's. |
| Tabs | **All · Types · Symbols · Files**, switched with Tab / Shift+Tab. *All* shows three groups (types, then files, then members), a few rows each, with "more" jumping to that tab. *Types* is classes, structs, interfaces, enums, records. *Symbols* is types plus members. |
| Reopening | Reopens on the last tab with the last query, all of it selected, so typing replaces it. That's Rider's behaviour. The query is kept per repository for the app session only. |
| A row | Kind icon, name (with the matched letters highlighted), then muted: the containing type for members, parameter types for overloads, then the path relative to the repository. A row from a language server shows a small marker, so it is visible which rows came from the server. |
| `Path:line` | `Foo.cs:42` or `src/Foo.cs:42:7` on the Files tab opens at that line (and column). |
| Opening a result | `NavigateTo`, then `PlaceCaret` at the symbol's name (the index's `NameLine`/`NameColumn`, or the server's range). This gives the pinned tab and back/forward step. Shift+Enter opens it in a transient tab. |
| File results | `ListWorkingTreeFiles` once per open, ranked with `PathSearch.Rank`, the same as Find file. Shared with `FileFinderViewModel` by extracting a function both can call. It is not a new cache. |
| Symbol index | One per repository, in an `ISymbolIndexStore` that owns it, like the snapshot store. Everything else reads from the store. It is built in the background at repository open, deferred through `StartupSweepCoordinator` so it doesn't compete with the first load. Each file is keyed by (path, size, last-write time), and a file whose key has not changed is not parsed again. It is updated from the repository watcher and kept in memory only in v1. |
| What gets indexed | Files from `ListWorkingTreeFiles` whose `CodeLanguage` has an outline query. Files over 1 MB are skipped. Parsing runs with limited parallelism, below the UI's priority. While it runs, the popup shows "Indexing symbols — 1,240 of 5,300 files" and results fill in as files finish. |
| Symbol ranking | A new `SymbolSearch.Rank`: exact name, then prefix, then CamelHumps (`FBVM`, `FilBrVM`), then substring, then subsequence. Matching ignores case, with a bonus when the case matches. Types rank above members when scores tie. A dot in the query splits it into container and name: `Auth.Login` means `Login` inside something matching `Auth`. Ranking runs off the UI thread with stale answers dropped, as in the file finder. |
| Language servers | Only servers that are **already running and ready** for the active repository are asked. The popup never starts one, so opening search stays instant and has no side effects. |
| Asking a server | `workspace/symbol` with the query, 150 ms after typing stops. A newer query cancels the older request (`$/cancelRequest`; **verify** the client can send it). The client does **not** advertise `resolveSupport`, so servers answer with full locations and not bare URIs that need `workspaceSymbol/resolve`. Each server gets 2 s; a slow one is dropped without disturbing the rest. |
| Priority | Per language. Once a server for language L has answered the current query, its rows are the Types/Symbols results **for files of L**. The index's rows for L that the server also returned (same file, same name, name line within one line) are removed. Index rows for L that the server did not return stay, **ranked below** the server's rows, because servers limit and filter their answers in their own ways and dropping them would hide real definitions. Rows for other languages are unaffected. Rows are replaced in place without clearing the list, so the list doesn't flicker when the answer arrives. |
| Server results outside the repository | Dropped in v1: library and SDK symbols point at files the Files pane cannot open. |
| Kinds | The LSP `SymbolKind` is mapped onto the app's `SymbolKind` at the boundary, in one exhaustive switch, and anything unknown maps to a single `Other`. The rest of the code never sees LSP numbers. |

## The hard parts

**Seeing the taps at all.** The double tap is decided on releases, and releases are exactly what
the app keymap ignores today. The detector must see every key event in the main window, including
while focus is in the editor, the terminal or a text field, and must never consume them. A bare
Shift has to keep working as a modifier for whatever has focus. Route events to it the same way
wherever focus is, and check each focus target under `GuiTestHarness`.

**Two sources, one list.** Index rows appear right away. Server rows arrive later, for some
languages, maybe never. The merge has to be a pure function of (index rows, server answers per
language, query), so it can be tested without a server and cannot depend on arrival order. An
answer to an old query is thrown away, never merged.

**Index cost on large repositories.** A 50,000-file repository is thousands of parses. Parse with
limited parallelism, skip huge and generated files, and measure memory: store names and
positions, not trees or text. If the first build is too slow, index recently changed files first,
and write the index to disk in v2.

**Servers disagree on `workspace/symbol`.** csharp-ls and rust-analyzer do fuzzy matching.
typescript-language-server answers only for projects it has loaded (an open file). clangd needs a
compile database. gopls matches its own way. Some return nothing for an empty query or cap at 100.
This is why server rows never remove index rows they did not return.

## Build order

1. **Double-tap bindings.** The `Stroke`/`DoubleTap` binding type, parsing, display, saving,
   recording in the shortcuts dialog, and conflict detection. Add `KeyCommand.SearchEverywhere`. The
   tap detector in `AppKeybindController`, on the injected clock. Tests: two taps within the window
   fire; a slow tap, a held Shift, repeats, Shift+A, and Shift-click do not fire; the binding
   round-trips through preferences.
2. **The popup with Files only.** Overlay, search field, tabs, a virtualized list bound to the VM's
   derived results, keyboard handling, `Path:line`, opening the result, and reopening on the last
   query. Pull the file listing and ranking out of `FileFinderViewModel` so both use it. Strings in
   all seven locales. This step is usable on its own.
3. **`SymbolSearch.Rank`.** CamelHumps, prefix, `Container.Name`, tie-breaks. Table-driven tests.
4. **The symbol index.** `ISymbolIndexStore`: a background build deferred behind startup, updates
   from the watcher, progress reporting. Then the Types and Symbols tabs and the All groups. Tests
   on a temp repository: build, edit a file, delete a file, rename a file.
5. **`workspace/symbol`.** The protocol types and reading the server's answer at the boundary, the
   capability, `LanguageServerStore.WorkspaceSymbolsAsync` over running servers only, cancellation
   and the time limit. Then the merge as a pure function, with tests for priority, removing
   duplicates, rows the server did not return, stale answers, and a server that never answers.
6. **Checking the running app** through the GUI MCP server: double Shift from the editor, the
   terminal and a text field; results against csharp-ls on this repository, showing server rows
   ahead of index rows.

## Testing

- The tap detector on a fake clock, every case in the "What counts as a tap" row.
- `GuiTestHarness`: double Shift opens the popup from each focus target; Esc and clicking outside
  close it; Enter opens the file with the caret on the symbol's name; Tab switches tabs; reopening
  restores the last query, selected.
- `SymbolSearch` and the merge: pure, table-driven tests.
- The index: temp repositories, including a file over the size limit, a binary file and a language
  with no outline query.
- Timing-sensitive tests run in isolation when they fail under the full suite.

## Risks

- **Double Shift misfires while typing.** Two quick capitals in a row are Shift+key, and any key in
  between cancels a tap, so this should be rare. Rider has lived with it for years. Turning it off
  in settings is the escape.
- **Index memory.** A name, kind, container, file id and position per symbol. Measure on a large
  repository before committing to keeping it in memory.
- **Servers that answer badly.** Handled by the time limit and by never letting a server remove
  rows it did not return.

## Deliberately not doing (v1)

- Actions, meaning a command palette of `KeyCommand`s. The popup's tabs are the place to add it later.
- Text search in file contents.
- Double Shift in secondary windows.
- Starting a language server from search.
- Symbols from libraries and SDKs outside the repository.
- Saving the index to disk.
