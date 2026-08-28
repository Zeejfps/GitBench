---
name: full-suite-timeout-flakes
description: The nondeterministic full-suite failures are wider than the three usually named — any 10s-exact failure in GuiTestHarness or store tests is load, not a regression
metadata:
  type: project
---

The known-flaky set is not limited to `RepoWatcher{Classifier,Debounce}Tests` and `GitReadGateStoreTests`. Under full-suite load on this machine, these have also failed and then passed cleanly when re-run filtered:

- `AssistantProviderSwitchConversationTests`, `AssistantProviderKeyIsolationTests`,
  `AssistantSelectionActionTests`, `AssistantOverlayViewTests`
- `Markdown.CodeBlockHighlightTests`
- `RepoOperationsStoreTests`

**Why:** they wait on a harness/store settle with a 10-second budget, and a saturated machine (a parallel agent building, or the app running) blows it. The tell is a duration of exactly `10 s` in the failure line — a real assertion failure is milliseconds.

**How to apply:** before reporting a full-suite failure as a regression, check the duration column. Anything at a flat 10 s, re-run that class alone with `--filter` (add `--no-build` to skip a rebuild) before naming it red. See [[flaky-timing-tests]].
