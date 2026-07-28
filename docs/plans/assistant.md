# Assistant — an in-app LLM with real tools

> Plan for adding an LLM assistant to GitBench. **Framing:** the assistant is not a chatbot bolted
> onto the side — it is an agent with a curated set of *domain* tools over `IGitService` and the
> view models, so it changes the app the way a user would, and the user watches it happen in the
> real UI. Phases are ordered so each one produces something runnable and testable before the next
> starts. The first slice ships one entry point (a floating action button + `Ctrl/Cmd+K`) and a
> free-form prompt; contextual quick actions ("Generate title" in the commit bar) come after, built
> as preset prompts on the same pipeline.

## Decisions

Every row below is a settled call, not a default.

| Area | Decision |
|---|---|
| Backend | Direct Messages API, hand-rolled on `HttpClient`, agent loop in C#. `IAssistantBackend` seam so a Claude Code CLI backend can be added later without rework. |
| Tool surface | Domain tools, in-process, over `IGitService` + view models. **Not** the GUI MCP server. |
| Repo scope | Active repo only. Tools are constructed bound to one `Repo`; the assistant cannot reach the others. |
| Approval | Read tools run silently. Every write tool pauses the loop for an inline approve/deny card. |
| Model policy | Per-task tiers, not user-visible. Chat/agent loop on `claude-opus-5`; quick actions on `claude-haiku-4-5`. |
| Chat surface | Non-modal overlay, no scrim, built as a reusable widget so it can be docked or windowed later. |
| Entry point (v1) | FAB + `Ctrl/Cmd+K` only. |
| Icon | New purpose-drawn small dino mark, crisp at 16–24px. |
| Transcript | Streamed text, collapsed one-line tool rows, no thinking content. |
| Conversation | One per repo, in memory, session-only. |
| v1 tools | Reads + staging + set-commit-message + commit. |
| Credentials | OS-native secret store, `ANTHROPIC_API_KEY` honored as fallback. |
| Onboarding | Inline setup card inside the overlay. |
| Rollout | FAB visible to everyone; key prompt on first use. |
| Prompts | Embedded markdown resources, one file per agent. |
| Context | Small live repo-state block each turn; everything else via tools. |

## Why not the GUI MCP server

`GuiMcpServer` (`framework/ZGF.Gui.Desktop/GuiMcpServer.cs`) exposes `gui_snapshot`, `gui_screenshot`,
`gui_click`, `gui_type`, `gui_key` — it drives the app by clicking pixels, over Streamable HTTP on
`127.0.0.1:5577`. That is the right shape for the `/verify` skill and `GitBench.Automation`, and it
stays exactly as it is. It is the wrong shape for a product assistant:

- **Slow and expensive.** A screenshot round-trip per observation, against tools that could return a
  200-token structured diff.
- **Brittle.** The `/verify` skill already documents that virtualized lists aren't clickable by id —
  which is most of the surfaces the assistant would want to touch.
- **Ungateable.** "Reads auto, writes confirm" is meaningless when every action is `gui_click`.

Domain tools give typed arguments, per-tool gating, cheap results, and headless tests via a fake
backend. The GUI MCP server remains the dev/test surface it was built to be.

## Architecture

```
GitBench/Features/Assistant/
  Backend/
    IAssistantBackend.cs        seam: SendAsync(turn, tools, ct) -> IAsyncEnumerable<BackendEvent>
    AnthropicBackend.cs         HttpClient + SSE reader + tool_use loop
    AssistantJsonContext.cs     [JsonSerializable] source-gen context (AOT)
    ModelTier.cs                Chat -> claude-opus-5, Quick -> claude-haiku-4-5
  Agents/
    AgentDefinition.cs          name, system prompt, allowed tool names, model tier
    AgentCatalog.cs             loads embedded .md resources
    general.md                  the v1 free-form agent
  Tools/
    IAssistantTool.cs           Name, Description, JsonSchema, IsWrite, Invoke(args, ct)
    AssistantToolset.cs         built per-repo; filtered by the agent's allowed list
    ReadTools.cs / WriteTools.cs
  AssistantSessionStore.cs      IAssistantSessionStore — one conversation per repo, in memory
  AssistantViewModel.cs
  AssistantPanel.cs             stateless widget: transcript + input + status
  AssistantOverlay.cs           composes AssistantPanel with positioning/animation
  AssistantFab.cs
  AssistantSetupCard.cs
  TranscriptRow.cs / ToolCallRow.cs / ToolApprovalCard.cs
```

Placement follows existing precedent:

- **Per-repo store.** `IAssistantSessionStore` is keyed per repo and registered alongside
  `IRepoSnapshotStore` / `IRepoOperationsStore` in `App/AppServices.cs`. View models project from
  it; nobody else holds conversation state.
- **Widget composition, not inheritance.** `AssistantOverlay` *composes* the stateless
  `AssistantPanel`. A future docked or windowed placement composes the same panel — no abstract
  base, no `Build*()` helpers.
- **Reactive lists.** The transcript is a `Column<TranscriptRow>` bound to a VM `Derived`; if rows
  gain stable identity it becomes `Each<T>`. Never `Raw`.
- **Secret storage belongs in the framework.** OS-native secret access is the same category as
  `IFilePicker` — `ISecretStore` + `AddNativeSecretStore()` land in `ZGF.Gui.Desktop`, with
  DPAPI (Windows), Keychain (macOS), and Secret Service (Linux) implementations. GitBench consumes
  the interface and knows nothing about platforms.

## API specifics that constrain the implementation

Grounded against the current Messages API, not recalled:

- **`claude-opus-5` rejects `temperature`, `top_p`, `top_k`** with a 400. Steer with prompting only.
- **Thinking is on by default** on Opus 5, and `thinking.display` defaults to `"omitted"` — which is
  exactly the transcript treatment chosen (a "Thinking…" pulse with no content), so it costs
  nothing extra. `max_tokens` caps thinking *plus* response text, so it needs headroom.
- **Stream everything.** `max_tokens` above ~16k on a non-streaming request risks SDK/HTTP timeouts.
  Default `max_tokens` to 64000 and read SSE.
- **Parallel tool calls.** One assistant message may contain several `tool_use` blocks. Execute
  them, then return **all** `tool_result` blocks in a **single** user message — splitting them
  across messages trains the model out of parallel calls.
- **Prompt caching is a prefix match**, rendered `tools` → `system` → `messages`. Put
  `cache_control: {type: "ephemeral"}` on the last system block so tools + system cache together,
  and keep the tool list byte-stable (sorted, deterministic serialization). Opus 5's minimum
  cacheable prefix is 512 tokens.
- **The live context block must not sit in the cached prefix.** Opus 5 supports mid-conversation
  system messages — append `{"role": "system", "content": "<repo state>"}` to `messages[]` rather
  than rebuilding the top-level `system`. That is the whole reason the cached prefix survives
  between turns. **Haiku 4.5 does not support this**, so the quick-action tier puts its context in
  the user turn instead.
- **Handle `stop_reason: "refusal"` before reading `content`.** Opus 5 ships elevated cybersecurity
  safeguards and a git client will inevitably show it security-adjacent diffs; a refusal returns
  HTTP 200 with empty or partial content, so indexing `content[0]` unconditionally will break.
  Opt into `fallbacks: "default"` (beta header `server-side-fallback-2026-07-01`) so a policy
  decline is re-served rather than surfaced as a dead turn.
- **No SDK.** The official Anthropic C# package is a large generated dependency with unverified
  NativeAOT behavior, and AOT failures in this repo have historically been debug-invisible
  (`libgit2sharp-callback-apis-crash-aot`). The surface actually needed — messages, SSE, tool loop —
  is small, and hand-rolling it on `HttpClient` with a `JsonSerializerContext` matches the existing
  `PreferencesJsonContext` / `RepoStateJsonContext` pattern and is guaranteed AOT-safe.

## Phases

Each phase compiles, and each produces something that can be exercised on its own.

### Phase 0 — the small mark

Extend `scripts/make-windows-icons.py` (or add a sibling) to emit a simplified dino silhouette
sized for 16–24px: fewer shapes, heavier strokes, no glass or shadow layers. Add it to
`GitBench/Assets/` next to the existing set and load it the way `AppLogo` loads `IconImageId`.
Deliverable: the mark rendered at 18px next to a Lucide glyph, visually checked.

### Phase 1 — backend and tool loop, headless

No UI. `AnthropicBackend` + `AssistantToolset` with **read tools only**:

`get_status`, `get_local_changes`, `get_diff`, `get_commit_history`, `get_commit_details`,
`get_branches`

all thin wrappers over `IGitService.GetStatusSummary` / `GetLocalChanges` / `GetDiff` / `Load` /
`LoadDetails` / `GetBranches` (`GitBench/Git/IGitService.cs:13–75`), bound to one `Repo`.

Tests in `GitBench.Tests` against a real fixture repo, plus a `FakeAssistantBackend` that replays
scripted turns so the loop (parallel tool calls, single tool-result message, refusal handling,
cancellation) is tested without network. **Publish a Release build in this phase, not at the end** —
AOT breakage here must surface while the surface is small.

### Phase 2 — `ISecretStore` in the framework

`ZGF.Gui.Desktop`: interface + `AddNativeSecretStore()`, with DPAPI, Keychain, and Secret Service
implementations. Resolution order: stored secret → `ANTHROPIC_API_KEY` → none.

### Phase 3 — the overlay

`AssistantPanel` (transcript + input + status), `AssistantOverlay`, `AssistantFab`,
`AssistantSetupCard`. Non-modal, no scrim. `Ctrl/Cmd+K` toggles via `AppKeybindController`
(`GitBench/App/AppKeybindController.cs:41`) — `K` is free; `F5`, `Ctrl/Cmd+B`, and `Ctrl/Cmd+1..9`
are taken. `Esc` closes when the overlay holds focus. Streamed text, collapsed `ToolCallRow`s,
`Pulse` for the thinking indicator, `FadeIn` from a skeleton placeholder. Errors render inline in
the transcript, not through `OperationErrorDialog` — a failed turn is conversational.

Widget tests via `GuiTestHarness` with the fake backend.

### Phase 4 — write tools and approval

Add `stage_files`, `unstage_files`, `set_commit_message`, `commit`. `set_commit_message` goes
through `LocalChangesViewModel.SetTitle` / `SetDescription`
(`GitBench/Features/LocalChanges/LocalChangesViewModel.cs:278–282`), **not** around it, so the
commit bar's bindings update normally and the user sees the text land.

`ToolApprovalCard` pauses the loop and renders the tool name and exact arguments with approve/deny.
Deny returns an `is_error` tool result so the model can adapt rather than stalling.

### Phase 5 — quick actions (next slice)

The commit-bar icon in `CommitBarWidget` (`GitBench/Features/LocalChanges/CommitBarWidget.cs`)
opening a popup with "Generate title" and "Chat…". "Generate title" runs a tuned agent — a new
`Agents/commit-title.md` with its own system prompt, `ModelTier.Quick`, and an allowed-tool list of
exactly `get_local_changes` + `get_diff`. Adding it is adding a file.

## Cross-cutting

- **Localization.** Every new string must be added to all six `Strings/*.json` or the source
  generator fails the build with `LOC004`.
- **Build checks.** `dotnet build GitBench\GitBench.csproj --artifacts-path <scratchpad>` — isolated
  outputs, never the default `obj/bin`, and never stop or start the running app.
- **Interruption.** A running turn is cancellable via `CancellationToken`. Sending while a turn runs
  does not queue.
- **No cost UI in v1.** Token counts and spend are not surfaced.
- **FAB visibility.** Hidden on the welcome screen and whenever no repo is active — the toolset
  cannot be constructed without a `Repo`.

## Risks

1. **NativeAOT.** Mitigated by hand-rolling the client and publishing Release in Phase 1. The
   failure mode in this repo is a silent fail-fast in published builds, so this cannot wait.
2. **Linux Secret Service.** The fiddliest of the three secret backends. If it stalls Phase 2, ship
   Linux on the `ANTHROPIC_API_KEY` fallback and finish it separately — the seam already allows it.
3. **Prompt-cache invalidation.** If the live context block leaks into the top-level `system`, every
   turn re-bills the whole prefix. Verify with `usage.cache_read_input_tokens` — a persistent zero
   means an invalidator got in.
4. **Refusals on security-adjacent diffs.** Real for a git client. Handled by the `stop_reason`
   check plus server-side fallbacks.
5. **Approval fatigue.** "Confirm every write" is correct for v1 and will get annoying. The
   deliberate follow-up is per-tool "always allow for this session" with a visible way to see and
   reset grants — not weakening the default.
