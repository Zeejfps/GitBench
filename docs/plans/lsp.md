# Language servers — IDE reading in the Files pane

> **Framing:** the Files pane already reads code. It syntax-highlights it, folds it, outlines its
> declarations and names the enclosing one in a breadcrumb — all from tree-sitter, which parses one
> file with no idea the rest of the repo exists. Everything a *project* knows and a single file
> cannot — this call goes there, this expression is that type, this line does not compile — is the
> gap an LSP client fills.
>
> **Verdict: build it.** The Phase 0 spike has run; its numbers are in
> [Findings](#findings--lsp-under-a-read-only-viewer-measured) and they answer the question the
> feature hung on — real compiler diagnostics do arrive from a single `didOpen` with no edit, ever,
> on all three servers tested. The read-only pane is the reason this is tractable: document
> synchronization is the hardest and buggiest part of any LSP client, and a viewer that never edits
> does not have it.
>
> Two things the spike moved. The orphaned-server risk this plan led with is **largely already
> handled on Windows** — every server exits on stdin EOF — while rust-analyzer's **1.7 GB steady /
> 3.5 GB transient / 32 s cold index** is worse than assumed and now drives the process policy. And a
> review against the code knocked out the plan's central structural claim: `ISymbolExtractor` is
> *not* the seam. What survives is below; the corrections are most of the value.

## Decisions

| Area | Decision |
|---|---|
| Scope of v1 | **Reading only** — hover, diagnostics, go-to-definition. No completion, formatting, rename or code actions: all four are edits, and there is no editor. |
| Document sync | `didOpen` on preview, `didClose` when the cursor leaves. **No `didChange` ever in v1.** Disk is the only writer, so the server's copy and ours cannot diverge — provided we notice the write (D10). |
| Structure | **Four layers, three boundaries**, mirroring the `GitBench.Pty` / `GitBench.Terminal.Vt` split this repo already uses for external-process concerns. See [Architecture](#architecture). Not a second `ISymbolExtractor` (C0). |
| Config | One **user-scoped** `language-servers.json` in `AppPaths.AppDataPath`. Not per-repo — not as a matter of principle, but because this app has no consent surface to hang a trust prompt on (D8). |
| Server sourcing | **None bundled.** The user installs the servers. "Not found" is a designed state with a message, and resolution goes through the login shell's `PATH` (C8). |
| Process policy | **One server per language, for the active repo only.** Not one per (root × server) across every open repo — the measured footprint forbids it (F3). Stopped, not idled, when a repo goes inactive for T; global cap N with LRU eviction. |
| Readiness | A sum type — `NotConfigured \| Starting \| Indexing(progress) \| Ready \| Failed(reason)` — surfaced in the pane, wired to `$/progress`. **Not optional**: a 32-second cold index is indistinguishable from a broken feature without it (F2). |
| Opt-in & discovery | Off until the config file exists; per-repo enable on top. A Language Servers settings card is a **v1 deliverable**, not a follow-up — otherwise the feature ships invisible (D7). |
| Go-to-definition targets | In-repo definitions land in the tree as they do today. Out-of-repo ones — stdlib, `~/.cargo/registry`, `node_modules`, `GOMODCACHE` — open a **detached preview**: the preview target becomes `FromCursor(rowKey) \| Detached(absolutePath)`, the tree cursor clears, and the header names the absolute path. `FileContentLoader.Load` already takes any absolute path, so the loader does not change — only the target's *provenance* decouples from the cursor. |
| Blast radius | The document may **never** light up outside the Files pane. Enforced structurally: the session comes from the Files view-model, not from `ctx`, so no other `DiffRenderState` consumer can reach one (D9). |
| Gating | A `DiffOptions.StructureEnabled`-style flag, so the whole subsystem is one boolean away from off. |
| Out of scope for v1 | `workspace/symbol`, references panel, semantic tokens, call hierarchy, inlay hints, multi-root workspaces, servers over TCP or WebSocket. |

## Architecture

`ISymbolExtractor` is **not** the extension point (C0). Four layers instead:

| Layer | Where | Knows about |
|---|---|---|
| Framing + JSON-RPC correlation | `GitBench.Lsp` (new project) | a `Stream` pair. Nothing else. |
| LSP semantics — payloads parsed into closed types | `GitBench.Lsp` | the protocol. No `Repo`, no process. |
| Lifecycle: config, spawn, roots, per-repo keying | `Features/LanguageServers` | `Repo`, `AppPaths`, `IUiDispatcher` |
| Consumption | `Features/FileBrowser` | a per-document handle, and nothing else |

The Files pane depends on a narrow, document-scoped handle obtained from the store and disposed when
the preview changes — roughly `IReadable<IReadOnlyList<LineDiagnostics>> Diagnostics`,
`Task<Hover?> HoverAt(FileLine, RawCol, CancellationToken)`, `Task<Definition?> DefinitionAt(…)`.
That interface has a trivial null implementation for "no config file", which is what makes the
opt-in decision cheap. Boundaries enforced by an architecture test in both directions, as
`lua-plugins.md` does.

## What already exists — verified against the code

- **A JSON-RPC layer is already a transitive dependency.** `ZGF.Gui.Desktop` references
  `McpSdk.Server`, pulling `McpSdk.Shared` and `McpSdk.Protocol` into `GitBench`
  (`framework/Directory.Packages.props:23-25`). Their assemblies export `JsonRpcTransport`,
  `ITransport`, `ITransportFactory`, `JsonRpcRequest/Response/Notification/Message`, `JsonRpcFraming`.
  Correlation, ids and notification dispatch exist; the framing is line-delimited where LSP needs
  `Content-Length`, and `ITransport` is exactly that seam. The package is ours
  (nuspec `authors: Zeejfps`, `github.com/Zeejfps/EnvMcp`), so extending it is available.
- **The *landing* half of navigation.** `FileBrowserViewModel.NavigateToLine` (`:144`) unfolds,
  reveals, and — via `_pendingReveal` — holds a line while the previewed file is still being read,
  firing `LineRevealRequested` when the text lands (`:495`). Reaching a line in a file the pane
  already shows costs nothing. Reaching a *different* file does not work; see C7.
- **A per-line character-range channel for diagnostics.** `DiffRow.Line.Emphasis` is
  `IReadOnlyList<CharRange>` (`DiffRow.cs:43`), plumbed from `DiffRowSet` and painted as a background
  channel independent of the foreground `Spans`. That is the diagnostic-range shape, and it is
  separate from `TokenSpan.Slot`, a closed `TokenColorSlot` enum with no underline concept. The
  gutter already has glyph columns and hit-testing (`DiffContentView.cs:214, 416, 703-708`).
- **Popup placement for hover.** `PopupTooltipService` is ~60 lines over `IPopupWindowFactory`,
  taking `BuildRoot = ctx => <any widget>.BuildView(ctx)`, with preferred/flipped placement, pooling
  and `MousePassThrough`. A markdown hover is that call with `MarkdownDocumentView` substituted.
- **Login-shell `PATH` resolution** — see C8. Solved four commits ago and directly needed here.
- **Cross-platform child-process teardown plumbing.** `GitBench.Pty/Platforms/Unix/UnixPtySession.cs`
  already does `POSIX_SPAWN_SETSID`, `getpgid` verification (`:464`), a closed `SignalTarget`
  Child/Group hierarchy (`:392-404`) and `kill(-pgid)` teardown (`:406-416`); `WindowsNative.cs` has
  `InitializeProcThreadAttributeList`/`UpdateProcThreadAttribute` (`:93-125`), which is where
  `PROC_THREAD_ATTRIBUTE_JOB_LIST` attaches. Parent-death *binding* is new; the platform plumbing has
  a home.
- **Config mechanics.** `AppPaths.AppDataPath(name)` + `AtomicFile` + four existing
  `JsonSerializerContext`s. All four serialize app-authored state, so the hand-edited case is new.
- **Off the UI thread, already.** `FileBrowserViewModel`'s serial lane with `_previewGeneration` and
  `_previewCancel` is the cancellation model an LSP request needs.

## Corrections — what a first pass got wrong

**C0 — `ISymbolExtractor` is not the seam, and putting this behind it would be a Rule 2 violation.**
It is `FileOutline? Extract(string text, CodeLanguage language)` — synchronous, pure, per-file, no
repo, no cancellation, no push channel. LSP is async, stateful, workspace-scoped and push-based. And
C1 concludes LSP must not produce the outline, so a second implementation would return `null` from
the interface's only method. Worse: it has **38 references across 17 files** in 7 features, including
`Features/Assistant/Tools/AssistantToolset`, `ReadTools`, `ReviewTools` and
`Features/Review/ReviewWindowsViewModel`. Wiring process spawning behind it puts language servers
into the assistant toolset and the review window. Hence [Architecture](#architecture).

**C1 — `documentSymbol` is a downgrade for the outline.** `OutlineNode` carries `ParameterTypes` and
`SignatureEndLine`; the latter is load-bearing at `DiffRowSet.cs:379-400` (fold start) and
`FileBrowserViewModel.cs:480`. LSP's `DocumentSymbol` has `range` and `selectionRange` and nothing
meaning "where the signature stops and the body starts", and its `SymbolKind` has 26 members against
this app's 14. LSP does have `textDocument/foldingRange`, which the first draft missed — but it still
cannot reproduce the signature/body split that drives collapse-onto-the-signature. **Tree-sitter owns
the outline; LSP augments.**

**C2 — `ITooltipService` cannot show a hover.** `Show(object owner, string text, RectF)` — one
string, no markup. LSP hover returns `MarkupContent`, routinely with a fenced code block. No popup
hosts markdown today; the placement machinery to build one does (see above).

**C3 — truncated files must not be opened.** `FileContentLoader.MaxTextBytes` is 2 MB (`:65`) and
`SplitLines(dropLastPartialLine: true)` discards the tail (`:166`). Sending that as the file's
contents produces diagnostics for a file that does not exist. `Highlight()` already refuses on
`truncated` (`:213`), so this matches existing policy.

**C4 — the orphan risk was overstated; the real requirement is narrower and cheaper.** Measured (F4):
killing only the client, with no tree kill, left **no orphan** on any of the three servers — they exit
on **stdin EOF**, not via the LSP `processId` contract (verified by passing `processId: null`). So the
requirement is: **never let anything else hold a handle to the server's stdin**, and never interpose a
wrapper that keeps the pipe open. Job objects / `PR_SET_PDEATHSIG` / `kqueue` remain worth having as
belt-and-braces — stdin-EOF is a server convention, not a guarantee, and Linux and macOS were not
tested — but this is no longer the plan's top risk. Related: **any kill or memory readout must walk
the tree**, because for two of three servers the memory is not in the direct child (tsserver's 442 MB
lives in grandchild node processes; `rust-analyzer` on `PATH` is a 13 MB rustup shim that execs the
real binary).

**C5 — LSP's union types defeat the source generator, and there is no precedent for the fix.**
`Hover.contents` is `MarkedString | MarkedString[] | MarkupContent`; `textDocument/definition` returns
`Location | Location[] | LocationLink[] | null`. `[JsonSerializable]` cannot express that. The first
draft claimed `OpenAiStreamReader` already does this — **it does not**: it deserializes *with* the
source generator (`OpenAiStreamReader.cs:117`) and hand-writes only SSE line framing. There is **no
`Utf8JsonReader` and no `JsonConverter<T>` anywhere in the repo**. These would be the first. The spike
confirms the approach is sound (F5): hand-written readers over `JsonNode`/`Utf8JsonReader` analyze
AOT-clean; the only discipline needed is avoiding `JsonSerializer.Serialize<Dictionary<string,object>>`
and the generic `JsonArray.Add<T>`.

**C6 — CI exists but is manual.** `.github/workflows/` holds **two** files. `build.yml` already runs
`dotnet build GitBench.sln -c Debug` and `dotnet test GitBench.sln -c Debug` over a four-RID matrix —
but `on: workflow_dispatch` only. The conclusion `lua-plugins.md:19` and `glfw-static-linking.md:271`
reach still holds; their premise is stale, and this plan should not repeat it a third time. The
remediation is two trigger lines on a job that already works. That it is now the third plan to hit
this says it should be its own one-page plan.

**C7 — `NavigateToLine` cannot open a file, and that is most of go-to-definition.** `_previewPath` is
derived *solely* from the tree cursor: `SyncPreview` reads `rows[IndexOfCursor(rows)]` and takes its
`FullPath` (`:383-389`), and `NavigateToLine` sets `_pendingReveal` only when `_previewPath` is already
non-null (`:157`). Its one caller, `SelectSymbol` (`:222-227`), jumps within an already-materialized
row. So landing on a definition needs three things, none of which exist: **(a)** for an in-repo target,
expand ancestors (`FileBrowserTree.cs:89,124`), await the serial lane, then `SetCursor` on a row key
that now exists; **(b)** for an out-of-repo target, the detached preview from the Decisions table;
**(c)** a back stack — verified absent everywhere. Narrower than it appears: `ToRelative` (`:520`)
constrains **persisted** state only (`:358,363`) — live rows carry absolute paths — so a detached
preview is representable at runtime today, as long as it never tries to persist as a cursor.

**C8 — the login-shell `PATH` problem is already solved, and this plan needs it.** A macOS GUI app
does not inherit the user's shell `PATH`, so `rust-analyzer` in `~/.cargo/bin` would report "not
found" on machines where it is installed. Commit `28643f8` added `GitProcessRunner.LoginEnvironment()`
(`:297-306`) and `ResolveGitExecutable()` (`:220-236`), which walk the login `PATH` plus
`/opt/homebrew/bin` and `/usr/local/bin`, with an `UninheritedEnvKeys` scrub at `:287`. Both are
`private static` today: the work is promote-and-share, not build.
`DetectPathspecFromFileSupport()` (`:255-280`) is a working precedent for probing an external tool's
version.

**C9 — diagnostics cannot live on `FilePreview.Text`, and spans are frozen at flatten time.**
`DiffRowPainter` never sees `DiffHighlight`: `DiffRowSet` calls `ForLine` (`:197,284,314`) and bakes
the result into `DiffRow.Line.Spans`, which the painter reads at `DiffRowPainter.cs:543`.
`DiffOptions`'s own comment notes a runtime flip "takes effect on the next FlattenRows … not
instantly" (`:19-21`). Meanwhile `publishDiagnostics` is pushed repeatedly while a server indexes —
measured at two and three waves per file (F1). Folding them into the render state means re-flattening
every line per push, and adds a fourth optional field to a record whose own doc comment says it is a
sum type *specifically* to avoid an optional-field bag. **Diagnostics are a separate reactive overlay
keyed by `(uri, version)`, read by the painter at draw time**, coalesced on the `IUiDispatcher`, with
any publish whose version is not on screen dropped. "Same shape as `DiffHighlight`" is true spatially
and false temporally.

**C10 — position mapping is wrong on both axes, silently.** This is the top risk.
`DiffTextPos.Char` is an offset into **tab-expanded** text — `DiffText.ExpandTabs` replaces each tab
with a flat `TabWidth` spaces, and the type's own doc comment says "tabs already expanded". LSP
`character` indexes the **raw** line. gofmt tab-indents every Go file and gopls is server #1, so every
hover and diagnostic on an indented line lands `(TabWidth - 1) × leadingTabs` columns off — and looks
like a working feature. Separately, `DiffTextPos.Row` is an index into the flattened row stream
(banners, hunk separators, tears, folded gaps), not a file line; the source line is only recoverable
from `DiffRow.Line.OldNumber`/`NewNumber`, which are **pre-stringified** (`DiffRow.cs:38-39`). So the
first draft's "a `(line, character)` from a mouse point and nothing else" is where it over-claimed
reuse. **Fix:** one `LspPosition` module, two total functions mapping
`(FileLine, RawCol) ↔ (RowIndex, ExpandedCol)`, with a distinct type per coordinate kind so they
cannot be swapped at a call site — Rule 1's highest-yield newtype here. The good news the draft
missed: .NET string indices are already UTF-16 code units, LSP's default `positionEncoding`, so
surrogate pairs and emoji are correct *for free* — provided nothing converts to runes, which
`DiffText.StepCells` deliberately does for display width. Declare
`general.positionEncodings: ["utf-16"]` and refuse a server that negotiates otherwise (clangd defaults
to utf-8 via its `offsetEncoding` extension).

> The raw↔expanded half of `LspPosition` is being built now, independently: the same root cause is a
> live clipboard bug — `DiffSelectionModel.BuildCopyText` copies expanded text, so copying a
> tab-indented line yields spaces, and a copied Makefile line is broken. `DiffSelectionQuote`
> deliberately shares that function, so the assistant is fed the same mangled text.

## Findings — LSP under a read-only viewer, measured

Standalone harness, `Content-Length` JSON-RPC over stdio, `initialize` → `initialized` → `didOpen`
(disk text) → `hover`/`definition` → `shutdown`/`exit`. **No `didChange` was sent in any run.**
Server→client requests (`workspace/configuration`, `client/registerCapability`,
`window/workDoneProgress/create`) are answered — required, or gopls and tsserver stall.

Projects: the vendored `tree-sitter` Rust workspace (9 crates, 102 files, 53,785 lines);
`golang.org/x/tools@v0.49.0` (1,283 files, 238,766 lines); `tree-sitter/lib/binding_web` (strict TS,
3,243 lines, 224 npm deps).

| | rust-analyzer 1.90 | gopls v0.23 | typescript-language-server |
|---|---|---|---|
| `initialize` round-trip | 15 ms | 56–61 ms | 93 ms |
| First useful hover, cold | **31,812 ms** | 2,368 ms (7,411 ms first-ever) | 648 ms |
| First useful hover, warm | 10,706 ms | — | 634 ms |
| Warm hover / definition | 0 ms | 0 ms | 1–3 ms |
| Diagnostics, broken file | **+1,943 ms** | **+6,634 ms** | **+1,871 ms** |
| Peak WS, server process | 1,739 MB | 754 MB | 38 MB (direct child only) |
| Peak WS, process tree | **3,509 MB** cold | 775 MB | 442 MB across 4 procs |
| `shutdown`+`exit` | yes, 2,147 ms | yes, 38 ms | yes, 4 ms |
| Orphaned on client kill? | **no**, 647 ms | **no**, 71 ms | **no**, 74 ms |
| `$/progress` notifications | 2,983 | 2 | 2 |

**F1 — diagnostics arrive, unambiguously.** rust-analyzer published 5 (`severity: 1`,
`source: "rustc"`, `mismatched types`) because workspace load runs `cargo check` once — no `didSave`,
no `didChangeConfiguration`. gopls published 3 (`source: "compiler"`), tsserver 4
(`source: "typescript"`). Clean files publish `n=0`, so "no errors" is an explicit signal rather than
an absence to time out on. **gopls publishes in two waves** (type-check +2,289 ms `n=0`, analyzers
+5,334 ms `n=2` hints); rust-analyzer republished the same URI three times as flycheck progressed.
Per-URI state must be **replaced, never appended**, and the UI must tolerate diagnostics changing
seconds after a file settles.

**F2 — cold start is rust-analyzer's problem alone**, and it is severe: 31.8 s to first useful hover
with `target/` deleted, 10.7 s warm. Once indexed, hover and definition are 0 ms — the cost is
entirely one-time project load. Two traps, both observed: hover during indexing returns JSON-RPC error
**`-32801 "content modified"`**, which is a *retry* signal, not a failure; and `initialize` returned in
15 ms while the server was still ~30 s from useful, so a successful handshake says nothing about
readiness. The 2,983 progress notifications carry real percentages (`Indexing 85%`), so a determinate
bar is available. gopls and tsserver need no progress UI.

**F3 — memory forbids the obvious process policy.** rust-analyzer holds 1,739 MB for a 54k-line
workspace and does not shrink; cold load spawns a `cargo`/`rustc` build-script storm (40+ transient
children) reaching **3,509 MB working set / 4,113 MB private** across the tree. That transient spike,
not the steady state, is what collides with the user's own build. Hence one server per language for
the active repo only.

**F4 — no orphans on Windows.** See C4.

**F5 — AOT analysis reaches zero warnings.** First pass produced exactly two warned call sites (each
IL2026 + IL3050): `JsonSerializer.Serialize<Dictionary<string,object>>`, and the *generic*
`JsonArray.Add<T>`, which is reachable from ordinary `JsonNode` DOM code and easy to hit by accident.
Replacing them with a hand-built `JsonObject` + `ToJsonString()` and
`((IList<JsonNode?>)arr).Add(...)` gave **0 trim/AOT warnings**. Framing, `JsonNode.Parse`,
`JsonObject` indexer assignment and union-typed readers all analyzed clean.

**Not measured:** Linux and macOS orphan behavior (Windows only — do not assume stdin-EOF transfers);
a truly cold OS page cache; gopls with a cold `GOCACHE`. One caveat: rust-analyzer reported
`Failed to run build scripts of some packages` on the large workspace — hover and definition still
worked, but degraded-workspace states need a reason string in the UI.

## Phases

**Prerequisite (not this feature's cost):** two trigger lines on `build.yml` (C6).

- **1 — One vertical slice.** Framing over `ITransport` + `initialize` + `didOpen` + one `hover`
  against a **hard-coded** server, rendered in a popup. Config for N servers before hover for 1 is
  abstraction ahead of the variation. Includes `LspPosition` (C10) — nothing downstream is
  trustworthy without it.
- **2 — Config, lifecycle, readiness, discovery.** `language-servers.json` → `LanguageServerSpec`
  records, malformed entries dropped individually with a reason. `ILanguageServerStore` keyed per
  repo; the active-repo-only policy from F3; restart-with-backoff and a give-up cap (no supervision
  precedent exists anywhere — this is all new). The readiness sum type and the settings card ship
  here, not later.
- **3 — Diagnostics.** The overlay from C9, `-32801` retry handling, wave-replacement from F1, the
  `Emphasis` range channel and a gutter mark.
- **4 — Go-to-definition.** The largest phase, not the smallest — C7 has the accounting: the
  preview-target sum type and detached header, ancestor expansion + `SetCursor` for in-repo targets, a
  back stack, and the `Location | Location[] | LocationLink[]` reader from C5.

Deferred: references panel, `workspace/symbol`, semantic tokens.

## Config shape

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

Hand-edited, which no JSON file in this app is today — so `ReadCommentHandling.Skip`,
`AllowTrailingCommas`, and a parse error that names the line and is *shown*, not swallowed. The file
is standalone, so a parse failure cannot wipe other settings.

Against Rule 1: `command` is resolved against the **login-shell `PATH`** (C8), absolute paths taken
verbatim, **never through a shell**, `args` passed via `ArgumentList` and never a joined string.
`settings` exists because phase 1 must answer `workspace/configuration` and otherwise has nowhere to
get an answer from. `initializationOptions` is an untyped `JsonElement` hole passed to the wire —
acceptable, but it is a Rule 3 level-1 item and should be flagged as one rather than left looking
typed. Two entries claiming the same extension need a stated winner, or the key should be the app's
`CodeLanguage` enum with unknown names refused at parse.

**D8 — on per-repo config.** A repo-supplied file naming a command to spawn is arbitrary code
execution on clone, in an app whose job is pointing at other people's repositories. But the honest v1
reason is narrower than principle: **this app has no consent surface to hang a trust prompt on** —
`lua-plugins.md` says so explicitly, `ToolApproval` being `internal` to `Features/Assistant` and
rendered as a transcript row. Stated that way, the two plans agree, and per-repo config unlocks the
moment that surface exists.

**D9 — the document may not light up outside the Files pane.** Hover and definition naturally hang off
`DiffContentView`/`IDiffSelectionSurface`, which the diff pane, commit-details tabs and the review
window all share. There the text is a **blob at a commit**, not what is on disk, and a server asked
about it will be confidently wrong. Structural, not conventional: the session comes from the Files
view-model, never from `ctx`.

**D10 — the working tree moving under an open document.** `FileBrowserStore` already re-lists on
`WorkingTreeChangedMessage` (skipping `IndexOnly`), and `RepoReconcileService` fires one every 30 s.
With no `didChange`, "disk is the only writer" is an invariant only if we *notice* the write. Document
identity is `(path, contentVersion)`; a content change is `didClose` + `didOpen` at a new version;
diagnostics whose version is not current are discarded. `didOpen` sends the whole decoded string, not
the `Lines` list. This is the only place the plan's central simplifying assumption can break, so it
gets a named test.

## Testing

A scripted fake server is a **phase 1 deliverable**, with the hostile corpus `lua-plugins.md`
established as the pattern: never answers `initialize`; answers after the timeout; exits mid-request;
garbage before the first header; a header with no `\r\n\r\n`; a `Content-Length` that lies short and
long; a 50 MB hover; a response with an unknown `id`; a notification storm; `publishDiagnostics` for a
file never opened; `null` where `Location[]` was expected; `workspace/configuration` before
`initialize` completes; a server that ignores `shutdown`; `-32801` on every request until the tenth.

`LspPosition` gets its own corpus: tab-indented Go, CRLF, a line with an emoji, a line of CJK, mixed
tabs and spaces, and a folded region.

Plus one process-level test per platform that kills the client and asserts no orphan — the only test
that covers C4, and the one that closes the Linux/macOS gap the spike could not.

## Risks, ranked

1. **Silent position wrongness** (C10). Every other risk here announces itself: an orphan shows in
   Task Manager, a hang shows a spinner, a missing binary shows a message. A diagnostic underlining
   the wrong three characters *looks like the feature working*, in a subsystem whose entire value is
   being right about code.
2. **rust-analyzer's footprint and cold start** (F2, F3) — now measured, and the reason the process
   policy and the readiness UI are decisions rather than nice-to-haves. The identity tension is real:
   "native, no runtime, launches fast" and "spawns a 1.7 GB indexer" are opposed, and the mitigation
   is that no config file means nothing ever spawns.
3. **Server heterogeneity and readiness semantics.** `-32801` as a retry signal, multi-wave
   diagnostics, `initialize` returning long before usefulness, degraded workspaces with failing build
   scripts. Every request needs a timeout and every failure a reason a user can act on.
4. **Orphans on Linux and macOS** — downgraded from #1. Windows is measured safe via stdin EOF; the
   other two are untested, and stdin-EOF is a convention, not a guarantee.
5. **"No problems" is indistinguishable from "no server"** — mitigated by the readiness sum type.
6. **Localization drag.** Lowest: LOC004 is `DiagnosticSeverity.Error`, so a missing translation
   breaks the build, which is the definition of a risk that cannot reach a user.
