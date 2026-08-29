---
name: gitbench-tests-preexisting-reds
description: Five GitBench.Tests failures that already fail at commit a25970e — confirm against the baseline before treating them as your regression
metadata:
  type: project
---

On branch `terminal`, `GitBench.Tests` is not green even before the Unix PTY work. These five fail
identically at `a25970e` (the commit before it):

- `Terminal.TerminalKeybindCollisionTests.AFocusedTerminal_DeclinesCtrlDigit_AndTheRepoStillSwitches`
- `Terminal.TerminalKeybindCollisionTests.AFocusedTerminal_DeclinesCtrlNumpadDigit_AndTheRepoStillSwitches`
- `Terminal.TerminalKeybindCollisionTests.WithoutTerminalFocus_CtrlB_StillCollapsesTheRepoBar`
- `WorktreeRemoveTests.FailsAndKeepsTheTreeWhenGitRefuses`
- `GitRepoLocksTests.Real_git_resolves_a_linked_worktree_onto_the_primary_family`

The keybind three look PTY-adjacent but use `UnusedPtyFactory`, a stub, and fail on repo-switch
identity. `SyntaxHighlighterTests` also fails under full-suite load and passes filtered — that one is
a flake, not a red.

**Why:** three of them have "Terminal" in the name, so any terminal-area change looks like the cause.

**How to apply:** to check a baseline, `git worktree add <scratch> <commit>` then copy `framework/`
into it — `framework` is a submodule and a worktree gets an empty directory, so the build fails with a
wall of `CS0246: ZGF` errors otherwise.
