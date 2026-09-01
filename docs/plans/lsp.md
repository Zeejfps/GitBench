# Language servers in the Files pane

## What this is

The Files pane shows the working tree and previews whatever file you select. Today it understands
code one file at a time: it colors syntax, folds declarations, and lists an outline. It does that
with tree-sitter, a parser that reads a single file and knows nothing about the rest of the project.

A **language server** is a separate program that understands a whole project. You run one per
language — `rust-analyzer` for Rust, `gopls` for Go, `typescript-language-server` for TypeScript.
It reads your code, resolves imports, type-checks, and answers questions over a standard protocol
(LSP). Editors use them; that is where "go to definition" and red squiggles come from.

This plan adds an LSP client to the Files pane, so it can answer the questions a single file cannot:

- **Hover** — what is this symbol, and what is its type?
- **Diagnostics** — which lines do not compile, marked inline.
- **Go to definition** — jump to where a symbol is declared.

Servers are not bundled. The user installs them and lists them in a config file. If they never do,
nothing changes and nothing runs.

## Why this is affordable

The pane is **read-only**. It displays files; it never edits them.

That matters more than it sounds. Most of the difficulty in writing an LSP client is keeping the
server's copy of a file in sync with the editor's as the user types — incremental updates, version
numbers, and a long tail of bugs when the two drift apart. A viewer that never edits has none of
that. We tell the server "here is this file, read from disk", and the two copies cannot disagree.

The remaining work splits cleanly:

- **The protocol** — mechanical, pure, easy to test.
- **Running the servers** — process lifecycle, memory, failure. This is the part that costs.

## What the servers actually do

Measured with a throwaway client against real projects: a 54k-line Rust workspace, a 239k-line Go
module, and a 3.2k-line TypeScript package. No file was ever edited — each was opened once from disk.

| | rust-analyzer | gopls | typescript-language-server |
|---|---|---|---|
| Handshake | 15 ms | 60 ms | 93 ms |
| First useful answer, cold | **32 s** | 2.4 s | 0.6 s |
| First useful answer, warm | 11 s | — | 0.6 s |
| Answers once ready | 0 ms | 0 ms | 1–3 ms |
| Diagnostics appear after | 1.9 s | 6.6 s | 1.9 s |
| Memory, server process | **1.7 GB** | 754 MB | 38 MB |
| Memory, whole process tree | **3.5 GB** | 775 MB | 442 MB |
| Exits when asked | yes | yes | yes |
| Exits if we crash | yes | yes | yes |

Five things follow from this, and they drive most of the decisions below.

**Diagnostics work without editing.** Every server reported real compiler errors from a file opened
once and never modified. rust-analyzer runs `cargo check` on load; gopls and tsserver type-check on
open. This was the open question the whole feature depended on, and the answer is yes.

**Diagnostics arrive in waves, and can change minutes later.** gopls sends type errors first and
analyzer warnings four seconds later. rust-analyzer re-sent results for the same file three times as
its background check progressed. So diagnostics for a file must be *replaced* on each update, never
added to, and the UI has to tolerate a settled file changing.

**rust-analyzer is expensive.** 32 seconds before its first useful answer on a cold project, and 1.7
GB held steady afterward. During startup it spawns dozens of `cargo` and `rustc` children, briefly
reaching 3.5 GB across the tree. That is the number that sets the process policy.

**A finished handshake does not mean ready.** rust-analyzer completed its handshake in 15 ms while
still half a minute from answering anything. While indexing, it rejects requests with a specific
error code that means "ask again", not "failed". A client that treats that as failure will look
broken for the first 30 seconds.

**Servers clean themselves up.** Killing the client without killing its children left no orphans:
every server exited on its own within a second. They do this because they read their input from a
pipe we hold, and that pipe closes when we die. This was expected to be a major cost and is not —
with one caveat under Risks.

## How it is built

Four layers. Each depends only on the one above it.

| Layer | Lives in | Knows about |
|---|---|---|
| Message framing and request/response matching | `GitBench.Lsp` | a byte stream, nothing else |
| Protocol messages, parsed into real types | `GitBench.Lsp` | LSP only — no repos, no processes |
| Config, launching servers, per-repo tracking | `Features/LanguageServers` | repos, app paths, the UI thread |
| Showing results | `Features/FileBrowser` | one open document at a time |

The Files pane never touches a server. It holds a small handle for the file currently on screen —
its diagnostics, and two methods to ask about a position — and drops it when the selection changes.
When no config file exists, that handle has an empty implementation, which is what makes the whole
feature cost nothing when unused.

This deliberately does **not** extend `ISymbolExtractor`, the existing code-intelligence interface.
That interface is synchronous, per-file, and has 38 references across 7 features including the
assistant and the review window. Putting server processes behind it would wire language servers into
surfaces that should never touch them.

## Decisions

| Area | Decision |
|---|---|
| Features | Hover, diagnostics, go to definition. Nothing that edits code: no completion, formatting, rename, or code actions. There is no editor to put them in. |
| File sync | Send the file when previewed, drop it when the selection moves. Never send edits. |
| Which servers run | One per language, for the **active repository only**. Not one per open repo — the memory figures forbid it. Stopped when a repo goes idle, with a cap on how many run at once. |
| Config | A single file the user writes, stored with the app's other settings. Not stored per-repository — see Risks. |
| Finding the server binary | Resolved against the login shell's `PATH`, so a Mac GUI launch finds tools in `~/.cargo/bin` and Homebrew. Never run through a shell. |
| Progress | The pane always shows what the server is doing: not configured, starting, indexing (with a percentage), ready, or failed with a reason. Not optional — a 32-second silent wait is indistinguishable from a broken feature. |
| Discovery | A settings card listing languages in the current repo that have no server configured, with a button to create a starter config. Ships in v1, or nobody finds the feature. |
| Go to definition, in repo | Expands the tree to the target file and jumps to the line. |
| Go to definition, outside repo | Opens a **detached preview**: the file is shown, the tree selection clears, and the header shows the full path. Needed because most jumps in Rust and Go land in the standard library or a package cache, which the tree cannot show. |
| Where it applies | The Files pane only. Never the diff view, commit details, or review window — those show file contents *at a commit*, and a server asked about them would answer confidently and wrongly. |
| Off switch | One flag disables the whole subsystem. |
| Not in v1 | Project-wide symbol search, a references panel, semantic highlighting, call hierarchy, inlay hints, multiple workspace roots. |

## What already exists

Most of the supporting pieces are in the codebase.

- **A JSON-RPC library, already referenced.** `McpSdk.Shared` and `McpSdk.Protocol` arrive through
  `ZGF.Gui.Desktop` and provide request/response matching, message types, and a pluggable transport.
  LSP uses a different message framing than they do by default, but the transport interface is
  exactly the right place to add it. The package is ours, so it can be extended.
- **Column mapping between rendered and real text.** `Features/Diff/DiffLineText.cs` holds each line
  both as it appears on screen (tabs expanded to spaces) and as it is in the file, with typed
  conversions in both directions. Both are needed: positions we send must be in file coordinates,
  and ranges the server sends back must be painted in screen coordinates.
- **A place to draw diagnostics.** Diff rows already carry a list of character ranges used for
  intra-line highlighting, drawn independently of syntax colors. Underlines fit that channel. The
  gutter already has icon columns and click handling.
- **Popup positioning.** The tooltip service builds popups from any widget with automatic placement
  and flipping. A hover popup is that, with a markdown view inside.
- **Login-shell `PATH` lookup.** Added recently for finding `git` on macOS; it needs to be shared
  rather than written.
- **Process teardown across platforms.** The terminal already handles process groups and signals on
  Unix and process attributes on Windows.
- **Background work with cancellation.** The Files pane already runs file loads off the UI thread and
  cancels them when the selection moves.

## The hard parts

**Line-number mapping.** A position on screen is a row in a rendered list, which contains headers,
separators, and collapsed regions as well as code. A position in the file is a line number. These are
different things, currently both plain integers, and mixing them up produces a jump to the wrong
line that looks exactly like a working feature. The equivalent problem for columns is already solved
with distinct types; lines need the same treatment, with a total conversion in both directions that
handles rows with no line (a separator) and lines with no row (inside a collapsed fold).

**Diagnostics cannot be baked into the rendered rows.** Syntax colors are computed once when the file
is flattened into rows. Diagnostics arrive repeatedly, seconds apart, while the file sits on screen.
Folding them into the same structure would mean rebuilding every row on each update. They belong in a
separate layer, keyed by file and version, read at draw time, with stale updates discarded.

**Go to definition needs machinery that does not exist.** The pane can currently only preview a file
the tree has already listed and the user has selected. Jumping to a definition means expanding the
tree to a file, waiting for the directory listing, and moving the selection — or, for a file outside
the repo, showing a preview with no tree selection at all. It also needs a back stack, which the app
does not have anywhere.

**Some protocol responses have no fixed shape.** Hover content and definition results can each come
back in three different forms. The JSON code generator the app uses for everything else cannot
express that, so those few fields need hand-written readers. This is confirmed to work: the approach
compiles clean under the app's ahead-of-time build with no warnings, provided two specific
reflection-based JSON calls are avoided.

**Nothing in the app supervises a long-running process.** Restarting a crashed server with backoff,
giving up after repeated failures, shutting down an idle one — none of this has precedent here. It
all has to be written and tested.

**Large files must not be sent.** The preview truncates files over 2 MB and drops the last partial
line. Sending that to a server would produce errors for a file that does not exist. Truncated preview
means no server request.

**The outline stays with tree-sitter.** Language servers can list a file's symbols, but the app's
folding depends on knowing where a declaration's signature ends and its body begins, which the
protocol does not express. Servers add information; they do not replace the outline.

**Files change on disk.** The working tree can change while a file is on screen, and the app already
watches for that. Since we never send edits, "the server's copy matches disk" holds only if we react:
a changed file is closed and reopened at a new version, and results tagged with an old version are
discarded.

## Build order

Each phase produces something visible. Nothing is built two layers deep before anything works.

1. **One thing, end to end.** Message framing, handshake, open a file, one hover, shown in a popup —
   against a single hard-coded server. Includes the line-number mapping, since nothing downstream can
   be trusted without it.
2. **Real server management.** The config file, per-repo tracking, the active-repo-only policy,
   restart and shutdown, progress reporting, and the settings card.
3. **Diagnostics.** The overlay, the retry handling, wave replacement, underlines and gutter marks.
4. **Go to definition.** Detached previews, tree expansion, the back stack.

Deferred: references panel, project-wide symbol search, semantic highlighting.

Before phase 1: the repo has a CI job that builds and tests across four platforms, but it only runs
when triggered by hand. It needs to run on pull requests. That is a two-line change and it is assumed
by everything below.

## Config file

```jsonc
{
  "version": 1,
  "servers": {
    "rust": {
      "enabled": true,
      "command": "rust-analyzer",
      "args": [],
      "extensions": [".rs"],
      "rootMarkers": ["Cargo.toml"],
      "env": {},
      "initializationOptions": {},
      "settings": {},
      "requestTimeoutMs": 5000,
      "idleShutdownMs": 300000
    }
  },
  "maxConcurrentServers": 2
}
```

This is the only file in the app a user writes by hand, so it allows comments and trailing commas,
and a syntax error names the line and is shown rather than swallowed. One bad entry is skipped with a
reason; it does not discard the rest. The file stands alone, so a parse failure cannot affect other
settings.

`rootMarkers` finds the project root by walking up from the file, which is also what makes
submodules and nested projects work. `settings` exists because servers ask the client for their
configuration during startup and we need an answer to give them. Two entries claiming the same file
extension need a defined winner.

## Testing

**A fake server** is built in phase 1 and behaves badly on purpose: never answers, answers too late,
exits mid-request, sends a wrong byte count, sends a huge response, replies to a request that was
never made, sends results for a file that was never opened, and asks for configuration before
startup finishes.

**The position mapping** gets its own tests: tab-indented Go, Windows line endings, emoji, CJK text,
mixed tabs and spaces, and collapsed regions.

**Process cleanup** gets one test per platform that kills the client and checks nothing survives.

## Risks

1. **Wrong positions.** Every other failure here is visible: a crashed server shows an error, a slow
   one shows a spinner. A definition that jumps to the wrong line looks like it worked. This is why
   the line mapping is typed and tested before anything uses it.
2. **rust-analyzer's cost.** 1.7 GB and half a minute, in an app that sells on being small and fast
   to start. Mitigated by: nothing runs without a config file, only the active repo's servers run,
   idle servers stop, and there is a visible off switch.
3. **Servers behave differently from each other.** Different readiness signals, different timing,
   different setup requirements. Every request needs a timeout and every failure needs a message a
   user can act on.
4. **Cleanup on Linux and macOS is unverified.** Servers exiting on their own was measured on Windows
   only, and it relies on convention rather than a guarantee. The per-platform test above covers it,
   and the terminal's existing process handling is the fallback.
5. **Silence looks like breakage.** A file with no errors and a server that never started produce the
   same empty screen. The status display is the fix.

## Why config is not per-repository

A config file names a program to run. If it lived in the repository, cloning someone's project and
opening it would run their command. That is a bad property for an app whose whole job is opening
other people's repositories.

The honest version is narrower: per-repo config would be fine with a trust prompt, and the app has no
general prompt to hang it on today. When one exists, this can be revisited.
