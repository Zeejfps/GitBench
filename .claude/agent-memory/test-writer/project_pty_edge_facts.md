---
name: pty-edge-facts
description: Measured POSIX pty edge behaviour on this macOS box — MAX_CANON discards long lines, write-after-exit is EIO, same-size TIOCSWINSZ is silent, and which Linux divergences are untestable here
metadata:
  type: project
---

Facts probed on this machine (macOS 26.6, Darwin 25.6) while writing the edge suite for the Unix
half of `GitBench.Pty`. All measured, none inferred.

**Why:** these are the boundaries an edge suite has to assert on, and every one of them is a place
an implementation quietly does the wrong thing. Re-deriving them costs a C probe and a python pty
harness each time.

**How to apply:** reach for these before writing any pty boundary test; check them against Linux
docs before claiming a test covers Linux.

- **`posix_spawnp` errno mapping.** Missing name, missing absolute path, empty string, and a script
  with a broken shebang all return **ENOENT (2)**. A directory and a non-executable regular file both
  return **EACCES (13)**. It returns the errno as its *return value* and leaves the global `errno`
  alone, so two failures that must map to different `PtySpawnFailure` values is the cheapest test
  that catches an implementation reading the wrong variable.
- **`posix_spawnp` ignores the PATH in the envp it is handed** and resolves against the *calling
  process's* PATH. Measured with an envp holding only a `PATH=` pointing at a directory containing
  the program: ENOENT. CreateProcessW searches the parent's environment too, so the contract is the
  same on both platforms — an overlaid PATH reaches the child but does not decide what the child is.
- **A single line longer than MAX_CANON (1024 on macOS) is discarded by a canonical-mode tty.**
  4000 bytes written as one line arrived as zero. A bulk-write test must either use many short
  newline-terminated lines or `stty raw` first. In raw mode a single 65536-byte write round-tripped
  through `cat` complete.
- **`write(master)` after the child exited returns EIO.** So `WriteInput` on a dead session must
  swallow it — a keystroke typed a moment too late is normal, and the seam documents only
  `ObjectDisposedException`.
- **`read(master)` after the child exited returns 0, repeatedly, on macOS.** Buffered output the
  child wrote before dying still comes out first. On Linux the same situation returns -1/EIO.
- **`ioctl(TIOCSWINSZ)` with the size the tty already has delivers no SIGWINCH.** Resize-to-current
  is a silent no-op; assert it by resizing for real afterwards, not by waiting for nothing.
- **1x1 and 65535x65535 both work**: `stty size` reports them exactly. Note `ConPtySession.ToCoord`
  throws above `short.MaxValue`, so sizes above 32767 are a Unix-only assertion even though
  `PtySize` admits them.
- **`exit` truncates to 8 bits**: 256 -> 0, 300 -> 44, -1 -> 255. Windows keeps 32 bits, so exit-code
  truncation is a Unix-gated theory. `kill -9 $$` gives the shell's 137 (128 + signal).
- Validated markers: `[ -t 0/1/2 ]` (never inside `$(...)` — command substitution makes fd 1 a pipe,
  so `-t 1` always answers no); `: > /dev/tty` succeeds only with a controlling terminal;
  `case "$(ps -o stat= -p $$)" in *+*)` detects the foreground process group.

**Untestable on macOS — say so rather than pretending:**

- **AC-16's Linux EIO-as-EOF.** macOS returns 0. The same test passes on both, via different paths.
- **AC-22's grandchild case.** BSD *revokes* the tty when the session leader dies, so killing only
  the direct child already yields EOF here even with a `sleep 600 &` holding the slave. Measured.
  On Linux it would not. The test is right and vacuous here.
- **AC-24 EINTR.** No test can force it. CoreCLR does not signal threads already in native code, so
  a GC-pressure test provokes nothing. The nearest honest thing is a SIGCHLD burst, which can pass
  vacuously but cannot fail spuriously.

Related: [[pty-child-snippets]], [[verifying-red-suites]].
