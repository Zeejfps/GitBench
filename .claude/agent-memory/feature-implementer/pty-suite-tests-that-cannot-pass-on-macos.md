---
name: pty-suite-tests-that-cannot-pass-on-macos
description: Three GitBench.Pty.Tests cases whose child program, not the implementation, is what fails them on macOS — don't chase them
metadata:
  type: project
---

`GitBench.Pty.Tests` sits at 189/192 on macOS with two hard reds and one ~1-in-3 flake. All three are
the test's chosen child, not `UnixPtySession`. Verified by probe; see
[[unix-pty-platform-facts]] for the platform measurements behind them.

- `PtySessionSpawnTests.Start_SeesTheOverlaidPath_EvenThoughItDidNotResolveAgainstIt` — overlays PATH
  with an empty temp directory and then asks `PtyChild.PrintsVariables`, whose Unix arm reports
  through `env | awk`. Both are now off PATH, so the child prints
  `sh: env: command not found` and the token can never appear. Only a shell-builtin report (`$PATH`)
  could work here.
- `PtySessionTeardownTests.WriteInput_FromTwoThreadsAtOnce_NeitherDeadlocksNorThrows` — writes ~20 KB
  at `PtyChild.SitsSilently`, which never reads stdin, so the writers park in `write(2)` once the
  ~1 KB input queue fills. The test's stated intent (two writers neither deadlock nor throw) passes
  against any child that reads.
- `PtySessionIoTests.ReadingAndWritingConcurrently_LosesNothingAndDoesNotDeadlock` — types ~2.5 KB at
  an interactive bash; the tail is intermittently discarded, `exit 5` with it, and the failure reads
  "The shell did not exit". 3 ms of pacing between lines takes the loss rate from 5/15 to 0/12.

**Why:** each looks like a session bug — a lost variable, a deadlocked writer, a shell that will not
exit — and each costs an hour to trace back to the child.

**How to apply:** if these three are the only reds, the implementation is fine. Do not add an input
queue or a write pump to chase the last two: the suite's own doc for
`WriteInput_WithMoreBytesThanTheTerminalTakesAtOnce` describes `WriteInput` as a synchronous resuming
write loop, which is what is built.
