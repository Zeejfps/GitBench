# Shadow pairing: the agent drafts, you write

## Goal

You describe a change at a high level. An agent works out the whole implementation, but none of it
reaches your working tree. You see its version in place, in the editor, in the files it touches and
the context around each edit, and you write the real code yourself. The agent's version is a guide.
Your version is the one that gets committed.

It's built in four steps, each shippable on its own:

1. **Shadow worktree and ghost overlay.** The agent implements in a hidden worktree. Your editor
   shows its remaining changes as ghost lines, and they disappear as your code converges on them.
2. **Editor walkthrough (pairing mode).** The agent guides you through the change one stop at a
   time: it jumps to the spot, lights it up and explains it. You make the edit and press Next, and
   the agent checks what you actually wrote.
3. **Hint levels.** Ghosts are hidden by default and revealed one level at a time: intent, then
   location, then shape, then the full code.
4. **ACP.** Only if steps 1–3 feel too loose in practice: become an Agent Client Protocol client,
   so every write the agent makes comes through DiffDino by design.

---

## Step 1: Shadow worktree and ghost overlay

### The task

A **shadow task** is one goal worked on by one agent in one hidden worktree:

```
ShadowTask { Id, Goal, Repo, ShadowPath, BaseCommit, Harness, State }
```

- **Where it lives.** Outside the repository, in app data:
  `<app-data>/shadow/<repo-hash>/<task-id>`. Keeping it out of the working tree means it never
  shows up in status, search, the file browser or the language server's workspace.
- **How it's created.** `IGitWorktreeOperations.AddWorktree` with a detached HEAD at your current
  `HEAD`. Then your uncommitted changes are carried over (`git stash create` in your tree, then
  `git stash apply <sha>` in the shadow tree) so the agent starts from what you see, not from the
  last commit. `BaseCommit` records the stash commit (or `HEAD` when the tree is clean).
- **How it ends.** Discarding the task removes the worktree with
  `RemoveWorktree(..., force: true)`. There's no "accept" action: the only way code gets into your
  tree is by you writing it (step 3 adds a per-hunk escape hatch).

### Launching the agent

A **New task** command opens a dialog: a goal text box and a harness picker. It opens a terminal tab
with a new `ITerminalLaunch`, `AgentLaunch`, next to `ShellLaunch`:

- The working directory is `ShadowPath`. That one choice is the whole safety model: the agent can
  edit however it likes, and its edits can't reach your tree.
- The command comes from a harness preset in Settings: a command template with `{prompt}`,
  `{mcpUrl}` and `{cwd}` placeholders. Presets ship for Claude Code and Codex, and a custom preset
  covers anything else. Each preset registers DiffDino's MCP server for the session, so the agent
  can reach the tools added in step 2.
- The starting prompt wraps your goal: implement it fully in this directory, don't commit, run the
  build and tests here, and stop when done.

The terminal tab gets the task's name and stays attached to the task. Closing the task closes the
tab.

### The remaining diff

The core computation: for every file the shadow tree has changed, what's the difference between
**your current text** and **the shadow's text**?

- **Which files.** `git -C <shadow> status --porcelain` against `BaseCommit`, re-read when the
  shadow tree changes. Watch the shadow tree with a narrow `FileSystemWatcher`, using the same
  debounce idea as `RepoWatcher`.
- **Your side.** The open editor buffer if the file is open (including unsaved edits), otherwise
  the file on disk.
- **The diff.** An in-process line diff (Myers), a new `LineDiff` in `Features/Diff/`. Shelling out
  to `git diff --no-index` would work for files on disk, but it's too slow to rerun on every
  keystroke against a dirty buffer, so one in-process path serves both. Its output is the existing
  `DiffHunk`/`DiffLine` shape, so everything downstream (intra-line emphasis, painters) works
  unchanged.
- **When it reruns.** On a shadow file change (debounced), and on an editor revision of an affected
  file (debounced like `DocumentAnnotations`, around 50 ms). The result is keyed by document
  revision, as `EditorRowSet.SetAnnotations` already does, so an answer computed for older text is
  dropped.

A hunk that's empty on both sides is done. The task's progress is simply "N hunks across M files
remaining", and zero means you've matched the agent's change (or written your own version and
dismissed the rest).

### The ghost overlay

In the editor, a remaining hunk shows as:

- **Lines the agent added:** virtual rows between your document's rows, in faded ghost text. They
  aren't part of the document. `EditorRowSet.NewLineAt` returns null for them, the same way it does
  for a removed row in a diff, so the cursor, selection, search and the language server never see
  them.
- **Lines the agent removed or changed:** your own lines, with a strike tint and a gutter marker.
- **A gutter marker on each hunk** with a hover card showing the agent's full text for that hunk.

`EditorRowSet` already builds on `DiffRow`, so a ghost row is a new row kind with its own painter
style, not a new renderer. Rows are re-projected on every edit through the same `Reproject` path
folds and annotations use.

A new **Shadow** panel lists the task: goal, harness status (running, idle, exited), and the
remaining hunks grouped by file. Clicking a hunk opens the file at that spot. Files the agent created
that don't exist in your tree are listed there too. Opening one offers to create it empty, so the
whole thing appears as ghosts.

**Dismissing.** You'll often write something different on purpose. Right-clicking a hunk →
**Dismiss** hides it for this task, stored as the hunk's shadow-side text so it stays dismissed
across recomputes until the agent changes that text again.

**Reconciling.** Right-clicking the panel → **Ask agent to reconcile** types a prompt into the
agent's terminal: "The user implemented this differently in their tree at <path>. Read their version
and update yours to match their approach." The shadow then follows your design, and the remaining
ghosts are what's still left under your approach.

### Done when

- A task can be started from a goal with either preset, and the agent's edits land only in the
  shadow tree.
- The ghosts for every changed file appear in the editor and update as you type and as the agent
  keeps working.
- The remaining count reaches zero when your code matches, and dismissing hunks works.
- Discarding the task leaves no worktree, branch or files behind.

---

## Step 2: Editor walkthrough (pairing mode)

Step 1 shows *what* the agent would change. Step 2 adds *the order and the reasons*: the agent takes
you through the change one stop at a time in your editor, and checks each edit you make.

### Reusing the walkthrough store

`ReviewWalkthroughStore` already implements the protocol we need: batches of steps with a title, a
markdown body, a focus and spotlights; Next and Back handled locally; a question field; a blocking
wait that returns what you did; and presence and disconnect handling. It only reaches the review
window through `IReviewPresentation` (focus a line, focus a file, set spotlights, read the
selection).

So pairing mode is a second implementation of that interface, `EditorPresentation`, over the editor
instead of the review window:

- `FocusLineAsync` opens the file in the editor, scrolls the line to a third of the way down, and
  returns the resolved line text so the agent can check it pointed at the right line.
- `SetSpotlightsAsync` highlights ranges in the editor, with the same dimming option.
- `Selection` is your editor selection.

The walkthrough rail and card are hosted beside the editor instead of the review window. Rename the
store to `WalkthroughStore` (it's no longer review-specific), with one instance per surface.

### One new action: the edit

Today the wait returns `next`, `ask`, `pending` or `cancelled`. Pairing adds one:

```
{ action: "edited", at, diff }
```

When a step is marked as an edit step, the card's button reads **Done** instead of **Next**.
Pressing it saves the affected files and returns the diff of your tree since the step was shown, so
the agent reviews exactly the edit you just made. The agent replies with the next batch, or with a
correction step pointing at what's missing ("the overload at line 88 still takes the old
signature").

### Tools

The walkthrough MCP tools gain a `surface` argument (`"review"` or `"editor"`, with `"review"` as the
default so existing clients are unaffected). One new tool:

- `pairing_state`: the task's goal, the remaining hunks from step 1 (file, lines and shadow text),
  the file and line you're on, and your selection. It's what the agent reads to decide the next
  stop, and it means the agent doesn't need to diff the two trees itself.

The server's instructions get a pairing section: send the change in a sensible order (definitions
before callers), one edit per step, use `edited` results to check the user's work, and send
`walkthrough_end` when the remaining count reaches zero.

The `AgentLaunch` starting prompt from step 1 gains a variant, **Implement, then walk me through
it**, that tells the agent to finish the shadow implementation first and then start an editor
walkthrough.

### Done when

- Either harness can walk you through its shadow change in the editor, one stop at a time.
- Done returns your actual edit, and the agent reacts to it (confirms it, or corrects it with a new
  step).
- Asking a question mid-walkthrough works exactly as in the review window.
- The review-window walkthrough still behaves exactly as before.

---

## Step 3: Hint levels

If the full answer is always visible, it's too easy to just copy it, and then you no longer
understand the code you "wrote". Hints turn the ghosts from an answer key into help you ask for.

### The four levels

Per hunk, one keypress (and a gutter button) moves it up a level:

| Level | What you see | Where it comes from |
|---|---|---|
| 0: Intent | A one-line note in the gutter: "Validate the token before refreshing." | Agent |
| 1: Location | The note, plus the target lines in your file lit up | Step 1 diff |
| 2: Shape | The signature or pseudocode of the change, as ghost text | Agent |
| 3: Code | The full ghost lines from step 1 | Step 1 diff |

Levels 1 and 3 come from the diff for free. Levels 0 and 2 need the agent's words:

- A new MCP tool, `hint_set { path, shadowLines, intent, shape }`, attaches them to a hunk,
  identified by its shadow-side line range, so hints survive your edits.
- In pairing mode, the step's title and body already serve as level 0, so they're used whenever a
  step covers the hunk.
- When a hunk has no agent hint, level 0 falls back to "Change in <enclosing declaration>", from the
  existing tree-sitter outline (the same path `DiffAnnotations.HunkHeader` uses). Level 2 falls back
  to level 3.

### Defaults and the escape hatch

- A setting chooses the starting level for new tasks: **Intent** (the default), **Location** or
  **Code**. Choosing Code gives you step 1's behavior.
- The Shadow panel shows how many hunks you've revealed at each level. It's feedback for you, not a
  score.
- **Take this hunk** (level 3 only, behind a confirmation) copies the agent's text into your buffer
  as an ordinary, undoable edit. It's for boilerplate you have no interest in typing, and it's the
  only way agent code reaches your tree, one hunk at a time and on purpose.

### Done when

- New tasks start at the chosen level, and each hunk can move up a level independently.
- Agent-provided intents and shapes show up when present, and the fallbacks work when they're not.
- Taking a hunk inserts it as a single undoable edit, and the hunk disappears from the remaining
  count.

---

## Step 4: ACP

Steps 1–3 rely on the shadow worktree to keep the agent's edits away from your code. That's
enforced by the filesystem and works with any harness, but DiffDino only sees the result of each
edit after it lands on disk, never the edit as a structured operation. If that turns out to be too
loose (ghosts lag behind, or we want the agent's own reasoning attached to each edit), the next
step is to become an **Agent Client Protocol** client.

### Why ACP fits

In ACP, the editor is the client and the agent runs as a subprocess over JSON-RPC on stdio. The
client can offer filesystem methods (`fs/read_text_file`, `fs/write_text_file`) that the agent uses
instead of touching the disk, and it receives the agent's plan, tool calls and messages as
structured session updates. Adapters exist for Claude Code and Codex.

For this plan, that means:

- **Writes come to us.** A `fs/write_text_file` goes into the shadow text model directly (in memory
  or to the shadow tree, our choice). Ghosts update the moment the agent decides on an edit, not
  after a file watcher fires.
- **Reads can come from us.** `fs/read_text_file` can return your live buffer, including unsaved
  edits, so the agent always sees what you've actually written. Reconciling (step 1) becomes
  automatic.
- **The plan is structured.** The agent's plan entries and per-edit tool calls map onto
  walkthrough steps and level-0 hints without prompting the agent to call our tools.
- **No terminal UI to parse.** The session is rendered natively in the Shadow panel, and the
  terminal tab from step 1 becomes optional.

### The work

1. **Spike (before committing to it).** A minimal ACP client in a console project: start the Claude
   Code and Codex adapters, send a prompt, and log every session update. Confirm that both adapters
   actually send file writes through the client's `fs` methods for their edit tools, and find out
   what happens to edits made through shell commands (formatters, code generators). The answer
   decides whether ACP can replace the shadow tree or only supplement it.
2. **`AcpAgentConnection`** in `Features/AgentConnections/`: the JSON-RPC transport, the
   `initialize` and `session/new` handshake, and permission requests shown as the existing tool
   approval card.
3. **An ACP-backed shadow task.** A third harness kind next to the terminal presets. Its writes
   feed the same remaining-diff model from step 1, and its plan and tool-call updates feed the
   walkthrough store (step 2) and hints (step 3). Everything downstream of the shadow text is
   unchanged.
4. **Keep the shadow worktree** as the place builds and tests run, even for ACP tasks. The agent
   still needs a real tree to compile against. ACP changes how edits reach us, not where they're
   verified.

### Done when

- The spike's findings are written up, with a clear go or no-go.
- If go: an ACP task's ghosts update per edit rather than per file-watcher event, the agent reads
  your live buffers, and its plan appears as walkthrough steps with no extra prompting.
