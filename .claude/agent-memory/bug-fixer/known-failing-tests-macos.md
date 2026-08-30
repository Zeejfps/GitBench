---
name: known-failing-tests-macos
description: Baseline of GitBench.Tests failures on macOS that are NOT caused by whatever you just changed — check here before blaming your diff
metadata:
  type: project
---

As of 2026-08-30 (HEAD `c233a1d`), `dotnet test GitBench.Tests` on macOS fails a stable set of tests that
have nothing to do with the current working tree. Verify each is still failing before relying on this.

**Why:** these cost real time to re-diagnose from scratch every session, and their presence makes a clean
run impossible, so "did I break something?" has to be answered against this baseline rather than against zero.

**How to apply:** when a test fails, check it against this list first. If it is here, it is pre-existing.

Confirmed diagnosed:

- `TerminalKeybindCollisionTests.WithoutTerminalFocus_CtrlB_StillCollapsesTheRepoBar`
- `TerminalKeybindCollisionTests.AFocusedTerminal_DeclinesCtrlDigit_AndTheRepoStillSwitches`
- `TerminalKeybindCollisionTests.AFocusedTerminal_DeclinesCtrlNumpadDigit_AndTheRepoStillSwitches`

  One root cause, test-side. `AppKeybindController.PrimaryModifier` is `Super` on macOS and `Control`
  elsewhere (since `2e4a238`), but `CollidingApp` in `GitBench.Tests/Terminal/TerminalInputSeamTests.cs`
  hardcodes `InputModifiers.Control`. Failing on macOS since the file was created in `a25970e`. Several other
  fixtures in the suite already define a local mac-aware `Primary` modifier; this one did not.

- `TerminalInputRegressionTests.*` (every test calling `TerminalRun.Replaying(...)`), timing out with
  "Timed out waiting for the shell to be adopted".

  Test-side, in `TerminalRun.Start()`. It waits for `Render.Value is TerminalRenderState.Running`, but for a
  replay the recording is exhausted immediately, so `TerminalInstance` flips `Running -> Exited` and never
  goes back. Fails whenever the flip lands inside the same `QueuedDispatcher.Drain()` call — which is
  ~always for the first replay in a cold process and ~never once the path is JIT-warm, hence the
  "intermittent in the full suite, deterministic in isolation" shape. Introduced by `7050f96` (before it,
  exit was a `_shellExited` bool and `Running` was terminal, so the wait was safe).

Flaky/environmental, not investigated: `TerminalRowRunsTests.Split_RepeatedCalls_AllocateNothing`,
`WorktreeRemoveTests.FailsAndKeepsTheTreeWhenGitRefuses`,
`GitRepoLocksTests.Real_git_resolves_a_linked_worktree_onto_the_primary_family`,
`SyntaxHighlighterTests.Svelte_MarkupAndEmbeddedScript_AreColored`,
`Markdown.CodeBlockHighlightTests.ThemeFlipRecolorsWithoutRetokenizing`,
`AssistantRemoteToolsTests.Fetch_WhileAFetchIsAlreadyRunning_...`.

Reproducing the replay timeout takes one command, no load needed:
`dotnet test GitBench.Tests/GitBench.Tests.csproj --filter 'FullyQualifiedName~StillDrawsTheScreen'`

Related: [[technique-internals-probe]]
