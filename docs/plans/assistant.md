# Assistant — an in-app LLM with real tools

> Plan for adding an LLM assistant to GitBench. **Framing:** the assistant is not a chatbot bolted
> onto the side — it is an agent with a curated set of *domain* tools over `IGitService` and the
> view models, so it changes the app the way a user would, and the user watches it happen in the
> real UI. Phases are ordered so each one produces something runnable and testable before the next
> starts. The first slice ships one entry point (a toolbar button + `Ctrl/Cmd+K`) and a free-form
> prompt; contextual quick actions ("Generate title" in the commit bar) come after, built as preset
> prompts on the same pipeline.

## Decisions

Every row below is a settled call, not a default.

| Area | Decision |
|---|---|
| Backend | Direct Messages API, hand-rolled on `HttpClient`, agent loop in C#. `IAssistantBackend` seam so a Claude Code CLI backend can be added later without rework. |
| Tool surface | Domain tools, in-process, over `IGitService` + view models. **Not** the GUI MCP server. |
| Repo scope | Active repo only. Tools are constructed bound to one `Repo`; the assistant cannot reach the others. |
| Approval | Read tools run silently. Every write tool pauses the loop for an inline approve/deny card. |
| Model policy | Per-task tiers. Chat/agent loop on `claude-opus-5`; quick actions on `claude-haiku-4-5`. Tiers are not user-visible through Phase 5; from Phase 6 the provider and model become user-selectable and the tier→model mapping is per provider. |
| Chat surface | Non-modal overlay, no scrim, built as a reusable widget so it can be docked or windowed later. |
| Entry point (v1) | Toolbar button + `Ctrl/Cmd+K` only. **Revised during Phase 3** — a floating action button covered the Commit button, so the entry point moved into the existing actions toolbar. |
| Icon | New purpose-drawn small dino mark, crisp at 16–24px. |
| Transcript | Streamed text, collapsed one-line tool rows, no thinking content. |
| Conversation | One per repo, in memory, session-only. Kept across repo switches for the app's lifetime; cleared only by an explicit header action (Phase 7). |
| v1 tools | Reads + staging + set-commit-message + commit. |
| Credentials | OS-native secret store, keyed per provider. `ANTHROPIC_API_KEY` honored as fallback (`OPENAI_API_KEY` from Phase 6). |
| Onboarding | Inline setup card inside the overlay. The key field is **masked**, and a masked field refuses the clipboard entirely — copy and cut are declined, so a secret cannot be pasted somewhere it persists. A saved key pre-fills the field; an environment-only key does not (filling it would imply it was saved). |
| Rollout | Toolbar button visible to everyone; key prompt on first use. |
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
  AssistantToolbarButton.cs
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

`AssistantPanel` (transcript + input + status), `AssistantOverlay`, `AssistantToolbarButton`,
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

### Phase 6 — other providers

Behind the existing `IAssistantBackend` seam, which was put there for exactly this. Scope is
**OpenAI-compatible endpoints**: one implementation reaches OpenAI, Ollama, LM Studio, OpenRouter,
Groq, Together and vLLM, because they all speak `/v1/chat/completions`. Gemini's native API and the
Claude Code CLI backend stay out of this slice.

`OpenAiCompatibleBackend` is not a reskin of `AnthropicBackend`. The wire format differs in ways
that reach into the loop:

| | Anthropic | OpenAI-compatible |
|---|---|---|
| System prompt | top-level `system` array carrying `cache_control` | a `role: "system"` message at the head of `messages[]` |
| Tool declaration | `{name, description, input_schema}` | `{type: "function", function: {name, description, parameters}}` |
| Tool call | `tool_use` block; `input` is an object | `tool_calls[]`; `function.arguments` is a **JSON-encoded string** |
| Tool results | **all** results in one `user` message | **one `role: "tool"` message per result**, each with `tool_call_id` |
| Stream end | `message_stop` event | `data: [DONE]` sentinel |
| Stop reason | `stop_reason`, including `refusal` | `finish_reason`: `stop` / `length` / `tool_calls` / `content_filter` |
| Token cap | `max_tokens` | `max_tokens`, or `max_completion_tokens` on newer models |
| Caching | explicit `cache_control` breakpoint | implicit or absent; no breakpoint to place |

**The tool-result rule inverts.** Phase 1 deliberately made splitting results *unrepresentable*
because Anthropic trains the model out of parallel calls when they are split. OpenAI-compatible
endpoints require exactly that split. So `AssistantMessage.ToolResults` stays one logical message
carrying a list, and each backend's writer renders it its own way — the invariant belongs to the
writer, not the conversation model. Do not weaken the model to accommodate the second backend.

There is no mid-conversation `system` equivalent here, so the live repo-state block goes in the user
turn — the same fallback `ModelTier.Quick` already takes for Haiku.

Also in scope:

- **Provider registry.** `AssistantProvider` — id, display name, base URL, auth scheme, default
  model per tier, and capability flags (tool calling, streaming, where the system prompt goes).
  `ModelTier` stops being two hard-coded ids and becomes a per-provider lookup.
- **Credentials per provider.** `AssistantCredentials` hardcodes `ANTHROPIC_API_KEY` today.
  Generalise to a per-provider secret name in `ISecretStore` plus a per-provider env fallback
  (`OPENAI_API_KEY`, and none at all for a local Ollama that needs no key).
- **Settings UI.** Provider picker, model field, base-URL override for self-hosted endpoints, and
  per-provider key entry. The Phase 3 setup card stops assuming a single Anthropic key.
- **Degradation.** Many local models have weak or absent tool calling. When a provider returns no
  `tool_calls` after tools were offered, say so inline in the transcript rather than looping
  silently.

Tests extend `FakeAssistantBackend` to the OpenAI framing, plus wire-format tests asserting the
split tool-result messages, the JSON-string `arguments` decode, and `[DONE]` termination. No
network. Re-publish Release AOT — a second backend is new serialization surface.

### Phase 6b — provider follow-ups found by using it

Four things surfaced once the provider work was in a running app. The first is a live bug.

**`reasoning_effort` and tools are mutually exclusive on OpenAI.** Sending both errors, so a
reasoning model plus a domain toolset — which is every turn this assistant makes — fails outright.
This belongs in the **backend**, not in the user's head: they picked a model from a list, and the
request body is not their problem. Put it in `AssistantProvider` as a capability the writer honours,
the same shape as the `MaxOutputTokens` ceiling already there.

**Corrected while implementing — the paragraph above had the direction backwards.** It said to omit
`reasoning_effort`, but omitting was already the behaviour: the string appears nowhere in the source
and nowhere in the git history, verified by `git grep` and `git log -S`. OpenAI's current models
**reason by default**, so the incompatibility fires on a request that never mentions the field, and
the provider's own error names the remedy: *"set reasoning_effort to 'none'"*. Silence is the bug.
So the rule is inverted — **a request that carries tools states the opt-out its provider declares**,
`"none"` on OpenAI and nothing elsewhere. Groq, xAI and OpenRouter accept reasoning *alongside*
tools, so forcing `none` there would discard quality for nothing. The wire test asserts the presence,
not the absence: an earlier draft of this line demanded "a request with tools carries no
`reasoning_effort`", which would have pinned the broken behaviour in place.

**The cost, which is not free.** `reasoning_effort: "none"` disables reasoning for tool turns, and
every turn here carries tools — so OpenAI reasoning models now run non-reasoning. The only way to
keep both is `/v1/responses`, a third wire format and its own slice of work. Today's alternative is
a 100% failure rate, so this ships, but the follow-up is real.

The general rule this is an instance of: *a provider's incompatibilities are the writer's problem.*
Anything that makes a request 400 for a combination the UI allows is a backend bug, not a
documentation note.

**Pre-fill the key field instead of describing it.** The settings card currently shows a hint line
("A key is saved for Anthropic."). Fill the masked field with the saved key instead — the field is
masked and now refuses the clipboard, so the value is visible only as bullets and cannot be
exfiltrated by selecting it.

One case does not fit and must not be papered over: a key that comes only from the **environment**
is not saved, and filling the box with it would imply otherwise — clearing the box would then look
like it removes a key the app never owned. Keep the hint line for that case, and pre-fill only what
`SavedFor(provider)` actually returns. The `AssistantKeySource` enum already distinguishes them.

**Preset models per provider, with manual entry intact.** Typing a model id from memory is the wrong
default; the ids are long, versioned, and a typo yields an opaque 404 from the endpoint. Give each
known provider a short list of current models and let the picker offer them, while keeping the free
-text field for anything not listed — self-hosted endpoints and new releases must stay reachable
without an app update.

- The list is a **default, not a whitelist**. A model the user types that isn't in the list is
  accepted as typed.
- Local providers (Ollama, LM Studio, vLLM) serve whatever the user pulled, so a fixed list there is
  a guess at best. Either query the endpoint's `/v1/models` or leave those free-text and say so.
- The per-tier defaults in `AssistantProvider` are already a data table of guessed ids; these presets
  are the same table widened, so keep one source rather than two lists that drift.

**Model ids are guesses and should be checked.** The defaults shipped in Phase 6 (`gpt-5`,
`llama-3.3-70b-versatile`, `llama3.1`, …) were written without verification. Confirm them against
what each provider actually serves before the preset list makes them prominent.

### Phase 7 — making the overlay a real panel

The overlay is fixed in size and position today. This phase gives it the affordances a window has —
resize, move, selectable text, and a transcript that stays readable — plus the framework capabilities
those need.

**Resizing.** Drag a **corner** to resize both axes, drag a **side** to resize one, with the pointer
changing shape over each grip.

The framework already has the seam. `IProvidesCursor`
(`framework/ZGF.Gui.Desktop/Input/IProvidesCursor.cs`) lets a controller request a cursor while it is
hovered or capturing the pointer, and the input system pushes it to the window once per frame. So
this is a controller with hit zones along the edges, not a new input mechanism.

**The one gap is the corner cursor.** `MouseCursor` (`framework/ZGF.Desktop/MouseCursor.cs`) has
`ResizeHorizontal` and `ResizeVertical` but no diagonals, because the bundled `Glfw.NET` binding
stops at GLFW 3.3's cursor set. GLFW 3.4 added `GLFW_RESIZE_NWSE_CURSOR` (0x00036007) and
`GLFW_RESIZE_NESW_CURSOR` (0x00036008) as plain constants, so extending `CursorType`, `MouseCursor`
and `GlfwStandardCursors` is mechanical — but it only *renders* if the native GLFW is 3.4 or newer.
Verify at runtime that `glfwCreateStandardCursor` returns non-null and fall back to the nearest axis
cursor if it doesn't, rather than leaving the pointer stuck on an arrow with no explanation.

Also in scope:

- **Clamping.** A minimum that keeps the composer and at least a few transcript rows usable, and a
  maximum bounded by the window. Resizing must never let the panel exceed its host or collapse the
  send affordance off-screen.
- **Persistence.** Remember the size in preferences (`PreferencesStore`), the way a window size is
  remembered — not per session, and not per repo.
- **RTL.** Edge hit zones are mirrored; `Direction.Wrap` already governs the overlay root.
- Widget tests via `GuiTestHarness`: dragging each grip resizes the expected axis, clamps hold at
  both bounds, and the requested cursor matches the zone under the pointer.

**Moving the panel.** The overlay should be draggable within the main window, and its position
remembered.

- **Drag by a header strip, never the body.** This phase also makes transcript text selectable and
  the transcript already scrolls, so a drag-anywhere panel would fight both — press-and-drag over a
  reply must select text, not move the window. Give the panel an explicit grab area (its header /
  title region) and confine dragging to that.
- **Clamp inside the host window.** The panel must not be draggable off-screen or under the window
  chrome. Keep it fully within bounds, the same clamping the resize grips use.
- **Re-clamp when the host resizes.** A position saved at one window size can put the panel off-screen
  at a smaller one — restore-then-shrink is the case that bites. Re-clamp on restore *and* whenever
  the host window changes size, rather than trusting the stored value.
- **Persist alongside the size** in `PreferencesStore` — one stored placement, not per session and
  not per repo.
- **RTL.** Position is mirrored with the rest of the overlay root.

Tests: dragging the header moves the panel and dragging the transcript does not; the panel clamps at
every edge; a stored position outside a shrunken window is corrected rather than lost.

**Selectable transcript text.** The user must be able to select an assistant reply and copy it —
part of a message, not just the whole thing. Today the transcript renders through `Text`, which has
no selection, and the framework's only selection lives in `TextInputView`, an editable input with no
read-only mode. So this is a framework capability, not a widget swap.

Prefer teaching `TextInputView` a read-only mode over building a second text view: selection
rendering, clipboard wiring (`BaseTextInputKbmController` already takes an `IClipboard`), and
keyboard navigation all exist there and would otherwise be duplicated. Read-only must suppress every
edit path — typed characters, paste, delete, and IME composition — while leaving selection, copy and
caret navigation intact, and it must not capture Tab or fight the composer for focus.

Weigh that against the cheap alternative before committing: a per-message copy affordance needs no
framework change at all. It is worse — it can only copy a whole message — so take it only if
read-only selection turns out to be genuinely invasive, and say so rather than quietly substituting.

**Depends on the overlay consuming pointer input** (fixed in Phase 4). While events fall through to
the layer beneath, a drag inside the transcript selects the *diff view*, so selection here cannot
work until that lands.

**Group consecutive tool calls into one expandable row.** Today `TranscriptRow` maps every
`AssistantRowKind.Tool` to its own `ToolCallRow`, so a turn that reads six files spends six lines
saying so and pushes the actual answer off-screen. Collapse a *run* of adjacent tool rows into a
single summary row the reader can expand to see the individual calls.

- **Grouping is by adjacency.** A run ends at the first non-tool row — a message, a notice, an
  approval card. Do not group across an answer.
- **Failures stay visible collapsed.** `ToolCallRow` already distinguishes a failed call with
  `TriangleAlert` and danger colour. A collapsed group that looks calm while hiding a failure is
  worse than the six lines it replaced, so the summary must carry that signal.
- **Expansion state belongs in session state**, not local widget state — the overlay is non-modal and
  can be closed and reopened, and rows re-render on every streamed delta. Same lesson as the pending
  approval in Phase 4.
- **Default collapsed**, and a group that is still growing mid-turn stays collapsed while its count
  climbs.
- **Do not break row identity.** The transcript is `Each<AssistantRow>` with a per-row
  `State<string>` precisely so a streamed delta repaints one row instead of reseeding the list.
  Grouping must produce stable keys per group; rebuilding the group list every render would undo
  that and make streaming thrash.

Tests: a run of N tool calls renders one row; expanding shows N; a message between two runs yields
two groups; a failed call is signalled while collapsed; expansion survives a close/reopen of the
overlay.

**Clearing the conversation.** Conversations already persist the way they should — `_sessions` in
`AssistantSessionStore` is keyed by repo id and only emptied on `Dispose`, so switching repos and
coming back returns to the same exchange for the app's lifetime. What's missing is the other
direction: **there is no way to start over.** A long thread eventually carries stale context the
model keeps reasoning from, and the only escape today is restarting the app.

Add a clear affordance to the panel header — the same strip Phase 7 introduces as the drag handle.

- **Clears the active repo's conversation only.** The store is per repo; a clear that wiped every
  repo's thread would violate the model the user already relies on. One repo, the one on screen.
- **Cancel any running turn first.** Clearing mid-turn must not leave a stream writing into a
  transcript that no longer exists. The `CancellationToken` from the Interruption rule is already
  there — use it, and clear only once the turn is actually down.
- **Clear the session's other state too**, not just the rows: the pending approval from Phase 4 and
  the tool-group expansion state from this phase both live in session state and would otherwise
  survive as orphans pointing at rows that are gone.
- **No confirmation dialog, but the action must be recoverable or plainly labelled.** A modal for
  clearing a chat is heavy, and this app's precedent is that destructive-but-cheap actions just
  happen. But session history is unrecoverable once gone. Prefer an undo affordance over a confirm
  dialog — `IToastService` in `Features/Notifications/`, whose `ToastIntent` takes a `ToastAction`
  built for exactly this; if undo proves awkward, a confirm is the acceptable fallback — silently
  discarding with neither is not. (An earlier draft of this line named a `StatusFeedbackService`
  status-bar slot. **No such type exists in this repo** — it was a bad recollection, and a subagent
  hit the dead name before grep settled it.)
- **The key is not a session.** Clearing the conversation must not touch the stored API key or
  `IsConfigured` (renamed from `HasApiKey` in Phase 6), or the setup card will reappear as though
  the user had been signed out.

Tests: clearing empties the active repo's rows and leaves another repo's thread intact; a running
turn is cancelled before the rows go; pending approval and expansion state are dropped with them;
`IsConfigured` is unaffected.

### Phase 8 — code review: tools and ask-about-this-selection

Everything so far points the assistant at the *working tree* — status, staging, a commit message.
This phase points it at **code under review**, and adds the affordance that makes that natural: the
user highlights something in a diff and asks about it directly, instead of retyping it into the
overlay.

**Ask about the selection.** The diff already has real text selection, and the extraction already
exists: `DiffSelectionModel.BuildCopyText(rows, start, end)` is a pure function over
`IDiffSelectionSurface.RowsOf(scope)` (`GitBench/Features/Diff/DiffSelectionController.cs:16`,
copy path at `:271`). So the quick action reuses the copy pipeline — **do not write a second
extractor.** A context menu on a live selection offers "Ask about this", and the assistant receives:

- the selected text,
- the file path and the line range it came from,
- which side of the diff it is (added / removed / context) — a question about a removed line means
  something different from one about an added line, and a bare string loses that.

Preset prompts on the same pipeline as Phase 5, not a new mechanism: "Explain this", "What could
break?", "Suggest a fix", plus a free-form "Ask…" that opens the overlay pre-seeded with the
selection quoted. The presets are `Agents/*.md` files, same as `commit-title.md`.

**Two structural problems to solve before writing any of it.** Both were settled when Phase 8 was
briefed: **(1)** main-window diff only, review window as its own follow-up; **(2)** presets run
detached, "Ask…" continues the per-repo thread. The reasoning is preserved below.

1. **Selection lives in two windows, and the assistant lives in one.** Both `DiffContentView`
   (main window) and `ReviewDiffList` (review window) implement `IDiffSelectionSurface`, but the
   overlay is a child of the *main* window and the review window is a separate OS window. "Ask about
   this" from a review has nowhere to render today. Decide explicitly, don't discover it late — the
   options are to scope this phase to the main-window diff only, to give the review window its own
   overlay instance (the panel is a reusable stateless widget precisely so this is possible), or to
   focus the main window and seed it there, which is jarring mid-review. **Recommendation: ship the
   main-window diff first, and treat the review window as its own follow-up** rather than
   half-wiring it.
2. **The conversation is per repo, and a selection is per file.** `IAssistantSessionStore` keys one
   conversation to a `Repo`. Asking about a selection either continues that conversation (context
   accumulates, which is good for follow-ups and bad for a one-shot "explain this") or runs
   detached like `commit-title`. Presets should run detached and render their answer in the
   overlay; "Ask…" should continue the conversation. Say which is which per action.

**Review tools.** Bound to the same `Repo`, wrapping the review surfaces that already exist:

`get_review_stack` (files in the current review, their status and the base ref — over
`IReviewStackSource`), `get_review_diff` (one file's diff at the review's base, not `HEAD`), and
`get_file_at_base` so the model can compare against the pre-change content rather than guessing what
was there.

These are **reads** and run silently under the existing policy. `mark_viewed` is the one write
worth having — it goes through `IReviewProgressStore.SetViewed`
(`GitBench/Features/Review/ReviewProgressStore.cs:18`) with its `contentId` fingerprint, **not**
around it, so a mark made by the assistant invalidates on re-review exactly like a human's. It
takes an approval card like every other write.

**Reading whole files — and the scope decision it forces.**

The plan so far has a clean property: every tool is a thin wrapper over `IGitService` bound to one
`Repo`, so the assistant's reach is *the git data of the active repo* and nothing else. A
`read_file` tool that takes a path and returns bytes breaks that property, because the working tree
contains things git never shows the model in a diff — `.env`, `id_rsa`, `secrets.json`, an
untracked scratch file with a production token in it.

This matters more than it looks, because **reads run silently**. Under the current approval policy
a `read_file` tool would exfiltrate a credentials file to the model with no card, no prompt, and one
collapsed transcript row saying `read_file`. That is a real change in the app's security posture and
it should be a deliberate decision, not a side effect of "the model should see more context".

**Settled before implementing** (confirmed when Phase 8 was briefed — treat as decided, not open):

- **Restrict to tracked files.** Resolve the path through git, not the filesystem — if git doesn't
  know the file, the tool refuses. That single rule excludes the whole untracked-secrets category
  and costs almost nothing, since reviewing code means reading tracked code.
- **Refuse paths outside the repo root** after normalization, including `..` traversal and symlinks
  that escape. Path handling is where this kind of tool usually leaks.
- **Honour `.gitignore` even for tracked files** where they conflict, and refuse anything matching a
  small deny list of credential-shaped names regardless of tracked status.
- **Cap the response** and return a range rather than a whole 20k-line file; the model asked a
  question about a selection, not for the repo.
- Leave it a silent read *only* if all of the above hold. If the decision is instead to allow
  arbitrary working-tree reads, then `read_file` must take an approval card, and the "reads are
  silent" rule in the Decisions table needs amending to say so.

Tests: the selection→prompt payload carries path, line range and side; `BuildCopyText` is reused
rather than reimplemented; presets run detached and "Ask…" continues the conversation; `mark_viewed`
routes through `IReviewProgressStore` and its mark invalidates on a content change; and a table of
path-escape attempts (`../`, absolute, symlink-out, untracked, gitignored, deny-listed) each refuse
with an `is_error` result the model can read.

### Phase 8b — keeping several providers configured and swapping between them

The want, in the user's words: *"I may have Anthropic and OpenAI keys and I should be able to swap
between them if I wanted to."*

**Half of this already works, and must not be rebuilt.** Keys are already stored per provider —
`AssistantCredentials` reads and writes `provider.SecretName` (`AssistantProvider.cs:81`, `Id +
"-api-key"`), so `anthropic-api-key` and `openai-api-key` are separate entries in the OS secret
store and saving one never clobbers the other. Multiple saved keys is a property the app has today.
Do not add a second credential store, and do not change `SecretName` — that would orphan every key
already saved.

What is missing is the **round trip**. Three concrete gaps:

1. **The provider's model and endpoint are not remembered per provider.** `AssistantSettings` is a
   single record of `(ProviderId, Model, BaseUrl)` persisted as three flat preference fields
   (`Preferences.cs:34–39`). Switch Anthropic → OpenAI and back, and the model you had chosen for
   Anthropic is gone — the record only ever holds the *current* provider's overrides, and its own
   `<summary>` says so deliberately. That was right when a switch was a one-time setup act; it is
   wrong once switching is a thing you do. Persist the overrides **keyed by provider id** so each
   provider keeps its own model and base URL, and selecting one restores what you last used there.
   Migrate the existing three flat fields onto the current provider's entry rather than dropping
   them — a user who configured a model before this phase must not lose it.
2. **There is no fast way to swap.** Changing provider today means opening the settings card, which
   takes the composer's place. Swapping between two configured providers should not require a
   settings trip: put a switcher in the **panel header** — the same strip that Phase 7 gave the
   drag handle and the clear action. It lists the providers that are actually usable (a saved key,
   or `RequiresApiKey == false` like Ollama), marks the active one with `Item.Checked` per
   `context-menu-active-selection`, and offers one entry that opens the settings card for anything
   not yet configured. A provider with no key must not be silently selectable into a broken state.
3. **Swapping mid-conversation is not obviously safe, and must be checked rather than assumed.**
   `AssistantContent.ToolUse.Id` is issued by whichever provider produced it — `toolu_…` from
   Anthropic, `call_…` from OpenAI — and the whole conversation is replayed on every turn. After a
   switch, the new endpoint receives assistant messages carrying tool-call ids it never issued. It
   may well accept them, since the id only has to match between the assistant message and its
   `tool` result *within the same payload*, but that is a guess about someone else's validator and
   third-party OpenAI-compatible proxies are stricter than OpenAI itself. **Establish which it is
   before choosing a behaviour**, then pick one and say why:
   - keep the thread as-is (only if replay genuinely round-trips),
   - keep the transcript visible but start the *sent* conversation fresh from the switch point,
     with a notice row saying the history was not carried over,
   - or clear on switch, which is the heaviest and needs the Phase 7 undo affordance.

   Whichever it is, it belongs in code, not in a caveat: the user picked a provider from a list and
   the wire payload is not their problem — the same rule Phase 6b established.

**A live bug found by using it, and the reason this phase is not cosmetic.** The user hit a persistent
`invalid x-api-key` on Anthropic. The key was fine; the app had stored the **OpenAI** key in the
Anthropic slot. On disk both `anthropic-api-key.dpapi` and `openai-api-key.dpapi` held the byte
-identical `sk-proj-…` value, written ten minutes apart.

The header and the secret store are both faithful — `AnthropicBackend.cs:75` sends the raw key and
DPAPI round-trips it unchanged. The fault is that **`_savedKey` is a single state that is not
qualified by provider** (`AssistantSessionStore.cs:136`, written in `Adopt` at `:191`).
`AssistantViewModel.FillKeyField` (`:279`) pre-fills the masked field from it and sets
`_keyHoldsTheStoredOne`; `ApplySettings` (`:321`) then persists whatever is in that field under the
**drafted** provider. `Resolve` (`:155`) refreshes `_savedKey` on a worker and posts back through the
dispatcher, so there is a window — and after a provider switch, a state — in which the field is
pre-filled with a *different* provider's secret. Masking hides it completely: bullets look the same
whichever key they stand for.

So the fix is not only "remember settings per provider" but **key state must be per provider too**:

- Qualify the saved-key state by provider id, or re-read it for the drafted provider before filling.
  A field pre-filled from an unqualified cache is the defect.
- **Never write a key the user did not type for the provider they are on.** `_keyHoldsTheStoredOne`
  must mean "this field holds *this provider's* stored key", not "some stored key".
- The card must make the active provider's key state legible, since masking removes every other
  signal — see the settings-card bullet below.
- Include the regression directly: pre-fill for provider A, switch the picker to provider B, save —
  B's slot must be untouched, and no path may copy A's secret into it.

Also in scope:

- **The settings card shows configured state per provider.** Picking a provider in the card already
  re-resolves its key; make that legible — which providers have a saved key, which are running on an
  environment variable, which need nothing. The `AssistantKeySource` enum already distinguishes all
  four cases and the card renders only the selected one today.
- **Clearing a key clears that provider's key only.** Same rule as clearing a conversation clears one
  repo's.
- **Tiers stay per provider.** `ModelTier` already resolves through the provider, so a quick action
  after a swap must run on the *new* provider's quick model, not a stale id.

Tests: a model set for one provider survives a switch away and back; the pre-phase flat preference
fields migrate onto the current provider rather than being dropped; the header switcher lists only
usable providers and marks the active one; selecting a provider with no key routes to setup instead
of a failing turn; a quick action after a swap resolves the new provider's quick tier; and whatever
mid-conversation rule is chosen is asserted directly (a replayed foreign tool-call id either survives
or is provably not sent).

### Phase 8c — errors in the transcript must be selectable

Phase 7 made transcript text selectable by rendering message bodies through a read-only `TextInput`
(`TranscriptMessageText`, `TranscriptRow.cs:88`). It stopped short of the notices:
`TranscriptNoticeRow` (`:150`) still renders through a plain `Text`, and that row is what every
**error**, **refusal** and **advisory** lands in. So the text a user most needs to lift out of the
app — an endpoint's 400 body, a model id that 404'd, a refusal explanation — is the only text in the
transcript that cannot be selected or copied. Errors already render inline rather than through
`OperationErrorDialog` (a Phase 3 decision), and that dialog offers copy; the inline replacement
must not be a downgrade.

Give the notice body the same treatment the message body already has, rather than inventing a second
mechanism:

- Reuse the read-only `TextInput` path. The tone colouring (`Status.DangerText` / `Status.Warning`)
  moves onto it; the tone **background** stays on the surrounding `Box`, and the field itself keeps
  the transparent background so the row still reads as a notice and not as an input.
- The refusal case composes its sentence from `AssistantRefused` plus the model's optional
  explanation — that composition is already a bound value and stays exactly where it is.
- Factor the shared body out as a widget both rows compose, per `ui-widgets-not-builder-methods` and
  `composition-over-abstraction` — no base class, no `Build*()` helper.
- Read-only rules from Phase 7 continue to apply: no edit path, no Tab capture, no fighting the
  composer for focus.

Tests: a notice row exposes a selectable, non-editable body; a selection inside an error row yields
its text to the clipboard; typing into it changes nothing; the refusal prefix is part of the
selectable text rather than a separate unselectable label.

### Phase 8d — "Generate title" becomes "Generate commit message"

The quick action writes a subject line and stops. It should write the whole message — title **and**
body — because that is the part of committing worth handing off; a one-line summary is the half a
person can already write from memory.

What exists today: `Agents/commit-title.md` ends *"Return the title and nothing else — no body, no
second line"*; `CommitTitleQuickAction` parses one line (stripping a `Title:` prefix at `:174`) and
calls `CommitEditor.SetTitle(title)` at `:153` and nothing more. So every layer currently enforces
one line, and each has to change.

- **The agent.** Rename `commit-title.md` → `commit-message.md` and rewrite the Output section: a
  subject line, a blank line, then a body explaining *why* — the house style the repository's own
  history already shows. Keep every existing rule about the subject (≈50 chars, sentence case, no
  trailing period, no Conventional-Commits prefix), because those were right.
- **A body is optional and must not be padded.** Some changes are genuinely one line, and a model
  told to always produce a body will invent motivation it cannot know. Say explicitly that omitting
  the body is correct when the change speaks for itself — then a missing body is a decision, not a
  parse failure.
- **The parse.** Split on the first blank line: line one is the title, the remainder is the body.
  Both go through `LocalChangesViewModel.SetTitle` / `SetDescription` — **through the view model,
  not around it**, the same rule Phase 4 set for `set_commit_message`, so the commit bar's bindings
  update and the user watches the text land. A reply with no blank line is a title and no body.
- **It now overwrites two fields, not one.** Generating over a description the user already typed
  destroys more than it used to. Follow the Phase 7 precedent for destructive-but-cheap actions —
  an undo through `IToastService` — rather than a confirm dialog or silent replacement.
- **Renames reach the UI and the catalogs.** `assistant.generate_title`, `.generate_title_failed`
  and `.generate_title_empty` all name a title; they become message-shaped keys in all **seven**
  `Strings/*.json`, and the visible label becomes "Generate commit message". `CommitTitleQuickAction`
  renames to match.
- **The existing tests assert the old contract.** `AssistantCommitTitleTests` (11 cases) pins
  one-line behaviour; inverting them is part of the work, not collateral. The cross-cutting
  language rule already flags `QuickTier_AsksForNoParticularReplyLanguage` as needing inversion —
  check whether that is still outstanding rather than assuming either way.

Tests: a reply with a blank line sets both fields; a reply without one sets the title and leaves the
description alone; both writes go through the view model; the undo restores whatever the user had
typed in both fields; the agent's allowed-tool list stays exactly `get_local_changes` + `get_diff`.

### Phase 9 — review the whole thing

Every phase was verified as it landed: tests green, AOT analysis clean (the native link is broken on
this machine — see the note below), the orchestrator re-running each suite rather than trusting a
self-report. That catches *regressions*. It does not catch the
thing this phase is for — **drift between phases**, where each slice is individually correct and the
composition is not.

The branch is roughly **7,400 added lines across 64 files in the app** and **1,800 across 21 files
in the framework**, over seventeen commits in two repositories. That is past what one reviewer reads
carefully; attention decays and the last files get skimmed. So this phase is a **fan-out**: many
reviewers over disjoint slices, then an adversarial pass over what they claim.

**Structure.** Reviewers by dimension × area, each returning structured findings; then every finding
goes to independent verifiers prompted to **refute** it, and dies unless it survives. Plausible
-but-wrong findings are the dominant failure of automated review — they read well, cost real time to
chase, and erode trust in the whole report. Verification is not optional polish.

Reviewers must be given the code and the invariant, **not the narrative** of why it was written that
way. The same reasoning that produced a bug will bless it on re-reading.

**The dimensions that matter here**, each grounded in something this project actually established:

- **Invariant erosion.** Each phase set a property later phases could quietly break. Check each is
  still true rather than assuming: parallel tool results remain unrepresentable-as-split
  (Risk 6, now that a second writer exists); the live context block never reaches the cached prefix
  (Risk 3); every write tool still gates on approval and no new tool slipped in as a read; the
  toolset is still bound to one `Repo`.
- **Cross-backend consistency.** Two backends now emit the same `BackendEvent` stream. Do they
  agree on refusal, cancellation, empty tool calls, and stream termination — or does one of them
  turn a filtered turn into a silent empty answer?
- **Concurrency.** The store writes `_key` under `Volatile`, resolves credentials on a worker, and
  posts back through `IUiDispatcher`; quick actions can outlive a repo switch. Look for state
  touched off the UI thread, cancellation that doesn't actually stop a stream, and results applied
  to the wrong repo.
- **Secret handling.** Keys move through the secret store, the environment, a masked field, request
  headers, and error paths. Anywhere a key could reach a log, an exception message, a transcript
  row, or the automation surface is a finding.
- **AOT and trim safety.** New serialization surface arrived with every backend. Reflection,
  unannotated generics, or a `JsonSerializerContext` gap fails as a *debug-invisible fail-fast in
  published builds* — the repo's known worst failure mode.
- **Tests with teeth.** A Phase 3 test passed against the unfixed code because an explicit height
  overrode the constraint it meant to assert. Sample the new tests and ask which would actually fail
  if the behaviour regressed. A vacuous test is worse than none: it reports safety that isn't there.
- **Convention drift.** `Column<T>`/`Each<T>` and never `Raw`; composition over inheritance; no
  private `Build*()` helpers; comment density matching the surrounding file; `<summary>` stating
  responsibility rather than mechanics; every new string in all seven catalogs.
- **Dead ends.** Code written for a phase and orphaned by a later one — unused properties, a seam
  nothing implements, a service registered and never resolved.

**Known and deliberate — do not re-report as findings.** A review that surfaces these buries the
real results in noise:

- `RepoWatcherDebounceTests` / `RepoWatcherClassifierTests` are flaky under load (real
  `FileSystemWatcher` timing). Pre-existing, unrelated to this branch.
- Two trim warnings, `Context.cs` IL2070 and `Widget.cs` IL2091. Pre-existing framework.
- `Home`/`End` and forward-`Delete` are unhandled in *every* text field, not just read-only ones.
  Deliberately left alone — adding them changes existing fields.
- A masked field's IME candidate window shows plaintext. Outside the process.
- The `RepoBarContextMenu.Show(...)` call in `CommitAssistantButton` is untested; popups need real
  windows the headless harness cannot create. Everything around it is covered.
- `scripts/out/mark-preview.png` is an untracked generated artifact.
- **`dotnet publish -c Release` cannot finish on this machine.** ILCompiler runs to completion and
  the trim/AOT analysis is clean apart from the two warnings above, but the final native link fails
  with `MSB3073` because `vswhere.exe` is not resolvable, so `link.exe` is never invoked. Reproduced
  from both Git Bash and PowerShell. **AOT analysis is verified; a linked native binary is not** —
  do not report Release AOT as end-to-end green, and do not chase this as a code defect.

**Deliverable.** One ranked report of *verified* findings — each with file, line, the concrete
failure (inputs → wrong behaviour), and a severity that reflects user impact rather than tidiness.
Findings that failed verification are dropped, not downgraded. If the review bounds its own coverage
— sampling tests rather than reading all of them, skipping generated files — **say so explicitly**;
silent truncation reads as "everything was checked" when it wasn't.

Fixes are a separate pass. A review that edits as it goes cannot be re-run against a clean baseline,
and mixing them makes it impossible to tell which findings were real.

## Cross-cutting

- **Localization.** Every new string must be added to all **seven** `Strings/*.json` catalogs — `en`,
  `es`, `ja`, `ko`, `zh-Hans`, `ru`, `ar` — or the source generator fails the build with `LOC004`.
  (`Pseudo` is synthesized by the generator, not a file.)
- **Build checks.** `dotnet build GitBench\GitBench.csproj --artifacts-path <scratchpad>` — isolated
  outputs, never the default `obj/bin`, and never stop or start the running app.
- **Language.** The assistant replies in the app's selected language, not just its chrome. The
  instruction rides in the **live context block**, never the top-level `system` — putting it in the
  cached prefix would make prefix stability depend on a user setting and invalidate the cache on
  every language change. Read the `State<Locale>` at turn-build time so a switch takes effect on the
  next message; treat `Pseudo` as English. **This includes the `commit-title` agent** — decided
  after Phase 5 shipped, reversing the English-only call made when it was written. Applying it
  means adding the instruction to that agent's live context block and inverting
  `QuickTier_AsksForNoParticularReplyLanguage`, which currently asserts the block never says
  "Reply in".
- **Interruption.** A running turn is cancellable via `CancellationToken`. Sending while a turn runs
  does not queue.
- **No cost UI in v1.** Token counts and spend are not surfaced.
- **Entry-point visibility.** Unavailable on the welcome screen and whenever no repo is active — the
  toolset cannot be constructed without a `Repo`. In the toolbar this follows whatever the
  neighbouring repo-dependent buttons do, so the row does not reflow.

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
6. **Second backend erodes the first.** The tool-result rule inverts between Anthropic and
   OpenAI-compatible endpoints, and the temptation in Phase 6 will be to relax
   `AssistantMessage.ToolResults` into whatever both accept. That would undo the Phase 1 property
   that splitting parallel results is unrepresentable. Keep the invariant in each backend's writer.
7. **Local models that cannot call tools.** A domain-tool assistant is useless against a model with
   no tool support, and the failure is quiet — it just answers from nothing. Detect the empty
   `tool_calls` response and say so, rather than letting the loop spin.
8. **`read_file` silently widens the blast radius.** Every tool through Phase 7 is a wrapper over
   git data for one repo. A path-taking read tool reaches the whole working tree — including files
   git never puts in a diff — and under "reads run silently" it does so with no approval card and
   one collapsed transcript row. Resolve paths through git rather than the filesystem, refuse
   anything outside the repo root, and if arbitrary reads are wanted instead, gate them and amend
   the Decisions table rather than leaving the policy stale. This risk compounds with Phase 6:
   a self-hosted or third-party OpenAI-compatible endpoint means "silently read a credentials file"
   becomes "silently POST a credentials file to whatever base URL is configured".
