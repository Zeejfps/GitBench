---
name: pty-lifecycle-seams
description: Seam/lifecycle testing for GitBench.Pty — the nohup trick that makes the grandchild teardown test non-vacuous on macOS, GatedHandle's leak-instead-of-close failure mode, the fd-number leak probe, and the platform-gate lint
metadata:
  type: project
---

The composition half of the Unix `GitBench.Pty` work: teardown, threading, resource ownership, and
how to keep 24 unskipped tests honest.

**Why:** happy-path spawn assertions pass on macOS against implementations that hang forever on
Linux, and the suite has no way to say so unless the tests are built to discriminate.

**How to apply:** reach for these when writing or reviewing any `IPtySession` lifecycle test.

- **The grandchild teardown test is only vacuous if the grandchild is killable by SIGHUP.** BSD
  revokes the tty when the session leader dies, so a plain `sleep 600 &` grandchild dies either way
  and proves nothing. Measured with `nohup /bin/sh -c '...' >/dev/null 2>&1 &`: ending only the
  direct child leaves it **running**; `killpg` ends it. So a test that reads the grandchild's pid
  from a file and asserts it is gone after `Dispose` **does** tell kill-pid from kill-process-group
  on macOS. This corrects the "right and vacuous here" note in [[pty-edge-facts]].
- **`GatedHandle<T>` turns "close under a blocked reader" into "leak under a blocked reader".**
  `Close()` defers the real close to the last caller that `Leave()`s. A reader blocked forever in
  `read()` never leaves, so the master fd and the reader thread leak instead of faulting. That is
  the right trade on Windows (ending the child ends the pipe) and only safe on Unix if teardown
  reaches every holder of the slave — hence the process group, or a self-pipe wakeup.
  `GatedHandle` had **zero tests**; five direct tests against a `TrackingHandle : SafeHandle` are
  what make moving it out of `Platforms/Windows/` safe without a Windows machine. They pass.
- **Leak probe without /proc:** `open` returns the lowest unused descriptor, so
  `File.Create(...).SafeFileHandle.DangerousGetHandle()` is the fd high-water mark, portably across
  macOS and Linux. Warm up with one cycle, then assert it did not move across eight — a per-session
  leak of 2 fds shows as 16. Windows handle values do not work this way; gate it Unix-only.
- **Gate the platform-specific tests three ways** (`[PtyFact]` / `[WindowsPtyFact]` /
  `[UnixPtyFact]`) and add a reflection lint `[Fact]` asserting the attribute and the
  `…OnWindows`/`…OnUnix` name suffix agree, plus that no universal test names a platform. It is the
  only mechanical guard against a test silently claiming a contract it proves on one host.
- **A raw `Thread` in a test whose body can throw crashes the whole test host** (`Test Run Aborted`,
  every other result lost). Always `Record.Exception` inside the thread body and assert on the test
  thread. Cost me one run.
- **App-side composition risk to keep in view:** `TerminalSession.Write`/`Resize` guard only
  `ObjectDisposedException`, and `TerminalSession.Dispose` joins its reader for 2s after
  `_pty.Dispose()`. So `WriteInput` after the child exits must swallow EIO, `Resize` must not throw,
  and `Dispose` must release a blocked reader well inside 2s or the pane leaks a thread.

Related: [[pty-child-snippets]], [[pty-edge-facts]], [[verifying-red-suites]].
