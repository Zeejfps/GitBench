---
name: terminal-keyboard-exit-signal
description: TerminalSession.Exited is a racy "is the shell alive" signal for test doubles whose pty exits on the first read; the fix was the fixture, not a guard in the view model
metadata:
  type: project
---

`TerminalViewModel.IsAcceptingInput` goes false by watching `TerminalSession.Exited` and posting
through the dispatcher. The watch is registered **unconditionally** at the end of `Adopt` —
`ContinueWith` on an already-completed task just runs straight away, which is right for a shell that
died during the spawn.

**Why:** an earlier `if (!session.Exited.IsCompleted)` guard there was a real defect — a shell
exiting between the spawn and that sample was never watched, so the pane kept claiming every key in
the window forever. It only looked necessary because the terminal test doubles
(`RecordedPtySession`, `RecordingPty`) complete `Exited` on the first `ReadOutput` that finds no
bytes, so a fixture built on `RecordingPty([])` was indistinguishable from an exited shell, and
`TerminalRun.Start()` waited on `IsAcceptingInput` as its proxy for "adopted". Both were fixture
faults: `Start()` now waits on `RenderState.Value is TerminalRenderState.Running`, and the input
fixture uses `SeamPty`, which stays open until `ShellExits()`/`Dispose`.

**How to apply:** when a terminal test double's liveness looks like it forces a guard in the view
model, suspect the double first. A replayed recording whose bytes have run out is legitimately
`Running` *and* legitimately not accepting input — do not conflate the two.
