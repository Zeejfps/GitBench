# Terminal — a real terminal inside DiffDino

> **Framing:** the goal is running `claude` (and things like it) *in the app* — not showing git
> output in a nicer pane. That single requirement sets the bar at xterm-class emulation: an Ink/React
> TUI streams tokens through cursor addressing, truecolor SGR, bracketed paste, synchronized output
> and capability queries that expect real replies. A line-oriented "command console" cannot host it,
> so the cheap options are off the table before we start.
>
> The plan is therefore: **vendor a proven VT engine behind a seam**, and own everything around it —
> PTY, input encoding, renderer, session lifecycle. If we later want our own engine, it drops in
> behind the same seam without touching the other four modules.

## Decisions

| Area | Decision |
|---|---|
| Scope | A general terminal that hosts arbitrary TUIs. `claude` is the acceptance target; `vim`, `less`, `lazygit` are the stress tests. |
| Engine | Vendor **XtermSharp** (MIT, a C# port of the xterm.js core: parser + grid + modes, no renderer) into `framework/`, behind an `ITerminalEngine` seam. It is unmaintained — that is acceptable, because a VT engine is a spec-frozen artifact and we own the vendored source. Consistent with how this repo already carries Glfw.NET, OpenGL.NET, PngSharp, JpegSharp, ZGF.Svg. |
| Own engine later | The seam exists from day one and the conformance corpus is engine-independent, so a hand-rolled backend is a swap, not a rewrite. Not scheduled. |
| PTY | Ours, thin. `IPtySession` with two implementations: ConPTY (Win10 1809+) and `openpty` + `posix_spawn(POSIX_SPAWN_SETSID)` on macOS/Linux. Vendor Pty.Net's native-helper approach only if the controlling-terminal dance fights back. ConPTY is measured and sufficient — see Findings. |
| Renderer | Ours, a ZGF.Gui widget painting a cell grid. Requires a new cell-grid text path on `ICanvas` (see Modules). |
| Input | Ours. We own the key encoder, so the **kitty keyboard protocol** is implemented natively and Shift+Enter works with no `/terminal-setup` step. |
| Testing | **Recorded byte corpora replayed against golden grid snapshots.** We cannot drive a subscription-authenticated `claude` from CI, so we capture real sessions by hand once, commit the bytes, and replay them forever. Engine tests are pure `bytes → grid`. |
| Placement | A third mode in the switcher: **Changes │ History │ Terminal**. One session per repo, cwd at the repo root. |
| Shell | The user's interactive login shell, so PATH/nvm/rc files are live — same reasoning as `GitProcessRunner.GitLaunch.Shell`. `TERM=xterm-256color`, `COLORTERM=truecolor`. |
| Relationship to the assistant | **Unrelated.** `docs/plans/assistant.md` builds an in-process agent over domain tools; this hosts a shell. They share no code and neither blocks the other. Running `claude` in the terminal is not the assistant backend. |
| Out of scope for v1 | Tabs and splits, ligatures, color emoji, inline image protocols (kitty/iTerm2), scrollback reflow on resize, in-terminal search, shell integration marks. |

## Modules

Seven pieces plus one throwaway. Each has a real boundary; the middle three are where the weeks go.

**1. `GitBench.Pty` — the process side.**
`IPtySession`: spawn (argv, cwd, env), a byte read stream, a byte write path, `Resize(cols, rows)`,
exit notification, dispose. Two implementations behind it. Signals come free — the kernel line
discipline turns Ctrl-C into SIGINT for the foreground process group, we just pass the byte. Reader
runs on its own thread and hands batches to the UI thread via the existing `IUiDispatcher`.

It lives in the app repo rather than `framework/` for now; it is a GitBench concern until something
else needs it. `TERM` and `COLORTERM` are the caller's to set — no platform sets them for us.

**2. `GitBench.Terminal.Vt` — the engine seam.**
`ITerminalEngine`: `Feed(ReadOnlySpan<byte>)` in, and out of it a grid, a damage region, a response
byte stream (DA/DSR replies go back up the PTY), and observable terminal state — title, cursor
position/visibility/shape, alt-screen flag, bracketed-paste flag, kitty-keyboard flags, mouse mode.
The vendored XtermSharp adapter implements this. Everything downstream talks only to the seam.
Like module 1 it lives in the app solution rather than `framework/`, for the same reason: it is a
GitBench concern until something else needs a VT engine.

**3. The grid surface.**
What the renderer is allowed to read: rows of cells (rune, fg, bg, attribute bits), the scrollback
window, cursor, selection. Deliberately narrow and snapshot-shaped, because it is the contract the
golden tests assert against and the thing a future own-engine has to satisfy.

**4. `ZGF.Terminal.Input` — key → bytes.**
The encoder: application-cursor and keypad modes, kitty CSI-u progressive enhancement with a
modifyOtherKeys fallback, SGR 1006 mouse encoding, bracketed-paste wrapping, and the
copy/paste/clipboard bridge. Pure and table-testable.

**5. The renderer widget.**
Cell metrics from the monospace advance, run coalescing (one background rect and one glyph run per
style run, not per cell), cursor and selection painting, scrollback and wheel, damage-driven
repaint, and read coalescing so a `cat` of a large file collapses into one frame instead of
thousands.

**6. A cell-grid text path on `ICanvas`.**
The one framework change. `DrawText` shapes a string, and `FreeTypeFontBackend`'s shape cache is a
256-entry generational bucket — a repainting TUI would thrash it every frame. We need a glyph-run /
fixed-advance draw that skips shaping on the ASCII fast path and places wide characters and
combining marks on cell boundaries deliberately rather than by luck. Underline and strikethrough
(SGR 4/9) land here too, as lines.

**7. `GitBench/Features/Terminal` — the app side.**
Mode integration, per-repo session lifecycle (create on first use, keep alive across mode switches,
tear down with the repo), view model, focus and keybind arbitration, and settings — font size,
shell override, scrollback depth.

**8. The probe harness (throwaway, but its output is permanent).**
A PTY logger with an escape-sequence decoder: spawn a program, dump every byte it writes with
sequences named and tallied, replay our keystrokes at it. Its output is the capability inventory
that gates the engine choice *and* the recorded corpus the test suite runs on forever.

## Phases

**Phase 0 — the mode slot.** `MainViewMode.Terminal`, a third `SegmentViewModel` in
`ModeSwitcherViewModel`, a case in `MainContent`, an `AppModeTerminal` string in the catalogs, and a
placeholder widget behind it. Touches four small files, ships on its own, and gives everything after
it a place to land. Done.

**Phase 1 — PTY and probe.** Get a shell spawned on all three platforms and log what comes back.
Record `claude`, `vim`, `less`, `git rebase -i` into committed corpora. **Deliverable: the sequence
inventory.** It answers "how far off is XtermSharp?" with a diff instead of a guess, and it is the
gate on the next phase. The Windows transport half of this is done — see Findings; what remains on
Windows is the real `ConPtySession` and the recordings. The Unix half is untouched.

**Phase 2 — engine in.** `ITerminalEngine`, the vendored adapter, and the replay tests green against
the Phase 1 corpora. No UI yet — this phase is headless and fully testable.

**Phase 3 — pixels.** The canvas glyph-run path, then the grid renderer. First light is `claude`
visibly running inside the Terminal mode, even if the keyboard is still crude.

**Phase 4 — keyboard.** The encoder, kitty enhancement, paste, mouse, and the focus-modality
decision: the terminal must swallow Esc, Tab, Ctrl-C, arrows and most chords, which collides head-on
with `AppKeybindController` and every dialog's Esc handling. Also the Cmd/Ctrl+C ambiguity —
copy-selection versus SIGINT.

**Phase 5 — session and polish.** Resize and SIGWINCH, scrollback and wheel, selection and copy,
window title, OSC 8 links, per-repo lifecycle, settings. Resize and scrollback are done — see
Findings. What remains here is selection and copy, paste, the title, OSC 8, the per-repo lifecycle
and the settings.

**Phase 6 — conformance and throughput.** The corpus suite as a regression gate, streaming-repaint
performance, and optionally `esctest` for anything the corpora miss.

## Findings — ConPTY, measured

Run as a spike ahead of Phase 1, because the Windows engine choice hangs off it. A throwaway C#
ConPTY host (`CreatePseudoConsole` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` + `CreateProcess`)
driving `cmd`, `node` and `powershell` on Windows 10.0.26200, SDK 10.0.26100. These are
measurements, not readings of the documentation. The Unix side remains unmeasured.

**The transport does what a TUI needs.**

| Check | Result |
|---|---|
| `CreatePseudoConsole` | `S_OK` |
| Child sees a terminal | `node`: `process.stdout.isTTY === true` |
| Size propagation | requested 137×41, child reported `cols=137 rows=41` |
| Input round-trip | keystrokes written to the master reached and ran in `cmd` |
| `ResizePseudoConsole` | `S_OK`; child's live console moved 80×24 → 120×40 |

**Reaches us intact, so our engine owns it.** Truecolor SGR (`CSI 38;2;…m`), synchronized output
(`?2026h/l`), alt screen (`?1049`), SGR mouse (`?1006`), modifyOtherKeys (`CSI >4;2m`), and —
the one that mattered — **the kitty keyboard protocol**: both `CSI ? u` (query) and `CSI > 1 u`
(push) arrive at the master unanswered. conhost does not intercept them, so kitty negotiation is
ours to implement and the no-`/terminal-setup` decision in the Input row holds on Windows.

**conhost answers before we see it.** `CSI 6n` (DSR) and `CSI c` (DA1) never reach the master;
conhost replies to the application itself, advertising its own attributes
(`CSI ?61;6;7;21;22;23;24;28;32;42c`). Our engine therefore cannot describe itself to a program on
Windows. Little consequence for `claude`, which feature-detects through `TERM`/`COLORTERM`, but
anything gating on DA1 is negotiating with conhost, not with us.

**Two things worth knowing before writing `ConPtySession`.**

The spawn must set `STARTF_USESTDHANDLES` with all three std handles null. Without it the child
uses whatever std handles it inherited and bypasses the pseudoconsole entirely — while
`CreatePseudoConsole` still returns `S_OK`, the child still runs, and the captured stream still
contains conhost's startup and teardown frames with none of the content in between. It reads as a
ConPTY failure and is a spawn failure.

There is no passthrough mode. `consoleapi.h` in SDK 10.0.26100 declares exactly one flag,
`PSEUDOCONSOLE_INHERIT_CURSOR`. The reflow is not opt-out-able. (Win11 adds
`ReleasePseudoConsole`, for detaching without killing the child — possibly useful in Phase 5.)

**Teardown, measured separately.** Run because the end-of-stream contract on `IPtySession` hangs off
it. Same host, same machine.

| Check | Result |
|---|---|
| EOF on child exit alone | **No.** `cmd /c exit 7` exited with code 7; the reader drained 16 bytes and then blocked for 3s with no end of stream. |
| EOF after `ClosePseudoConsole` | Yes — and 70 further bytes arrived first, then `ERROR_BROKEN_PIPE` (109). |
| `ClosePseudoConsole` with nobody draining | Returned in 0ms, with the child alive (`STILL_ACTIVE`) and 4119 bytes sitting unread in the pipe. |
| Kill, then close, then read | `TerminateProcess` → child gone in 1ms → close returned → the blocked reader saw EOF. 31ms end to end. |

Three consequences, all of them now in the seam.

**The session owns the teardown, and the caller keeps reading through it.** A child exiting does not
close the terminal, so `Exited` completing is not the end of the output — the session must close the
pseudoconsole once it observes the exit, and the reader must drain *past* that close, because the
flush arrives after it. A reader that stops at `Exited` truncates the session's last output.

**The `ClosePseudoConsole` deadlock does not reproduce.** The widely-repeated warning is that closing
blocks unless something is draining; measured with the pipe demonstrably full and the child
demonstrably alive, it returned immediately. Teardown ordering is therefore ours to choose rather
than forced.

**`Dispose` needs no cancellable I/O.** Killing the child runs the ordinary teardown, which ends the
stream, which releases a reader blocked in a synchronous read. No overlapped reads, no `CancelIoEx`,
and none of the handle-reuse races that come with cancelling a read from another thread.

## Findings — the scrollback, as built

The viewport's position lives on `TerminalSession`, not in the engine and not in the view. The engine
has no scroll position on purpose — the same grid must not read differently depending on the UI — and
the view cannot hold one either, because following the shell is a rule about *output arriving* and
the session is the only thing that sees output arrive.

**A reader who has scrolled back has to be carried by the output.** Each feed says how many lines
left the top of the screen, and a viewport that is not at the bottom moves with them; leaving it
alone would have the text crawling under someone who has not touched the wheel. This is what made
`FeedResult.LinesScrolled` load-bearing, and measuring it exposed that the number was wrong: it was
the growth of the history's depth, which stops the moment the history is full while lines keep
leaving the screen. It is now its own counter in the vendored engine (patch 16).

**The alternate screen needs no special case.** Its buffer has no scrollback at all, so the history
is zero rows deep while a full-screen program is up and the ordinary clamp pins the viewport to the
live screen. Leaving the alternate screen therefore lands at the bottom, which is where it belongs.

**Typing comes back to the prompt**, and it is the view model that does it rather than the keyboard,
so that every path to the shell lands somewhere the sender can see. The engine's own replies to a
program's questions go straight to the session and deliberately do not: a program asking the terminal
its size must not yank the screen out from under whoever is reading it.

**The wheel is not the whole of the wheel.** Scrolling the history is what the wheel does on the
normal screen with no mouse tracking on. The other two cases both belong with mouse reporting: a
program that has asked for mouse events wants the wheel as an SGR report, and one on the alternate
screen that has not wants the alternate-scroll convention (`?1007`, wheel as arrow keys) or nothing.
Until that lands the wheel does nothing over a full-screen program, which is the visible gap.

## Risks

| Risk | Note |
|---|---|
| XtermSharp coverage gaps | Most likely kitty keyboard, synchronized output (`?2026`), maybe truecolor edges. Phase 1 quantifies it; we own the source, so gaps are patches, not blockers. Narrowed on Windows: all three reach the engine intact, so the only question left is whether XtermSharp implements them, not whether the transport delivers them. |
| Unix spawn / controlling terminal | The genuinely fiddly native bit. `forkpty` in a managed runtime is what everyone does and nobody likes; `posix_spawn` with `SETSID` plus a non-`O_NOCTTY` slave open is the clean version. Budget real time here, isolated in Phase 1. |
| Glyph atlas is single-channel | `GlyphAtlas` is alpha-only, so color emoji are impossible without an RGBA path. Claude Code's UI is box-drawing and symbols, which JetBrains Mono covers — but emoji in *tool output* will tofu. Accepted for v1. |
| Keyboard ownership | Not a late bug-fix; a modality design decision in Phase 4, subject to the parent-owns-modality rule. |
| Streaming repaint cost | Rendering is instanced glyph quads with growable VBOs and damage-driven frames, so the GPU is not the worry — shaping and per-frame allocation are. Module 6 is the mitigation. |
| ConPTY quirks | Confirmed and bounded (see Findings). On resize it re-emits its buffer and sends us `CSI 8;rows;cols t`; there is no passthrough flag to turn that off. Costs redundant repaints, not correctness. Windows still needs its own corpus — treat the two platforms as separate recordings asserting the same grid, rather than pretending one stream. |
| conhost owns DA1/DSR | Capability replies on Windows are conhost's, not ours, and we cannot override them. Fine for `claude`; a constraint on anything that negotiates through DA1. |
| Own-engine cost on Windows | Escaping conhost entirely means reimplementing a console host over the undocumented ConDrv protocol — months, plus a permanent break-on-update liability. Ruled out; the divergence above does not come close to justifying it. |
