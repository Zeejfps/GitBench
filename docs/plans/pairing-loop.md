# Pairing loop: the agent navigates, you write

## Goal

You describe a change at a high level. An agent doesn't implement it. It takes you through it one
stop at a time: it opens the file, puts you on the function, and says why this is the next place to
change. You write the code. The agent reads what you wrote, reassesses against the goal, and picks
the next stop. Your code is the only implementation code that gets written.

Tests are the exception: the agent may write a test for a stop, the app checks that it fails, and
the stop is finished when your code makes it pass.

## Why a loop, not a draft up front

An earlier version of this plan had the agent implement the whole change in a hidden worktree
first and show it as ghost lines. Two problems:

- **Waiting.** On a large task you wait for the full implementation before the first useful stop.
- **Divergence.** The moment you design something differently, most of the draft is wrong, and
  every later ghost is advice for code you're not writing.

In the loop, each turn only answers "where next, and why". That's quick, and every turn starts from
what you actually wrote, so a large change of direction just redraws the rest of the roadmap.

## The loop

```
Goal ──► Roadmap ──► Stop ──► you edit ──► Done ──► agent reassesses ──┐
                      ▲                                                 │
                      └─────────────────────────────────────────────────┘
```

- **Roadmap.** 3–7 coarse milestones, no code. Shown in the Pairing panel. The agent rewrites it
  on any turn, and the panel shows what changed, so a redirect is visible instead of silent.
- **Stop.** One file and one symbol, a short title and the reason. The app opens the file and
  places the caret on the symbol. Stops are addressed by symbol name, resolved through the
  tree-sitter outline (or the language server when it's up), never by line number, so they
  survive your edits. A stop for a symbol that doesn't exist yet names the file and the symbol it
  should go next to.
- **Kind.** The agent decides per stop: an **edit stop** or a **test stop**.
- **Done.** Saves the affected files and returns your diff since the stop was shown, plus an
  optional note ("went with an event instead of a callback"). The agent either sends a correction
  stop at the same place or moves on.
- **Finish.** The agent ends the session when the roadmap is empty. You can end it at any time.

## What the app enforces, and what's left to the prompt

| On rails (enforced by the app) | Instructions (asked of the agent) |
|---|---|
| The agent can't write files other than through `pairing_write_test` | Pick an order that builds: definitions before callers, test before code |
| One open stop at a time: proposing another while one is open fails | Keep a stop to one function or one small edit |
| Every turn gets the same input: goal, roadmap, your diff, your note | Explain *why here*, not the code to type |
| A test stop can't be finished until its test passes (overridable, flagged) | Revise the roadmap whenever your diff departs from it |
| Hints only go up a level when you ask | No code in the explanation below the level you asked for |

The first row is the one that matters. If the agent can't edit your code, "you write it" doesn't
depend on the agent following instructions.

## What already exists

| Piece | Where |
|---|---|
| Walkthrough protocol: batches of steps, Next/Back, question field, blocking wait, presence | `Features/Review/Walkthrough/ReviewWalkthroughStore.cs` |
| The surface the store drives: focus a line or file, spotlights, selection | `IReviewPresentation` in `Features/Review/Walkthrough/ReviewPresentation.cs` |
| Walkthrough MCP tools (`walkthrough_step`, `walkthrough_wait`, `walkthrough_end`) | `Features/AgentConnections/AgentToolMcpSource.cs` |
| Agent connections: settings, session, instructions, repo resolution | `Features/AgentConnections/` |
| Terminal tabs with pluggable launches | `Features/Terminal/ITerminalLaunch.cs` |
| Outline of declarations per file | tree-sitter outline queries |

---

## Step 1: ACP spike

The **Agent Client Protocol** is how the app will run agents. The app starts the agent as a child
process and talks JSON-RPC over stdio. It receives the agent's plan, messages and tool calls as
structured updates. It answers the agent's permission requests, and it can pass MCP servers into
the session. Adapters exist for Claude Code and Codex, and Gemini CLI speaks it natively. The
adapters wrap each vendor's CLI and use its own login, so users can use their subscriptions rather
than API keys.

What matters for the loop is that the agent asks the app before a tool runs. The app refuses every
write, for every harness in the same way, instead of trusting each CLI's flags.

A console project, before any UI:

1. Start the Claude Code, Codex and Gemini adapters. Send a prompt, and log every session update.
2. **Subscriptions.** Confirm that each adapter runs on a signed-in CLI with no API key set.
3. **Permissions.** Confirm that edit tools *and shell commands* arrive as permission requests the
   client can reject, and what the agent does after a rejection.
4. **MCP.** Confirm that an MCP server passed in `session/new` is reachable by the agent, and that
   its tools don't need a permission prompt each call.

### Done when

- Findings are written up per adapter, with a go or no-go for each.
- If no adapter passes 2 and 3, step 5 (terminal harnesses) becomes the main path and the rails in
  the first row above become per-harness flags.

---

## Step 2: The loop, with edit stops

### Sessions

```
PairingSession { Id, Repo, Goal, Harness, Roadmap, Stop?, State }
```

A **New pairing session** command opens a dialog: the goal and the harness. The session runs
through a new `AcpAgentConnection` in `Features/AgentConnections/` (transport, `initialize`,
`session/new`, and the permission policy: reads allowed, writes and shell denied). Permission
requests the policy doesn't settle are shown as the existing tool approval card.

### The walkthrough store, generalised

Pairing reuses the walkthrough store over the editor:

- Rename `ReviewWalkthroughStore` to `WalkthroughStore` and `IReviewPresentation` to
  `IWalkthroughPresentation`. One instance per surface.
- `EditorPresentation` implements it over the editor: focusing a stop opens the file and places the
  caret on the resolved symbol with the declaration a third of the way down; spotlights highlight
  editor ranges; the selection is the editor selection.
- The wait gains one action, `done { at, diff, note }`. The diff is taken from the point the stop
  was shown, so it's your edit and nothing else.

### Tools

| Tool | Does |
|---|---|
| `pairing_roadmap { milestones }` | Replaces the roadmap. |
| `pairing_stop { path, symbol, after?, title, reason, kind }` | Opens a stop. Fails while one is open. `after` places a symbol that doesn't exist yet. |
| `pairing_wait` | Blocks until Done, a question, or cancel. Same timeout and re-wait behaviour as `walkthrough_wait`. |
| `pairing_state` | Goal, roadmap, the open stop, the file and line you're on, your selection. |
| `pairing_end { summary }` | Ends the session. |

The server instructions get a pairing section: the order to work in, one edit per stop, read
`done` diffs before moving on, correction stops over silent fixes, revise the roadmap on divergence.

### UI

- **Pairing panel:** goal, roadmap (with changes marked), harness status, and the agent's messages
  rendered as markdown.
- **Stop card** beside the editor: title, reason, a note field, **Done**, and a question field.

### Done when

- A session starts from a goal, and the first stop arrives without the agent writing any code.
- Done returns exactly your edit, and the agent either corrects it or moves on.
- A change of direction in your code shows up as a revised roadmap on the next turn.
- The agent's attempts to write or run shell commands are denied and don't stall the session.
- The review-window walkthrough behaves exactly as before.

---

## Step 3: Test stops

The agent decides when a stop starts with a test.

1. The agent calls `pairing_write_test { path, content, test }`. `test` names the test to run. This
   is the only write the app lets through, and only to test files (paths matching the repo's test
   globs). It lands in the working tree as an ordinary change, and the stop card shows it with an
   **Undo test** action.
2. **The app runs the test** with the repo's test command, a setting with a `{test}` placeholder
   (e.g. `dotnet test GitBench.Tests --filter {test}`). The first test stop in a repo without the
   setting asks for it, prefilled with the agent's suggestion. Output streams into a terminal tab.
3. The test must fail. If it passes, the result goes back to the agent as "this test proves
   nothing", and the stop doesn't open.
4. You implement. **Done** reruns the test, and the stop only closes when it passes. **Done
   anyway** closes it red, and the agent is told so.

The `done` result for a test stop carries the test outcome and its output alongside your diff.

### Done when

- A test stop writes one test, shows it red, and closes when your code makes it green.
- A test that passes before you've written anything is sent back, not shown.
- Test files are the only thing the agent can write, and each test can be undone from its stop.

---

## Step 4: Hint levels

Stops say *where* and *why*. When that isn't enough, you ask for more, one level at a time:

| Level | What you see | Cost |
|---|---|---|
| 0: Intent | The stop's title and reason | Free, comes with the stop |
| 1: Location | The lines to change, lit up | A spotlight from the agent |
| 2: Shape | Signature or pseudocode, as ghost text | One agent turn |
| 3: Draft | The agent's code for this stop only, as ghost text | One agent turn |

- A new wait action, `hint { level }`, asks the agent for the next level of the open stop. The agent
  answers with `pairing_hint { level, text | spotlights }`.
- Ghost rows are virtual rows in `EditorRowSet` with their own painter style.
  `EditorRowSet.NewLineAt` returns null for them, so the caret, selection, search and the language
  server never see them. They're re-projected on every edit through `Reproject`.
- The draft ghost disappears line by line as your text matches it (an in-process line diff between
  the draft and the stop's symbol range).
- **Take this draft** (level 3 only, behind a confirmation) inserts the draft as one undoable edit.
  It's for boilerplate, and it's the only way agent code reaches your implementation.
- A setting chooses the level new stops open at. The default is Intent.

### Done when

- Each stop can go up a level on request, and only the stop you're on costs an agent turn.
- Draft ghosts shrink as you type the matching code, and a taken draft is one undo step.

---

## Step 5: Terminal harnesses (fallback)

For agents without an ACP adapter: an `AgentLaunch` next to `ShellLaunch` runs a preset command
template with `{prompt}`, `{mcpUrl}` and `{cwd}` placeholders in a terminal tab. The loop tools are
the same MCP tools, so steps 2–4 work unchanged. What's weaker:

- Keeping the agent read-only relies on the preset's flags (e.g. disallowing edit tools). The
  preset editor says so, and each session shows it has "no enforced write guard".
- Status and messages are what the terminal shows. The Pairing panel shows only the roadmap and
  stops.

### Done when

- A custom preset can run a pairing session end to end through the terminal.
- A session without an enforced guard is labelled as such in the Pairing panel.
