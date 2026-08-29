---
name: pty-child-snippets
description: How to validate GitBench.Pty test children on macOS — python pty harness, the /bin/sh TERM=dumb trap, and the measured facts behind each POSIX snippet
metadata:
  type: project
---

Validating the shell snippets `GitBench.Pty.Tests/PtyChild.cs` spawns, when `UnixPtySession` is still a stub.

**Why:** the contract tests are red by construction until the Unix session exists, so a snippet that
is subtly wrong stays invisible behind the `NotImplementedException` and only surfaces after someone
has implemented the whole session.

**How to apply:** validate every `sh` snippet under a real, *sized* pty before proposing it.
`script -q /dev/null <cmd>` gives a pty but **no winsize**, so `stty size` reports `0 0` under it and
it cannot validate AC-2. A ~50-line python harness (`pty.openpty` + `TIOCSWINSZ` + `fork`/`setsid`/
`TIOCSCTTY` + `execvpe`) does validate size, resize (`TIOCSWINSZ` on the master mid-run), typed input
and exit status. Dump the real `PtySessionOptions` each intent produces from a throwaway xunit `[Fact]`
and replay *those* argv through the harness, so what runs is what the C# actually generates.

Measured traps:

- `/bin/sh` on macOS is bash 3.2 and **gives itself `TERM=dumb` as a shell variable when the
  environment has none**. `${TERM-unset}` therefore reports `dumb` for a variable that `env` and
  `printenv` both agree is absent. A test for "the session invents no terminal identity" must read
  the *process environment* (`env | awk ...`), never `$TERM`.
- `stty size` prints `rows cols` — rows first; the assertions read `cols=…;rows=…`.
- macOS `pwd` returns the physical path (`/private/var/folders/…`) while .NET `Path.GetTempPath()`
  returns `/var/folders/…`. Assert on `TempDirectory.Token`, never on `TempDirectory.Path`.
- `posix_spawnp` on a non-executable regular file returns `EACCES`; on a missing name, `ENOENT`.
- `sh -i` with a `PS1` overlay prints that prompt under a pty (preceded by `ESC[?1034h`, which
  `VtText` strips), echoes typed lines, and propagates a typed `exit 7` as `WEXITSTATUS` 7. Set
  `ENV=null` in the overlay so nothing gets sourced.
- At 80 columns the echoed keystroke line wraps mid-token; `VtText.Squash` is what makes the
  assertion survive it.

Related: [[verifying-red-suites]], [[terminal-engine-seam]].
