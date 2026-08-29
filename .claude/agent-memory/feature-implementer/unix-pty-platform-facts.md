---
name: unix-pty-platform-facts
description: Four macOS pty facts that decided the UnixPtySession design and that no amount of code review would have caught — measured on this machine
metadata:
  type: project
---

Measured on this Mac (Darwin 25, arm64) while building `GitBench.Pty/Platforms/Unix`. Each one
changed the implementation; re-measure only if a test contradicts one.

1. **A variadic libc function cannot be P/Invoked with a fixed signature on Apple arm64.**
   `ioctl(fd, TIOCSWINSZ, &size)` declared as three parameters returns -1 or writes garbage, because
   Apple's ABI passes variadic arguments on the *stack* while a non-variadic call puts the third in
   `x2`. Six `nint` pads before the pointer put it in the first stack slot and it works. On
   x86-64 and on arm64 Linux the plain three-parameter form is the correct one — so the declaration
   has to be chosen at runtime. `fcntl` has the same problem, which is why the descriptors are opened
   `O_CLOEXEC` and the pipe is closed with a `posix_spawn_file_actions_addclose` instead.

2. **macOS discards a pty's queued output when the last slave descriptor closes.** A write to the
   slave followed by `close` leaves the master reading 0, with no process involved. So a child's last
   line is gone the moment it exits unless something already read it, and the session needs a drain
   thread and a buffer of its own. Linux keeps the bytes.

3. **Holding the parent's copy of the slave open wedges the child in `exit`.** It looks like the
   obvious fix for (2) — keep the queue alive — but the session-leader child then sits in `ps` state
   `?Es` indefinitely and `waitpid` never returns.

4. **The pty input queue is about 1 KB (TTYHOG).** Writing more than that faster than the child reads
   either blocks the writer indefinitely (child not reading at all) or is silently discarded
   (interactive bash flapping between raw and canonical mode). Three tests in the suite write 2.5–20 KB
   at a child that cannot keep up; see [[pty-suite-tests-that-cannot-pass-on-macos]].

**Why:** every one of these is invisible in the code and only shows as a wrong number, a hang, or an
intermittently missing line.

**How to apply:** before blaming `UnixPtySession` for a lost byte, a 0x0 terminal, or a hang at exit,
check which of these four is in play.
