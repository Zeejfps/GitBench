# Guided review — an agent walks you through a change

## What this is

Ask "what did this PR change?" and be *shown*, not told: the review window jumps to the first thing
worth seeing, the lines that matter are lit up, a card beside them says what they do and how they
connect to the rest of the codebase, and a Next button hands control back to the agent for the next
stop. The reviewer reads code in DiffDino and the narration in DiffDino; the model runs wherever it
already runs.

Two models can drive it, and this plan makes them the same thing:

- **A terminal agent** — Claude Code, Codex — connected to DiffDino's MCP server. It already has the
  repository, grep, a language server and its own memory, which is most of "how this relates to the
  rest of the codebase". DiffDino is its projector.
- **The built-in assistant**, which already has the review read tools and a `review-branch` agent
  but can only describe a change, never point at it.

The presentation primitives — focus a file at a line, spotlight a range, show a step, wait for the
reviewer — are built once as a store the review window renders from. The MCP server and the
assistant toolset are two thin adapters over that store. Which one narrates is the reviewer's choice
per session, not an architecture decision.

## Why this is affordable

**An assistant tool is already an MCP tool.** `IAssistantTool` (`Tools/IAssistantTool.cs`) is name,
description, a JSON schema literal and `InvokeAsync(JsonElement)`. That is `tools/list` +
`tools/call` under another name. One adapter exports every assistant tool over MCP, so the terminal
agent gets `get_review_stack` / `get_review_diff` / `get_file_at_base` / `mark_viewed` for free, and
every presentation tool written as an `IAssistantTool` reaches both clients from the day it exists.

**The MCP server exists and is the right transport.** `GuiMcpServer` self-hosts Streamable HTTP on
`127.0.0.1:5577/mcp` with McpSdk; `claude mcp add --transport http diffdino http://127.0.0.1:5577/mcp`
and Codex's `[mcp_servers.diffdino] url = ...` both speak it. McpSdk 1.2.0 (`McpSdk.Server.dll`)
carries `WithInstructions`, `WithPromptsCapability` and elicitation, so the server can teach the agent
the walkthrough protocol itself and expose "walk me through this" as a slash command
(`/mcp__diffdino__walkthrough`) with no per-user skill file.

**Navigation half exists.** `ReviewWindowViewModel.ActivateFile` + `ScrollToFileRequested` →
`ReviewDiffListView.ScrollToFile` (`ReviewDiffList.cs:701`) already unfolds and pins a file. Each
section holds its `StartRow` and `DiffRowSet`, and `DiffRowSet.RowFor(DiffRowKey)` /
`RowNearestNewLine` (`DiffRowSet.cs:90,140`) map a file line on either side to a row. Scroll-to-line
is arithmetic on things already in hand.

**Drawing half exists.** Rows are painted by the shared `DiffRowPainter`; `ReviewDiffListView` has a
`PanelOverlay` (`ReviewDiffList.cs:1542`) at z 100 that already draws margins and the sticky header
on top of the rows. A spotlight band and a gutter pin are two more calls in that overlay.
`MarkdownBlockList` renders the assistant transcript, so the step card's prose costs nothing new.

**The reviewer's selection is already a quote.** `DiffSelectionQuote` (path, side, line range, text,
enclosing declaration) is what the "Explain this selection" menu sends to the assistant. Reporting it
to a terminal agent is the same object over a different pipe.

## Decisions

| Area | Decision |
|---|---|
| Who narrates | Both. Shared `ReviewWalkthroughStore` per review window; MCP and the assistant are adapters. MCP ships first — the terminal agent is the stronger model of the codebase today, and the export costs one bridge class. |
| Where the narration renders | In the review window, in a walkthrough rail — not in the terminal, not in the main window's assistant panel. The reviewer should never have to look away from the diff to read the explanation, and Next/Ask live where the code is. The rail is the assistant's whole surface in the review window too: prose the built-in model writes outside a `walkthrough_step` call is shown as a footnote on the current step, never as a second transcript. |
| Step protocol over MCP | A blocking tool that takes a *batch*. `walkthrough_step(steps: [...])` shows the first step, the rail walks Next through the rest locally, and the call returns when the queue drains (`{action: "next"}`), when the reviewer asks a question (`{action: "ask", question, selection?}`), or after a bounded wait (`{action: "pending"}`) so the agent re-calls `walkthrough_wait`. Every return carries `at`, the step the reviewer is on. The agent's common pattern is "plan a few stops, send them, react to questions" — one model turn per stop would leave the reviewer on a spinner at every Next. Blocking is the only mechanism every MCP client supports; elicitation is a later nicety, not the base. |
| Back is local | The store keeps every step it has shown; Back re-shows the previous one from that list without waking the agent. Instant, and the narration stays what it was the first time. Only a Next past the frontier returns control. |
| Actions are latched | A Next or Ask that lands while no waiter is attached — the gap between a `pending` return and the next `walkthrough_wait`, or while the agent is still analysing — is held in a single-slot latch and handed to the next wait call at once. A click is never lost. |
| Bounded wait | 60 s per call, then `pending`. MCP clients enforce per-call timeouts (Claude Code's `MCP_TOOL_TIMEOUT`); a call that waits for a human indefinitely is a call that gets killed mid-walkthrough. |
| Step protocol in-app | Same tool, non-blocking: shows the batch and returns `shown`. The rail's Next past the frontier and Ask post a message the assistant session consumes as the next user turn. The loop already treats a user message as "continue". |
| Spotlight, not selection | Spotlights are a new overlay (band + numbered gutter pin + optional dim), never the text selection. Selection belongs to the reviewer; the agent lighting up lines must not eat their copy/ask gesture. |
| Anchoring | Spotlights and focus are addressed by `(path, side, line)` in file coordinates, resolved to rows at draw time. Rows shift when a gap expands or a file folds; file lines do not. |
| Lines are verified, not trusted | `review_focus` and `review_spotlight` return the text of the lines they resolved. A wrong number shows up in the tool result and the agent corrects itself, instead of the reviewer seeing a pin on the wrong line. `get_review_diff` gains per-line old/new numbers so the model has numbers to give in the first place — today it would have to count from the `@@` header. |
| Dim is scoped | Dimming covers the spotlit file's section only, not the whole stacked diff, and is off unless the call asks for it. A wash over every other file darkens the window for the sake of three lines. |
| Tool surface | Two layers. Low-level: `review_state`, `review_open`, `review_focus`, `review_spotlight`, `review_clear`. High-level: `walkthrough_step` / `walkthrough_wait` / `walkthrough_end`, which compose the low-level ones so the agent's common case is one call per batch. |
| What crosses the MCP boundary | Review reads, presentation tools, `mark_viewed`. **Not** the repository-mutating writes (stage, commit, conflict, push) — the terminal agent has git; exporting those adds an unprompted write path for no gain. |
| Repo addressing | MCP tools take an optional `repo` (name or path; a path inside a worktree matches that worktree). Default is the most recently active review window's repo, else the main window's active repo — not the *focused* window, since while the reviewer types in the terminal no DiffDino window is focused. The instructions text tells the agent to pass its working directory every time. The assistant is already per-repo and passes none. |
| Turning it on | A preference (Agent connections: off / on, port), a status-bar indicator while a client session is open, and a "copy `claude mcp add …`" button in settings. The server issues a session token at start; the copied command carries it as a header, and calls without it are refused. Once a preference keeps the server on, every open repo's files are readable by any local process, and the token is the cheapest thing that closes that. The `ZGF_GUI_MCP` env var stays for scripted runs. `gui_*` input-injection tools stay debug-only (env var), off the preference path. |
| Framework seam | `GuiAppBuilder.UseMcpServer` grows a tool-source hook (`IMcpToolSource` / `Action<DefaultToolsController>`) plus `instructions`, prompts and the token check. The framework keeps zero knowledge of reviews. |
| Walkthrough lifetime | In-memory, per review window, cleared on `walkthrough_end`, window close, or a new `walkthrough_step` after `end`. Nothing persists; a walkthrough is a conversation, not review state. A `walkthrough_step` after the window closed is an error that names `review_open` as the way back, so the agent does not loop on it. |
| Not in this plan | Sampling (`sampling/createMessage`) so DiffDino's own UI can ask the agent's model; multi-window walkthroughs; the Files-pane "open at definition" hop (the [diff-in-context](diff-in-context.md) plan's `OpenFileMessage`); persisting steps; anchoring a spotlight by a text match instead of a line number. |

## The protocol, as the agent sees it

Delivered via `WithInstructions` so it rides `initialize`, and restated by the `walkthrough` prompt.

```
review_state()            → { windows: [{ repo, head, base, active_file, visible: {path, from, to},
                              viewed: [...], selection: DiffSelectionQuote? }], active_repo }
review_open(head, base?)  → opens/focuses the review window for that range (OpenReviewWindowMessage)
review_focus(path, line?, side?)        → activate + scroll so the line sits ~1/3 down the viewport;
                                          returns the text of the line it landed on
review_spotlight([{path, side, from, to, note?}], dim?)   → numbered pins; replaces prior set;
                                          returns each range's text as resolved
review_clear()

walkthrough_step({ steps: [{ title, body_md, focus?, spotlights? }] })
    → shows steps[0]; Next walks the batch in the rail; blocks ≤60 s
    → { action: next | ask | pending | cancelled, at, question?, selection? }
walkthrough_wait()        → same return, for re-entering after `pending`
walkthrough_end(summary_md?)
```

`next` means the reviewer stepped past the last step sent; `at` is where they are. Back never
reaches the agent. `ask` carries the question typed in the rail *and* the reviewer's current
selection quote when there is one, so "what's this?" with three lines selected needs no second call.

## What already exists — verified

- `GuiMcpServer` (`framework/ZGF.Gui.Desktop/GuiMcpServer.cs`): `RegisterTools(DefaultToolsController)`
  is private and fixed; `Run` wraps every tool synchronously. Tools are `IToolHandler` with
  `Task<CallToolResult> Call(IJsonObject, McpRequestContext)`, so an async blocking tool is the
  existing shape, just not used yet.
- `GuiApp.CreateDriver()` (`GuiApp.cs:336`), env-var start at `:356`; `GuiAppBuilder.UseMcpServer(port)`
  at `:78`. `GitBenchAppHost.Create` (`App/GitBenchAppHost.cs:40`) is where the app would add its
  tool source.
- Assistant: `AssistantToolset.ForRepo` builds the per-repo tools; `AssistantAgentLoop.cs:147` pauses
  only on `IsWrite`. Presentation tools are `IsWrite = false` — they change the screen, not the repo.
  `AssistantWriteSurface.OnUiThreadAsync` is the existing hop onto the UI thread that `MarkViewedTool`
  uses; the store must be touched the same way.
- `get_review_diff` (`ReviewTools.cs`, body via `ReadTools.WriteDiffBody`) emits hunk lines as
  `+`/`-`/` ` prefixed strings; only the hunk header carries numbers.
- `AskAssistantAboutSelectionMessage(AgentName, Prompt)` is how a surface injects a user turn; the
  rail's Next/Ask reuses it (`AssistantViewModel.cs:128`).
- `ReviewWindowsViewModel.Windows` (ObservableList) + `FocusRequested` — the registry `review_state`
  enumerates and `review_open` focuses through.
- `ReviewWindowViewModel`: `ActiveFile`, `SelectedPaths`, `ReviewedFiles`, `ActivateFile`,
  `ReportActiveFile`, `Session` (repo + head + base). No line-level API, no selection exposure.
- `ReviewDiffListView`: `Section { File, RowSet, StartRow, BodyRows, Folded }`, `ScrollToFile`,
  `SetScrollTarget`, `PanelOverlay`. Per-file diffs load lazily near the viewport — a focus into an
  unloaded file must wait for its `Render` before a line can resolve to a row.
- McpSdk 1.2.0: `ServerBuilder.WithInstructions`, `WithPromptsCapability`, `ElicitRequest`. The
  framework pins 1.0.0 of `McpSdk.Server` / `StreamableHttpServer` (`framework/Directory.Packages.props`)
  — bump.

## The hard parts

**Blocking a tool on the reviewer.** The tool's `Task` completes from a UI-thread click. A
`TaskCompletionSource<WalkthroughAction>` on the store, resolved by the rail when the reviewer steps
past the batch or asks, timed out by a 60 s timer, and cancelled by window close / `walkthrough_end`
/ a second concurrent `step` (the newer call wins; the older returns `cancelled`). One pending waiter
per window, ever; one latched action per window, consumed by the next waiter. The MCP session dropping
mid-wait must not leave the rail stuck on a step nobody will advance: the session's disconnect cancels
the waiter and the rail shows "agent disconnected" with a Clear button.

**Line → row when the file has not loaded.** `ScrollToFile` works on section geometry that exists
before the diff does (header + placeholder height). `review_focus(path, line)` has to: activate the
file, scroll to its header so the lazy loader picks it up, wait for `Section.Render` to become
`Loaded`, then resolve the row and scroll again. Expose this as one `ScrollToLineRequested(path,
side, line)` on the VM that the list view services, resuming after load if needed. Return from the
tool only when the line is on screen (or after a bounded wait with a clear error), so the agent's
next call — the spotlight — lands on a laid-out row. The return carries the resolved line's text, and
a line the diff does not hold is an error that names the nearest numbered lines.

**Spotlight geometry across folds and gaps.** Store spotlights in file coordinates; resolve rows in
`OnDrawSelf` per section from `RowSet.RowFor(DiffRowKey.NewSide / OldSide)`. A folded section draws
its pins on the header band instead. Dimming is a translucent wash over the rows of the spotlit
section outside the spotlit ranges — drawn in `PanelOverlay` so it needs no painter change, and
skipped entirely when there are no spotlights so the common path costs nothing.

**The rail's place in the window.** The review window is tree | stacked diff. The rail is a third
column on the right, collapsed until the first step arrives, resizable like the tree. It hosts:
step counter, title, `MarkdownBlockList` body, the numbered spotlight notes as a list (click →
`review_focus` on that pin), Back / Next, an Ask field, and the footnote slot for the built-in
assistant's out-of-tool prose. Keyboard: `Space`/`n` Next, `p` Back, `/` focus Ask — registered
through `IKeyMap` like the rest of the review keys.

**Bridging `IAssistantTool` to McpSdk.** Arguments arrive as `IJsonObject`; the tools take
`JsonElement`. Serialize through the System.Text.Json adapter once per call — it is small. The schema
is a raw JSON literal; `Tool(name, desc, ObjectSchema)` wants a model. Either parse the literal into
McpSdk's schema types or add a raw-schema `Tool` constructor to McpSdk (it is ours). The latter is
one line in the package and keeps the schemas byte-identical for both clients.

**The repo argument.** Assistant tools are bound to a `Repo` at construction. The bridge builds a
toolset per MCP call for the resolved repo (cheap: they hold a git service and a repo), so a session
can walk a review in one repo and read a file in another without state.

## Build order

Each step ships on its own.

1. **App-registered MCP tools.** Framework: `UseMcpServer(port, sources)`, `WithInstructions`,
   prompts capability, async tool path, session token; McpSdk bump to 1.2.0. App:
   `AssistantToolMcpBridge` exporting the review reads + `mark_viewed`; per-line numbers in
   `get_review_diff`; the Agent connections preference, status-bar indicator, copy-command button.
   *Payoff:* Claude Code can already answer "what did this PR change" from DiffDino's own range
   resolution, and mark files viewed as it goes; the built-in assistant can cite line numbers.
2. **Where and what.** `review_state`, `review_open`, `review_focus` (file-level — reuses
   `ScrollToFile`). `DiffSelectionQuote` surfaced on `ReviewWindowViewModel` from the list view's
   selection controller. *Payoff:* "show me the file this finding is in" works; "what's this?" over a
   selection works from the terminal.
3. **Lines.** `ScrollToLineRequested` with the wait-for-load path; `review_focus` gains `line` and
   echoes the resolved text.
4. **Spotlights.** `ReviewSpotlightSet` on the store, overlay drawing (band, pin, section-scoped
   dim), `review_spotlight` / `review_clear` with resolved-text returns. GuiTestHarness draw-capture
   tests.
5. **The rail and the step loop.** `ReviewWalkthroughStore` (step list, current, waiter, latch),
   `ReviewWalkthroughRail` widget with local Back and batch Next, `walkthrough_step` / `wait` / `end`,
   the `walkthrough` MCP prompt, the server instructions text. *Payoff:* the feature as envisioned,
   from the terminal.
6. **The assistant as second narrator.** Register the presentation tools in `AssistantToolset`; a
   `walkthrough-review.md` agent; the rail's frontier-Next/Ask broadcast
   `AskAssistantAboutSelectionMessage` when the walkthrough was started by the assistant rather than
   an MCP session; the model's out-of-tool prose routed to the rail's footnote; a "Walk me through
   this" entry in the review window's header.

## Testing

- **Store:** step/next/ask/timeout/cancel state machine, one-waiter rule, disconnect cancels; a batch
  advances locally and returns `next` only past the frontier; Back re-shows without touching the
  waiter; an action with no waiter attached is latched and handed to the next wait — plain unit
  tests, no UI.
- **Bridge:** `tools/list` from an in-proc McpSdk client equals the assistant toolset's names and
  schemas byte-for-byte; a `tools/call` round-trips `JsonElement` args and error results; a call
  without the token is refused.
- **Line resolution:** focus into an unloaded section resolves after load; fold/expand keeps the
  spotlight on the same file line (GuiTestHarness + `RecordingCanvas` for the band's position); a
  focus/spotlight result quotes the text of the lines it resolved; an off-by-one is an error naming
  the neighbours.
- **Rail:** keys route via `IKeyMap`; Ask carries the current selection quote; RTL root wraps
  `Direction` (secondary window rule).
- **Manual:** `claude mcp add`, `/mcp__diffdino__walkthrough` on a real branch; the `verify` skill
  drives the window over the same server.

## Risks

- **Client tool timeouts.** If a client's limit is under 60 s the bounded wait must be shorter; make
  it a server-side constant the instructions text quotes, not a tool argument the model has to get
  right.
- **Two narrators at once.** An MCP session and the assistant both stepping the same window. The
  store has one waiter; the second `step` cancels the first and the rail shows who is driving. Good
  enough for one person at one desk.
- **Rail width.** Three columns in a review window on a laptop. The rail collapses to a strip when
  the window is narrow, and the step body is readable in the transcript-style popover from the
  strip. Decide on real hardware before polishing.
- **Batch size.** An agent that sends twenty steps at once has committed to a narration it cannot
  revise after the reviewer's first question. The instructions text asks for two to four stops per
  call; the store does not cap it.

## Deliberately not doing

- Letting the agent *edit* through DiffDino. Fix suggestions render in the step body as a code block;
  applying them is the terminal agent's job with its own tools and permissions.
- Persisting walkthroughs or replaying them. A walkthrough is a conversation with a model, and the
  model is not in the file.
- A GitHub PR import. "This PR" is the checked-out branch's review range; fetching a PR branch is a
  `gh pr checkout` the terminal agent already knows how to do.
