---
name: pty-seam-facts-gitbench
description: Load-bearing facts about the GitBench.Pty seam — what the frozen types promise, which behavioural contracts were invented by the test suite, and the measured platform facts that decide them
metadata:
  type: project
---

Facts settled while reviewing the Unix half of `GitBench.Pty` (2026-08-29, branch `terminal`).

**The seam is frozen by decree, but doc `<remarks>` are not.** `IPtySession`, `PtyExit`, `PtySize`,
`PtySessionOptions`, `PtySpawnException` must not change shape. A doc-comment edit breaks no call
site and is outside what the freeze protects — say so rather than letting a false doc stand. Three
docs are currently wrong or silent and were recommended for change: `ReadOutput`'s "end of stream
arrives at or after `Exited`" (false on Unix — the slave's last close can end the stream before
`waitpid` returns); `PtyExit.Completed`'s "ran to completion" (a signalled child did neither);
`WriteInput`'s missing "a write to a terminal whose child is gone is dropped".

**Behavioural contracts the suite invented, all upheld on review:**
- A child that signals *itself* is `Completed(128 + signal)`, not `TornDown`. The case is decided by
  who initiated it, not by the wait status. A third `PtyExit` case was rejected: the type's own
  remarks demand a throwing `default` arm on every switch, so adding a case is a silent *runtime*
  break at every call site — the worst reversibility profile available.
- `WriteInput` after the child exits does not throw. This is *forced*, not chosen:
  `TerminalSession.Drain` writes DSR replies on the UI thread and catches only
  `ObjectDisposedException`.
- `ReadOutput(empty buffer)` returns 0 — `Stream.Read` sets the precedent and the API is read-shaped.

**Measured on this Mac (do not re-derive):**
- **EINTR is unreachable from managed code.** A thread parked in a P/Invoke `read()` saw *zero*
  EINTR across 3s of child-process churn, 3s of forced blocking gen-2 GCs, and 200 each of
  self-directed SIGWINCH/SIGCHLD/SIGCONT/SIGPIPE/SIGUSR1. A P/Invoke thread is preemptive so the GC
  does not suspend it, and every handler the runtime installs uses `SA_RESTART`. **AC-24's stated
  rationale ("the runtime signals threads for GC suspension") is false.** Retry EINTR anyway, but
  know it cannot be tested live.
- `getconf ARG_MAX` = 1048576; a 4 MiB argument gives E2BIG (errno 7) deterministically. This is the
  one errno a test can produce on demand, and the only way to pin the `Other` arm of the spawn map.
- macOS revokes the controlling terminal when the session leader dies, so **every teardown test here
  passes against an implementation that would hang on Linux.** A grandchild that calls `setsid()`
  escapes `kill(-pgid)` and holds the slave; Linux never revokes. The suite cannot reach this.

**`GatedHandle<T>` is necessary and not sufficient.** It prevents fd-*number* reuse under a blocked
caller — a bigger win on Unix than Windows, since a recycled descriptor lands silently on somebody
else's file. It is not a wakeup and never claimed to be; the wakeup is the platform session's
(process-group kill *plus* a self-pipe + `poll`, not either alone).

Related: [[recurring-seam-mistakes-gitbench]], [[seam-conventions-gitbench]]
