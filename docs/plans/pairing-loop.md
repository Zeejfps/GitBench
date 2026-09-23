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
- **Done.** Saves the affected files and returns your diff since the stop was shown. Anything
  you want to say about it ("went with an event instead of a callback") you say in the
  conversation, which the agent keeps for the whole session. The agent either sends a correction
  stop at the same place or moves on.
- **Finish.** The agent ends the session when the roadmap is empty. You can end it at any time.

## What the app enforces, and what's left to the prompt

| On rails (enforced by the app) | Instructions (asked of the agent) |
|---|---|
| The agent can't write files other than through `pairing_write_test` | Work top down: callers before what they call, test before code, a type only once code needs it |
| One open stop at a time: proposing another while one is open fails | Keep a stop to one function or one small edit |
| Every turn gets the same input: goal, roadmap, your diff, what you said | Explain *why here*, not the code to type |
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

### Findings

Measured in `spikes/acp` (full table in its README). **Go for Claude, Codex and Gemini**, all on
their subscription logins, with four conditions the connection has to meet:

- **Set the asking mode after `session/new`**: `default` for Claude and Gemini, `read-only` for
  Codex. Claude otherwise inherits the user's `defaultMode`, and in `auto` no write ever reaches
  the client.
- **Decide by tool kind**: `edit`, `delete`, `move`, `execute` rejected; `read`, `search`, `think`,
  `fetch` allowed. Claude runs read-only shell without asking, which the write guard doesn't mind.
- **Allow the app's own MCP tools**, which every adapter asks about, matched by each adapter's own
  marker.
- **Re-prompt after a rejection that ends the turn** (Codex always ends it `cancelled`).

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
- The wait gains one action, `done { at, diff }`. The diff is taken from the point the stop
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
- **Stop card** beside the editor: title, reason and **Done**, over a message field into the
  conversation.

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

---

## Implementation notes

All five steps are built on the `pairing-loop` branch. Where the build departs from the plan above,
this says so and why.

### Step 2

- **The walkthrough store was not generalised.** `ReviewWalkthroughStore` and `IReviewPresentation`
  are shaped around review-diff lines and batches of steps walked with Next and Back; a pairing
  session has one open stop, a roadmap and Done. `PairingStore` (`Features/Pairing/`) keeps the
  same protocol — one waiter, a bounded wait answering `pending`, cancellation on a newer wait —
  over its own model, and the review walkthrough is untouched. One difference on purpose: the
  user's moves **queue** while no wait is attached instead of latching latest-wins, so a Done is
  never lost behind a question asked after it.
- **Sessions are per repository.** The pairing tools take `repo` like every other exported tool and
  find the repository's live session; there is no separate session id on the wire.
- **ACP agents reach the app through the Agent connections server**, which a session turns on if
  it is off (the dialog says so). The app's own MCP tools are allowed **once** per call rather
  than "always": Claude Code writes an "always" into the repository's `.claude/settings.local.json`.
  An MCP approval that names no server is asked of the user.
- **The guard only sees what the CLI asks about.** A CLI's own allow rules (Claude Code's
  `permissions.allow`, for instance) decide without asking the client. For Claude Code the session
  therefore also takes the edit and shell tools away inside the CLI (`disallowedTools` through
  `session/new`'s `_meta`); for Codex and Gemini the guard is as strong as their asking modes.
- **Stops resolve through the tree-sitter outline** (then the name as a whole word). The app has no
  LSP `documentSymbol` request, so the language server is not consulted. The outline counts a
  member that ends with its line break as reaching its type's closing line; the resolver corrects
  for that rather than changing the extractor, whose end lines also drive folding.
- **Done's diff** comes from `WorkingTreeSnapshots`: the working tree written to a tree object
  through a throwaway index when the stop opens and again at Done, so new files count and the
  user's staging area is never touched.
- **The stop card is part of the Pairing panel**, docked beside the content panel: the stop's text,
  test and draft note scroll with the roadmap and the conversation, and Accept, Done and Skip stay
  pinned above the message field.
- **No note on Done.** The field under the conversation is the one place to talk to the agent, for
  questions and for "I did this instead" alike: the agent keeps the conversation, so what was said
  before Done is in mind when it reads the diff. On the wire the wait answers
  `{action:"message", text}` rather than `ask`.
- **The agent talks through `pairing_say`.** Prose outside the tools isn't reliable: Claude Code
  sent its answer to a message as a *thought* chunk, which the panel doesn't show, and a terminal
  agent's prose never reaches the panel at all. The instructions say `pairing_say` is the only way
  the user hears it. Streamed prose is still shown when it does arrive.
- **`pairing_show` points at code without a stop.** Asked "show me the test", an agent with only
  `pairing_stop` refused, since a stop was already open. `pairing_show` opens a file on a
  declaration or a line and touches nothing else: the open stop, its card and its draft stay, and
  the card's link takes the user back.
- **The panel follows the conversation**: a new message or a streaming reply scrolls it to the end,
  and a new stop takes it back to the top, where the stop card is. Accept, Done and Skip sit
  under the message field.
- **Top down, not bottom up.** The agent starts where the change is used and creates each
  function or type only once the code written so far needs it; the plan's "definitions before
  callers" is reversed. A diff that calls something not written yet is the next stop, not an error.
- **The conversation is per stop.** Done or Skip clears it, so stop 6 isn't shown under the chat
  about stop 1; what the agent says between stops, before it opens the next, stays with the next
  one. A question still waiting on the user survives the clear. The agent keeps its own memory.
- `DIFFDINO_ACP_TRACE=<dir>` writes each ACP session's protocol traffic to a file there.

### Step 3

**Test stops were removed; the agent runs the tests itself.** The app-side test machinery —
`kind: "test"`, `pairing_write_test`, the per-repository test command, the stop card's run/red/undo
section — was custom plumbing for what the agent's own shell already does. Now the guard allows
shell commands (`execute` is allowed once; an `execute` that is really another server's MCP call, as
Codex files them, is asked), Claude Code no longer has Bash/PowerShell disallowed, and the
instructions tell the agent to run the tests itself and to write a test first as an ordinary stop in
the test file. File edits are still refused. The notes below describe the removed design.

- **Test output is shown on the stop card** (its tail), not streamed into a terminal tab: the
  terminal has no tap on its output, and the app needs the output anyway to hand to the agent.
- **Test files are recognised by built-in rules** (`TestFiles`): a test directory on the path, or a
  file named like a test. There is no per-repository glob setting yet.
- **The user runs every test the agent writes**, from the stop card (the test's path opens it
  first). The plan had the app run it at once; but a test is the agent's code, and running it
  unread would let `pairing_write_test` stand in for the shell the guard refuses. Done reruns the
  same test without asking.
- **Build and runner configuration files are never test files** (`*.csproj`, `conftest.py`,
  `package.json`, `*.config.*`, …), and a test name is one plain argument — no spaces, no leading
  `-` — so it can't add options to the test command.
- **The test command is kept per repository** in the preferences, prefilled on the stop card from
  the agent's suggestion the first time.

### Step 4

**Hint levels were replaced by a draft on every stop.** In use, More help was a round trip per
level before the useful thing — the code — showed up. Now:

- `pairing_stop` carries `code`: the agent's code for that one stop, one block. It is drawn in the
  editor where it goes as soon as the stop opens.
- Where it goes is resolved by the app, not guessed from the stop's caret line: by default it
  replaces the declaration from its name line to its last line (`OnSymbol.LastLine`), goes in
  after the `after` declaration for a new one, or is the whole of a new file. The agent can narrow
  it with `lines {from,to}` (replace) or `after_line` (insert), checked against the file. The old
  draft hung after the declaration's *name* line, i.e. inside the signature.
- A replacement lights up the lines it replaces with the code drawn under them, and doesn't shrink
  as the user types; an insertion shrinks as before. Both anchors follow edits above them.
- **Accept** puts the code in as one undo step (a new file is created) and then does exactly what
  Done does; the `done` action says `accepted: true`. A test stop still has to go green. If the
  editor can't take it (the file never came on screen), the stop stays open with a notice.
- A different draft for the open stop is a `pairing_stop` with `replace: true`.
- **A new file is built up in blocks, not written in one go.** Its stop creates the file empty and
  opens it; the code is only the skeleton (imports, the type's outline), shown in the editor with
  the Accept pill like any other block, and members come at later stops. A file the stop created is
  deleted again if the user skips or ends while it is still empty; an empty file that was already
  there is left alone.
- **Accept only puts the code in; Next moves on.** Accept inserts it as one undo step and the stop
  stays open for the user to change it. Next (was Done) finishes with the file as it is, and Accept
  & next does both. The editor carries Accept and Accept & next pills on the suggestion's first
  line, like hunk Stage; the card shows Accept & next and Next, and only Next once the code is in.
  Shortcuts: Ctrl/Cmd+Enter Accept & next, +Shift Accept, +Alt Next (`PairingKeybindController`
  at the root, on the way back out, so the editor's own Enter keys are untouched).
- `done` says what happened to the agent's code: `draft: accepted_as_is | accepted_then_edited |
  not_accepted`, the middle one from the file's text right after Accept against its text at Next.
- Replaced lines are drawn as removed and the suggestion as added, with changed characters
  emphasised; lines the agent sent back unchanged at either end are left out of the replacement.
- A draft line counts as typed when a line reading the same (trimmed) is in the run the user has
  written below the anchor — down to the last typed line with real content — so a lone brace of
  the file's own doesn't swallow the draft's.

### Step 5

- Two extra placeholders: `{promptFile}` and `{mcpConfigFile}`, because a multi-line prompt and a
  JSON config can't be quoted into every shell's command line (the Windows command processor
  can't quote some characters at all, and refuses rather than half-quotes).
- The built-in preset runs Claude Code with the app's server as an MCP config file and its edit and
  shell tools disallowed. On Windows the shell stays open after the agent exits, so a session there
  only learns the agent is gone when the user ends it.

### Verified by hand

Claude Code over ACP, in a scratch repository: a session start to finish with a correction stop;
(before hint levels were removed) a draft shrinking as it was typed; a test stop red, still red after
a wrong edit, then green and closed; End from the panel. Claude Code in a terminal tab from the
preset. Codex and Gemini are covered by the spike, not yet by a session in the app.

### Open

- Accept and the per-stop draft are unit-tested (store, placement, editor take) but have not been
  run against a live agent.
- Untracked build output (`__pycache__`, `bin/`) that isn't ignored shows up in Done's diff.
