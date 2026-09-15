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
| Server state | Six states, not five: **not configured**, **stopped** (configured, nothing running — the normal resting state, and where a server returns after an idle stop), **starting**, **indexing** (with a percentage), **ready**, **failed** with a reason. Shown in the pane always. A 32-second silent wait is indistinguishable from a broken feature. |
| Ready means answered | `ready` may only be set by something that saw a real answer. A finished handshake is its own state — one server completes the handshake in 15 ms and is 30 seconds from useful. This makes the protocol layer responsible for telling "answered" apart from "replied with the ask-again code". |
| Giving up | A server that stops, stops. That state has a way out: an explicit retry, and any edit to its config entry. Without one, installing the missing binary would require restarting the app. |
| Idle shutdown | Applies to repositories the user has **left**, not to the active one. This layer only sees files being opened — hovers go through the per-file handle — so an active-repo timer would throw away a 32-second index while someone reads one file. |
| Config reload | Only the fields that affect launching (`command`, `args`, `env`, `rootMarkers`, `initializationOptions`, `settings`) restart a server. Editing a timeout must not kill a warm index. |
| Launch failure vs crash | Neither is retried, and they differ only in what the reason says. A server stops because its command is wrong, its toolchain is missing, or its project defeated it; none of those are different the second time, so starting it again only delays the one message the reader can act on. |
| Eviction | When the cap is reached: servers for repositories the user has left go first, then least-recently-used. Pure LRU evicts the server about to be asked a question. |
| Timeouts | `requestTimeoutMs` is a default, not the only knob. Requests carry their own — a server 32 seconds from its first answer and 0 ms thereafter cannot share one budget with a hover. |
| Threading | Process exit and readiness arrive on a pool thread; the supervisor takes no lock and is never re-entered. The adapter marshals both to the UI thread. The rule is **everything** that reaches the store, not just what the process raises: a handshake fails on whichever thread awaited it, and what listens rebuilds views — raised there it tears the view tree down underneath the input system mid-frame. That crash is what the rule is for, and it was found by running the app, not by reading it. |
| Discovery | A settings card listing languages in the current repo that have no server configured, with a button to create a starter config. Ships in v1, or nobody finds the feature. |
| Go to definition, in repo | Expands the tree to the target file and jumps to the line. |
| Go to definition, outside repo | Opens a **detached preview**: the file is shown, the tree selection clears, and the header shows the full path. Needed because most jumps in Rust and Go land in the standard library or a package cache, which the tree cannot show. |
| Where it applies | The Files pane only. Never the diff view, commit details, or review window — those show file contents *at a commit*, and a server asked about them would answer confidently and wrongly. |
| Off switch | One flag disables the whole subsystem. |
| Not in v1 | Project-wide symbol search, a references panel, semantic highlighting, call hierarchy, inlay hints, multiple workspace roots. |

## What already exists

Most of the supporting pieces are in the codebase.

- **The protocol, config, document and lifecycle layers themselves** — `GitBench.Lsp`, with 417 tests
  in `GitBench.Lsp.Tests`. Framing, request matching, timeouts and cancellation, result parsing, config
  parsing, server supervision, position mapping and document bookkeeping. It references no other
  project, so its tests need neither the app nor the tree-sitter natives and run in under a second.
  It now also spawns real processes (`ProcessLanguageServer`), behind a launcher interface the tests
  substitute; it still draws no pixels.
- **Both coordinate mappings, typed.** `Features/Diff/DiffLineText.cs` holds each line as it appears
  on screen (tabs expanded) and as it is in the file; `Features/Diff/DiffGutterNumber.cs` holds the
  line-number/row-index pair with the total mapping between them on `DiffRowSet`. Both directions
  matter: positions we send must be in file coordinates, and ranges the server sends back must be
  painted in screen coordinates.
- **A JSON-RPC library, already referenced.** `McpSdk.Shared` and `McpSdk.Protocol` arrive through
  `ZGF.Gui.Desktop` with message types and a pluggable transport. Only the transport interface is
  worth reusing: their request matching brings its own id allocation and error model, and neither
  produces the response type below, so it would be wrapped back into this shape anyway.
- **A place to draw diagnostics.** Diff rows already carry a list of character ranges used for
  intra-line highlighting, drawn independently of syntax colors. Underlines fit that channel. The
  gutter already has icon columns and click handling.
- **Popup positioning.** The tooltip service builds popups from any widget with automatic placement
  and flipping. A hover popup is that, with a markdown view inside.
- **Login-shell `PATH` lookup.** Added for finding `git` on macOS, now shared with server resolution
  through `ServerEnvironment`.
- **Process teardown across platforms.** The terminal already handles process groups and signals on
  Unix and process attributes on Windows.
- **Background work with cancellation.** The Files pane already runs file loads off the UI thread and
  cancels them when the selection moves.

## The hard parts

**Counting lines from zero and from one.** Both screen/file mappings are now typed, but a third
crossing remains and it is the sharpest: the protocol counts lines from **zero**, and everything a
person looks at — gutters, `FileLine`, an error message — counts from **one**. `LspLine.FromOneBased`
and `ToOneBased` are the only crossing, and a test asserts the conversion is not the identity, because
an identity here puts every jump one line off, on a real line, looking exactly like it worked.

The same risk appears again at the parse layer and does not look like a position bug: a definition
result carries both the declaration's whole range and the range of just its name. Taking the wrong one
lands in the right file at the wrong line.

**Nobody owned diagnostics-per-row.** Diagnostics arrive as ranges in file coordinates; the painter
iterates rows and asks what is on the row in front of it. That composer is now
`DiffDiagnosticOverlay`, built per wave and read per row at draw time. Two of its rules are only
visible when they are wrong and were mutation-checked into the suite: a range that ends at character
zero of a line belongs to the newline before it and must not mark that line, and a range with no
width at all — "expected a semicolon here" — draws nothing unless it is widened to a cell.

**A result can be retryable, and that is a type decision made now.** While indexing, a server rejects
requests with a code meaning "ask again", not "failed". Whether that is a case of the response type or
a flavour of error changes the type every caller switches on, so it belongs in the first phase — not
alongside diagnostics, where it first becomes visible. The same type also has to separate *cancelled*
from *failed*: moving the mouse quickly abandons hovers constantly, and an abandoned answer must not
render as an error.

**Coming back to a file starts from nothing.** Diagnostics belong to an open document, so re-selecting
a file looked at ten seconds ago shows a spinner again for as long as the server takes. The app already
solves this for commit details with stale-while-revalidate; the same applies here — show the last known
results dimmed while fresh ones are pending. Still outstanding: the distinction is modelled
(`FileDiagnostics.Answered` separates "not heard back" from "checked and clean") but nothing shows it
yet, so a file being re-checked looks exactly like a file with nothing wrong with it.

**A repository reached through a symlink disowns its own files.** Deciding whether a definition target
is inside the repo is a path comparison, and it also has to see through symlinks and be explicit about
case rather than trusting the platform default. The boundary is built from a resolved root —
`RealPath.Of` already exists for this.

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

**A hover cannot be scrolled.** A popup that follows the pointer has to let the pointer through to
the pane behind it, or it becomes unreachable — the pane reads losing the pointer as the reader
leaving and takes the card away as anyone moves toward it. Passing the pointer through also passes
the wheel through, so a hover longer than its card is clipped rather than scrolled. Fixing it means
letting a popup take wheel events without taking pointer ownership, which the popup layer cannot
express today; it is a framework change, not a change to this feature. The card built in phase 1 is
already a scroll pane with a wheel controller attached, so it will scroll the moment the framework
delivers the events. Until then the card is capped in height and long hovers are clipped.

**Nothing in the app supervised a long-running process.** Restarting a crashed server with backoff,
giving up after repeated failures, shutting down an idle one — none of this had precedent here. It is
now `LanguageServerSupervisor`: it holds no lock, is never re-entered, and takes its clock as a
dependency, so every timing rule is tested without waiting for one.

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

1. **One thing, end to end — done.** Framing, handshake, opening a file and one hover, drawn in a
   popup, against a real spawned process. `ProcessLanguageServer` runs the server;
   `HoverProbeController` turns a dwell in the Files pane into a request and `HoverPopupService` into
   a card. The probe takes its surface, source and presenter as seams, so the pane's own rules are
   tested without a window.
2. **Real server management — done.** `LanguageServerStore` owns per-repo tracking and the
   active-repo-only policy: it starts a server the first time the pane shows a file that server
   handles, forgets servers for repositories that close, and marshals process exit and readiness onto
   the UI thread. `LanguageServerSupervisor` owns the rest — failing a stopped server with its
   reason rather than restarting it, idle
   shutdown for repositories the user has left, the concurrency cap with left-repos-then-LRU
   eviction, and the "started but never became usable" timeout. Readiness carries the indexing
   percentage and only moves forward, so out-of-order progress cannot walk a ready server backwards.
   Status is visible in two places: a chip in the Files pane, and a settings dialog that lists each
   configured server with its state, stops and restarts one, reports config problems with their
   reason, suggests servers for languages present in the repository, and writes or copies a starter
   entry.
3. **Diagnostics — done.** The push had to be plumbed first: the protocol parsed
   `publishDiagnostics` and then dropped it, so nothing above the protocol layer had ever seen one.
   `ILanguageServerQuestions` now carries the wave and the matching close, and
   `LanguageServerConnection` was moved onto `PreviewSession` rather than keeping its own document
   bookkeeping — which is where wave replacement, per-file versioning, closing the previous file and
   discarding a late answer already lived, tested. `LanguageServerStore` publishes what the servers
   say about the file on screen; `FileDiagnostics` checks the path and the open document against each
   other, because a file goes on screen before the server is told about it and the moment in between
   would otherwise show one file's errors under another's name. `DiffDiagnosticOverlay` is the
   per-row owner the plan said still needed one: it clips a range to each line it crosses, converts
   the server's characters to drawn columns, and widens a zero-width range so a server pointing
   between two characters still marks something. `DiffRowPainter` draws a wave under the characters
   and a severity bar down a marker lane at the row's left edge, read at draw time from a layer the
   row set knows nothing about, so a wave arriving three seconds later costs a repaint rather than a
   re-flatten. The lane is reserved on every diff surface whether or not anything ever marks it — a
   lane that appeared when the first wave landed would step the code sideways a second after the
   file opened. A squiggle is readable: `HoverCardText` puts the problems above the type on the same
   card. 97 tests now cover the app-side half of phases 1 to 3.

   Three defects surfaced from running it rather than from testing it, and each one is now pinned:
   a document changing on a pool thread published straight into the view tree; a handshake failing
   on a pool thread raised its ending there too, which reached the same view tree and crashed the
   input system mid-frame as controllers unregistered underneath it; and a server started from the
   settings was never asked anything, so it indexed to 100% and sat there — ready means answered,
   and nothing was doing the asking until the reader happened to hover. The readiness probe also
   gave up after a minute, which stranded the same state on any index slower than that.

   Two things a server says are no longer thrown away. Its error stream is drained rather than
   merely redirected — the pipe filling would have blocked a server that was working — and its last
   words are quoted into the failure, which is the difference between "it stopped" and
   "Unknown binary 'rust-analyzer' in official toolchain". While the glyph column was being freed
   for the marker lane, it also stopped being reserved on whole-file previews that have nothing
   marked as added, where it only ever held a space.
4. **Go to definition — done.** `DefinitionProbeController` turns a Ctrl/Cmd-click or F12 in the
   Files pane into a request and `NavigationBackStack` into a way back; `FileBrowserViewModel` opens
   the destination, expanding the tree to it inside the repository and in a detached preview outside
   it. Both are asked at the first column of the identifier under the pointer rather than at the
   caret position nearest it — the hit-test rounds a pointer in a glyph's trailing half up to the
   next column, which past a word's last character is the whitespace after it, and servers answer
   nothing about that.

   The affordance is the part that had to be built twice. A held modifier washes the symbol under
   the pointer and rules a line under it, the way Rider does: `DiffLineText.IdentifierAt` finds the
   word, `DiffContentView` holds the mark and turns it into a `Hand` cursor, and `DiffRowPainter`
   draws it in the accent the selection already uses — the word keeps its syntax color, because
   recoloring it would fight the highlighter over the one thing on the line the reader is looking
   at. The identifier is found by which glyph the pointer is over, not by where a caret would land:
   caret rounding put the mark on the word *before* the whitespace being pointed at, which reads as
   the highlight lagging the mouse by half a character.

   Nothing is marked until a server has said the symbol goes somewhere. An underline under every
   word the pointer crosses is an offer the click cannot keep, and a reader learns to distrust it
   within a line or two — so the probe dwells 120ms, asks, and marks only on a real target. The
   dwell is what keeps a modifier held across a line from asking about every word it passes: only a
   word the pointer rests on is ever asked about. The span drawn is the server's own
   `originSelectionRange` when it gave one, which is the difference between underlining `bar` and
   underlining the `foo.bar` the server actually resolved; the client's word scan is the fallback,
   and a range that spans two lines or names a different one is ignored rather than drawn.
   `linkSupport` was already being advertised in the handshake, and the answer's origin range was
   already arriving and being dropped one type below where anything could use it.

   Modifier state is read live from the input system rather than remembered from the last key event.
   A chord released while another window had focus is never seen being released here, and the mark
   left behind by one would keep claiming a click the reader means as a text selection. That moved
   the window's modifier bitmask — which the framework already tracked privately, and already
   cleared on focus loss, to stamp wheel events with — onto `InputSystem` as the one authority.

Automatic restart was removed after it was watched failing. A server that will not start is
deterministic — the command is wrong, the toolchain is not installed, the project root is not what
the server wanted — and retrying it three times with growing backoff bought nothing but two minutes
of "Starting…" before a message the first failure already had. The failure now goes straight to the
same operation-error dialog a failed git command uses, carrying the exit code and the server's last
words, and the server stays stopped until the reader retries it. That trade only works because the
reader is told: a silent permanent stop would be worse than a pointless retry, which is why the
dialog landed first.

Deferred with it: a definition link in the diff pane and the review window. Both show a file as it
was at a commit, and a server asked about one would answer about the file on disk.

Deferred: references panel, project-wide symbol search, semantic highlighting.

Still outstanding, and assumed by everything above: the repo has a CI job that builds and tests
across four platforms, but it is still `workflow_dispatch` only. It needs to run on pull requests.
The per-platform process-cleanup test under Testing has nowhere to run until it does.

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

That last rule is why this parser cannot follow the app's other JSON stores. Source-generated
deserialization throws on the first wrong-typed field and discards the whole file — the opposite of
what a hand-edited file needs. This one walks the document by hand, which stays reflection-free and
ahead-of-time safe. An entry is taken whole or not at all: a field of the wrong type disqualifies the
entry rather than being guessed at, because guessing launches a process on a guess.

`rootMarkers` finds the project root by walking up from the file, which is also what makes
submodules and nested projects work. `settings` exists because servers ask the client for their
configuration during startup and we need an answer to give them. Two entries claiming the same file
extension need a defined winner.

## Testing

**A fake server** behaves badly on purpose: never answers, answers too late, exits mid-request, sends
a wrong byte count, sends a huge response, sends results for a file that was never opened, asks for
configuration before startup finishes, and writes plain text to the stream it is supposed to speak a
protocol on. Two cases that look alike and must not be treated alike: a reply to a request we gave up
on is **silent**, while a reply to an id we never issued is **reported**. Conflating them means either
a fault on every timeout or a real server bug going unseen.

**The position mapping** gets its own tests: tab-indented Go, Windows line endings, emoji, CJK text,
mixed tabs and spaces, and collapsed regions. One rule about the fixtures matters more than any single
case: **a fixture whose row index happens to equal its line number cannot catch the two being
confused**, which is the bug this feature is most at risk from. Every such fixture carries chrome rows
or a fold so the two numbers differ.

**Fakes must be able to interleave.** A fake that yields instead of parking never actually overlaps two
operations, so a missing lock passes. Fakes record synchronously, then park, so a test can drive the
overlap deliberately.

**Tests are checked by breaking the code.** Each mutation of a rule must redden at least one test; a
mutation that reddens nothing means the rule is not really covered. This has already caught dead
assertions in every suite written so far, including one on the highest risk in this document.

**The pane's own rules** — when to ask, when to stop, what to do with an answer that arrived too
late — are tested against fakes rather than a server, because that is where the defects were: the
protocol and configuration layers shipped without one, and every bug found by running the app was in
the few hundred lines of glue that had no tests. Driving them needs no window: the probe takes a
surface, a source and a presenter, and a test moves the pointer by calling it.

**Hover, and anything else driven by dwell, is drivable without hands.** `gui_move` places the
pointer through the MCP server, and the input loop leaves a driven pointer alone until a real mouse
actually travels. Before that existed, no hover feature could be verified except by asking someone
to hold their cursor still.

**Process cleanup** gets one test per platform that kills the client and checks nothing survives.

**A repository that does not compile.** `1d-lsp-diagnostics` in the test-repo generator is a Rust
crate and a C file that each fail to build in the same four ways: an unknown name, a type mismatch, a
tab-indented line with an error on it, and a call whose arguments span several rows. Two languages
because the shapes matter more than the language — clangd needs no toolchain component installed and
answers in under a second, so the overlay can be looked at without waiting out an index first. It
also carries working-tree changes — a staged tab-indented hunk, an unstaged one on the same file, a
second file changed only in the tree, and an untracked one — because the marker lane is reserved on
every diff surface and the place a reservation goes wrong is the surface that never uses it.

## Risks

1. **Wrong positions.** Every other failure here is visible: a crashed server shows an error, a slow
   one shows a spinner. A definition that jumps to the wrong line looks like it worked. It has three
   sources, in three different layers — tabs, row-versus-line, and zero-versus-one — plus a fourth at
   the parse layer, where a definition result offers both a declaration's full range and its name's.
   All four are typed and tested before anything uses them.
2. **rust-analyzer's cost.** 1.7 GB and half a minute, in an app that sells on being small and fast
   to start. Mitigated by: nothing runs without a config file, only the active repo's servers run,
   idle servers stop, and there is a visible off switch.
3. **Servers behave differently from each other.** Different readiness signals, different timing,
   different setup requirements. Every request needs a timeout and every failure needs a message a
   user can act on — which is why the server's own error stream is drained and quoted rather than
   redirected and discarded. "It stopped" is not actionable; "Unknown binary 'rust-analyzer' in
   official toolchain" is. Draining it also matters on its own: an unread pipe fills, and a full
   pipe stops a server that was working.
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
