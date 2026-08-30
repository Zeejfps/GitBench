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
| Placement | A third mode in the switcher: **Changes │ History │ Terminal**. Inside it, a title bar of tabs over one grid: several terminals per repository, one of them on screen. A terminal is still a repository's and its cwd is the repo root. |
| Title bar | A strip above the grid: one tab per terminal, a `+` on the trailing edge, and the active shell's title. The tabs are the commit-details tabs — `CommitTabChrome` and `TabClickController` move out of `Features/Commits` into a shared `Controls/TabStrip.cs` and both surfaces build on it — so the X, the middle-click close, the ellipsis at the width cap, the active and hover fills and the overflow scroller stay one implementation rather than two that drift. |
| Closing a tab | Confirmed when the tab holds a live shell, immediate otherwise. Same reasoning as `ConfirmQuitDialog` — a shell mid-build is not something to lose to a stray middle click — and the same modal machinery, with the tab rather than the application as the subject. An idle, exited or failed tab has nothing to lose and closes on the click. |
| Shell | The user's interactive login shell, so PATH/nvm/rc files are live — same reasoning as `GitProcessRunner.GitLaunch.Shell`. `TERM=xterm-256color`, `COLORTERM=truecolor`. |
| Relationship to the assistant | **Unrelated.** `docs/plans/assistant.md` builds an in-process agent over domain tools; this hosts a shell. They share no code and neither blocks the other. Running `claude` in the terminal is not the assistant backend. |
| Out of scope for v1 | Splits, reordering tabs by drag, renaming a tab, detaching one into its own window, ligatures, color emoji, inline image protocols (kitty/iTerm2), scrollback reflow on resize, in-terminal search, shell integration marks. |

## Modules

Eight pieces plus one throwaway (module 8). Each has a real boundary; the middle three are where the weeks go.

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

**9. The title bar — `GitBench/Features/Terminal`, over one control lifted out of `Features/Commits`.**
A strip above the grid: tabs on the left, a `+` on the trailing edge, the active terminal's title, and
below it the grid showing whichever tab is active. The pill is not new work. `CommitTabChrome`,
`TabClickController` and the `HorizontalScrollArea` the commit strip pans its overflow with are already
the general article — a label ellipsizing at a width cap, an optional close button, the row-selection
fill when active and the hover fill otherwise, left-click to activate on release and middle-click to
close, both armed on press so a drag off the tab cancels — so they move to `GitBench/Controls/TabStrip.cs`
and the commit surface keeps using them from there. What is terminal-specific is only what the strip is
bound to and what closing means. `CommitTabChrome`'s `Viewed` predicate is the one part that does not
generalise: it becomes an optional leading-slot widget the caller supplies, rather than a mark the shared
control knows the meaning of.

## Phases

**Phase 0 — the mode slot.** `MainViewMode.Terminal`, a third `SegmentViewModel` in
`ModeSwitcherViewModel`, a case in `MainContent`, an `AppModeTerminal` string in the catalogs, and a
placeholder widget behind it. Touches four small files, ships on its own, and gives everything after
it a place to land. Done.

**Phase 1 — PTY and probe.** Get a shell spawned on all three platforms and log what comes back.
Record `claude`, `vim`, `less`, `git rebase -i` into committed corpora. **Deliverable: the sequence
inventory.** It answers "how far off is XtermSharp?" with a diff instead of a guess, and it is the
gate on the next phase. Both transports are built — `ConPtySession` on Windows, `UnixPtySession`
plus the `ShellCommand` controlling-terminal acquirer on macOS and Linux, all three with tests. What
remains is **the Unix corpus**: every committed recording is a Windows capture. See the Findings
note below, which is the honest state of the test suite's coverage.

**Phase 2 — engine in.** `ITerminalEngine`, the vendored adapter, and the replay tests green against
the Phase 1 corpora. No UI yet — this phase is headless and fully testable.

**Phase 3 — pixels.** The canvas glyph-run path, then the grid renderer. First light is `claude`
visibly running inside the Terminal mode, even if the keyboard is still crude.

**Phase 4 — keyboard.** The encoder, kitty enhancement, paste, mouse, and the focus-modality
decision: the terminal must swallow Esc, Tab, Ctrl-C, arrows and most chords, which collides head-on
with `AppKeybindController` and every dialog's Esc handling. Also the Cmd/Ctrl+C ambiguity —
copy-selection versus SIGINT.

**Phase 5 — session and polish.** Resize and SIGWINCH, scrollback and wheel, selection and copy,
window title, OSC 8 links, per-repo lifecycle, settings. Resize, scrollback, the wheel — mouse
reports and alternate scroll included — the per-repo lifecycle, and selection, copy, paste and
OSC 52 are done, and so are OSC 8 links; see Findings. What remains here is the window title —
the engine parses OSC 0/2 and `TerminalState.Title` carries it, but nothing above the seam reads it
yet, and the thing that wants to read it is the tab, so it lands with Phase 7 — and the settings, all
three of which are still constants: `DefaultScrollbackLines`, the renderer's `FontSize.Body`, and a
shell that cannot be overridden.

**Phase 6 — conformance and throughput.** The corpus suite as a regression gate, streaming-repaint
performance, and optionally `esctest` for anything the corpora miss.

**Phase 7 — the title bar and tabs.** Four steps, each of which ships on its own.

1. **Extract the tab strip.** `CommitTabChrome`, `TabClickController` and the overflow scroller move to
   `GitBench/Controls/TabStrip.cs`, and the commit-details strip is rebuilt on them with no visual
   change. A pure refactor, and the commit surface is its own regression test.
2. **One terminal per repository becomes several.** `TerminalSessionStore` keys a *list* per repo id
   with an active index instead of a single instance. `Active` still projects the one terminal the pane
   draws; `HasLiveShell` and `ReposWithLiveShells` count over the whole list, so the quit confirmation
   keeps naming repositories and starts being right about a repository whose live shell is in a tab that
   is not on screen.
3. **The strip.** Tabs bound to the active repository's list, `+` opens another terminal — idle, since
   making one still starts nothing — click activates, X and middle-click close, overflow pans.
4. **Confirmation and title.** `ConfirmCloseTerminalDialog` beside `ConfirmQuitDialog`, gated on the tab
   holding a live shell, and `TerminalState.Title` finally read above the seam as the tab's label. That
   closes the window-title item Phase 5 left open.

Splits are the obvious next thing and are deliberately not here: a split is two grids on screen at once,
which is a different question from which of several sessions is on screen, and answering it early would
put a layout tree under the pane before there is anything to put in it.

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

**The wheel is three wheels, decided in one place.** Scrolling the history is what the wheel does on
the normal screen with no mouse tracking on, and it is the last of three cases rather than the only
one. A program that has asked for mouse events gets the wheel as a report in the encoding it asked
for; a full-screen program that has not gets the alternate-scroll convention (`?1007`, the wheel as
cursor keys), which is the only scrolling a buffer with no scrollback can offer; anything else moves
the pane's own viewport. The pane picks between them on the modes the engine reports, so a program
that switches modes mid-session switches what the wheel means without the pane holding any state
about it.

## Findings — the controlling terminal, measured

**The child was never getting one, and it cost every full-screen program its resizes.** The pane
resized, the engine resized, `TIOCSWINSZ` reached the terminal and the size the child read back was
correct — and no `SIGWINCH` was ever delivered, so a TUI kept drawing at the size it started at.
Ctrl-C reached nothing for the same reason. The kernel signals a terminal's *foreground process
group*, and the terminal had none.

**`POSIX_SPAWN_SETSID` plus opening the slave without `O_NOCTTY` does not acquire the terminal on
macOS.** That is what `UnixPtySession` assumed, in a comment that read like a citation, and it is
wrong: no file action can issue the `TIOCSCTTY` that would, and measurement says the open alone does
nothing. Timed from the parent, `tcgetpgrp(master)` is 0 the instant the child starts under every
shell; under bash it becomes the child's group about 25ms later, because **bash takes a controlling
terminal for itself at startup and zsh never does**. macOS's default shell is zsh. That is the whole
reason this looked like "TUIs are broken" rather than "the terminal is broken", and why the existing
`/dev/tty` spawn test passed throughout: its child is a shell, and the shell repaired what the spawn
had failed to give it.

**So the pane starts the shell through one.** `ShellCommand` spawns `/bin/sh -c 'exec "$0" "$@"'`
with the user's shell as `$0`: `sh` takes the terminal, `exec` hands it — with the process, the
descriptors and the session — to the shell that was asked for. It is deliberately *not* in
`GitBench.Pty`, where it was tried first: routing every spawn through a shell breaks that layer's own
contracts — a missing executable stops being a spawn failure and becomes exit 127, `PATH` starts
resolving against the child's environment rather than this process's, and bash quietly drops `PS1`
from the environment it passes on. Twelve of its tests said so. The layer's honest fix is `fork` +
`login_tty` or a native helper, which is the fallback this plan already names; until then the app
arranges what it needs and `ShellCommandTests` pins both halves — a child started through the
acquirer owns its terminal, and one started without it, on macOS, does not.

## Findings — the mouse, as built

**The reports are ours, like the keys.** `TerminalMouseEncoder` is the mouse's `TerminalKeyEncoder`:
pure, static, and a table — button and action and cell in, bytes out, and false when the program has
not asked for that event at all. The vendored engine has an encoder of its own (`Terminal.SendEvent`)
and it is deliberately unused, for the reason module 4 exists: input encoding is the part we own, and
routing it through the engine would put the seam in the middle of it. Gating lives with the encoding
because they are the same table — X10 tracking reports presses and no modifiers, normal tracking adds
releases, button-event adds motion while a button is down, any-event adds motion with none — and a
caller that had to know which events to offer would be reimplementing that table at every call site.

**Where the pointer is, is the renderer's answer.** A cell has no size until there is a canvas to
measure it against, and how far back the reader has scrolled is part of the same picture, so
`ITerminalCellGeometry` is implemented by the view and answers in the live screen's coordinates. A
point over the history is not a cell of the live screen and is not reported at all, which is what
keeps a program from being told about a click on text that scrolled past it minutes ago.

**A report is not a keystroke.** Mouse reports go to the session through `SendMouse`, which does not
return the viewport to the live screen the way typing does. A program tracking the pointer would
otherwise drag the screen back to the shell every time the mouse crossed a cell, which would make
scrolling back impossible while it is up. Motion is also reported once per cell rather than once per
event: a move is dispatched to the focused controller and again through the hover path, and the
pointer crosses a cell far less often than it moves.

**A resize is the pane's worst case, and it broke on the saved cursor.** A full-screen program
brackets its session in `?1049`, which saves the shell's cursor going in and restores it coming out,
and the window can be resized in between. The vendored buffer saved that cursor as a screen row while
adjusting it as a buffer row, so the restore landed on a row the resized screen no longer had and the
next printed character indexed past the buffer — a `NullReferenceException` on the thread that owns
the window, which is why resizing while Claude Code was up took the pane with it. Patch 18 fixes the
cause; the session now also catches a feed that throws and faults the pane rather than the
application, on the same reasoning as the reader thread's catch.

**Modifiers on the wheel had to be remembered, because nothing reports them.** GLFW's scroll callback
is two coordinates and no mods, so `MouseWheelScrolledEvent` had none to carry and xterm's
Shift-takes-the-wheel-back convention could not be implemented at all. Keys and mouse buttons *do*
arrive with the modifier state attached, so `DesktopInputSystem` keeps what it last saw on one and
stamps it onto the wheel. That is a cache, and the failure it can have is staleness — a chord
released while the window is away is never seen being released here — so it is cleared on
`OnFocusChanged(false)`. Querying the key state at scroll time would not go stale, but it needs a new
`IWindow` member and a backend behind it, and the cache uses the surface that already exists.

With that in place Shift is one chord rather than two: it takes the wheel back the way it already
took the page keys, so the history is reachable while a program is tracking the mouse. Other
modifiers are not taken back — they ride along in the report, which is what a program zooming on
Ctrl+wheel is listening for.

**A notch and a trackpad are not the same unit.** Both arrived as a delta multiplied by one
`WheelLines`, which is right for a wheel — one deliberate click, a few rows — and far too fast for a
trackpad, which spends a whole gesture in small deltas and crossed the history in a flick. The event
already carried `GesturePhase` and `MomentumPhase` and nothing had ever read them; a precise device
now takes `PreciseWheelLines` instead. Momentum is matched separately from the gesture, because it
arrives after the fingers lift with no gesture phase left on it, and the tail of every flick would
otherwise accelerate.

## Findings — one terminal per repository, as built

**A terminal is a repository's, and the pane is only ever looking at one of them.** They live in
`TerminalSessionStore`, keyed by repo id and made on first activation, which is the same shape the
assistant's conversations already have. The pane binds the store's active terminal rather than
reading the registry, so switching repositories swaps which shell is on screen while the others keep
running — and the pane cannot end up pointed at the repository that happened to be active when the
Terminal tab was first opened, which is what it did before.

**Drawing a pane no longer starts a shell.** The old view model spawned on the first viewport report,
which comes from a draw, so opening the tab was the same event as launching a process. Now the
viewport report only records the size, and `Start` is the only thing that spawns. Asked for before
the pane has been measured, it waits for the first size rather than guessing one — the "never spawn
at 80x24" rule the phase-0 notes give, kept intact by moving the trigger rather than the arithmetic.

**Exit is a state, not a flag beside one.** `Running` used to sit next to a `_shellExited` bool that
every reader had to consult, which is a pair that can disagree. The states are now
`Idle | Starting | Running | Exited | Faulted | Failed`; whether input is accepted, whether the
screen is drawn and whether a shell can be started are each one question about one value. `Exited`
and `Faulted` keep their session, so the screen a finished command left stays readable and
scrollable, and the renderer's switch over the six throws on anything it has no drawing for rather
than quietly showing the wrong thing.

**A spawn is handed over, not posted.** The spawn runs on a worker and posts its result to the UI
thread, and a post is not a handover: the loop can stop in between, and then nothing runs the
adoption and a live shell is owned by nobody. The session is now recorded under a lock before the
post, disposal takes whatever is sitting there, and a spawn that lands after disposal ends it on the
thread that made it.

**A terminal with no shell does not take the keyboard.** The input controller stole focus on any
press, whatever its state — which for an idle pane means holding focus while declining every key, so
the application's own chords die for as long as the pointer is over nothing else. The steal is now
gated on there being a shell, which is also what lets a click reach the start gate stacked over the
grid. The gate hands focus back on the way out, so the click that starts a shell is the last one
needed before typing.

**Removal is not only the user's doing.** Worktree and submodule reconciliation removes repository
rows nobody touched, and the store ends those terminals: a shell whose working directory has been
pruned has nowhere to be. It reconciles against the live list on every change rather than matching
removals, because a list says it was cleared or reset without saying what left.

## Findings — selection, copy and paste, as built

**A selection is carried by output, and the alternate screen is the special case the scrollback did
not have.** The viewport's position and a selection are both properties of this screen rather than of
the bytes, so both live on `TerminalSession` — but they do not obey the same rule. Clamping a scroll
offset that is already zero is a no-op; clamping a *span* collapses both ends onto one row, so a
selection is **dropped rather than clamped** when the text it covers leaves the history. Clamping
would move the ends onto rows the user never highlighted and hand them text they never selected.

And `LinesScrolled` cannot carry it on the alternate buffer. The counter increments there too —
`Terminal.Scroll` takes the `ScrollTop == 0` branch whichever buffer is active, and the alternate
buffer is always full — while `ScrollbackRows` stays zero, so shifting by it walks a selection off a
grid whose only rows are the visible ones. A full-screen program under a scroll region does not
increment it at all while its text still moves. So the shift applies only on the normal screen, a
selection is cleared on either alt-screen crossing, and on the alternate screen it is positional and
goes stale under the program — which is what every terminal does with it.

`CSI 3J` and RIS empty the history with no line ever leaving the screen, so there is no count to
carry a selection by and no resize to hang a clear off. `CopyRow` throws below `-ScrollbackRows`, on
the UI thread, outside the one `try` the session has — so the text builder is total against whatever
grid it is handed rather than trusting the span it was given.

**The mouse has two coordinate systems and they are deliberately different methods.**
`ITerminalCellGeometry.TryLocate` still refuses a point over the history, because a mouse report must
never name a row that scrolled past. Selection needs the opposite, so it has its own member —
`ClampToGrid`, named for the coordinate system it answers in and total where the other is partial.
One method serving both would have put a history row within reach of the mouse encoder.

**The gesture is decided once, at the press.** Selecting and reporting are alternatives, so they are
one `TerminalGesture` value rather than a drag flag beside an anchor beside a mode read: releasing
Shift halfway through a drag cannot turn the rest of it into mouse reports aimed at a program that
never saw it start. Shift takes the drag back from a program tracking the mouse, which is the chord
that already takes back the wheel and the page keys. The press itself is left unconsumed, so the
start gate stacked over an exited screen is still clickable.

**Copy is claimed above the reserved-chord check, which is the whole macOS carve-out.** Every Super
chord is otherwise handed back to the application, so Cmd+C would have fallen through and done
nothing. The chords are Cmd on macOS and Ctrl+Shift elsewhere — Ctrl+C stays the interrupt, and a
terminal that swallowed it would be broken. Ctrl+Shift+C previously encoded to `0x03`, since
`TerminalKeyEncoder.WriteLetter` ignores Shift when Ctrl is held; that is now intercepted before the
encoder sees it. Copy is claimed whether or not anything is selected, so the chord means one thing.
It reaches an exited screen too, which is why focus is taken for any pane with a screen rather than
only one with a shell.

**Paste is sent as-is, and the bracket is the only thing the encoder edits.** Line endings become the
carriage return a keyboard would have sent, and `ESC [ 201 ~` is stripped from the payload when
bracketing — without that a crafted clipboard closes the bracket early and the remainder runs as
typed input. With bracketed paste off, a multi-line paste runs every line but the last. That is what
every terminal does and it is a deliberate decision rather than an oversight; the test that pins it
says so.

**A paste used to freeze the window, and that is a write-path defect the read path had already
solved.** The master is a blocking descriptor and a shell at a prompt has about a kilobyte of line
discipline to take, so writing 200 KB on the UI thread stopped the window until the child had read
all of it. `TerminalSession` now has a writer thread and a queue, mirroring the reader's own
buffering, so `Write` never blocks the caller. The cost is that "the shell has read this" stops being
true when `Write` returns, which is what `Flush` exists for and what the test doubles call.

**OSC 52's read half is denied in the adapter, not in the app.** Denied with an empty reply rather
than with silence, because a program that asks waits for it. There is deliberately no request case
for a read anywhere above the seam: what cannot be constructed cannot be wired up later by accident.
The write half is sanitised by `ClipboardText.FromProgram` — a clipboard is pasted into other
terminals, so a carriage return that hides what follows it is not something a program gets to stage
there on the user's behalf.

**Not built, deliberately:** drag auto-scroll past the pane edge. The wheel already reaches the
history, and the pane repaints on output rather than per frame, so it would have needed a tick of its
own.

## Findings — OSC 8 links, as built

**The seam carries an id, not a url, and the ids are never reused.** A cell is copied by value for
every row of every frame, so a string on it would be copied with it; an id is also the only thing
that answers "how far does this link go", because two cells are the same link when their ids match
whatever sits between them and whichever row a reflow has since put each on. The table behind them
mints monotonically and never recycles, which is what makes eviction safe: a full table drops its
oldest entry, deepest in the scrollback, and a cell still pointing there stops resolving and reads as
ordinary text. A recycled id would turn that stale cell into a link to somewhere the program never
named — VTE avoids it with a mark-and-sweep over live cells, and monotonic ids buy the same safety
with none of the sweep.

**The id sits beside the attribute rather than inside it, and that is the one placement worth
defending.** Folding it into `CellAttribute` would cost no extra field and would be wrong: only
another OSC 8 ends a link, `SGR 0` must not, and the attribute reset in `CharAttributes` would
silently terminate every link at the next `ESC[m` — which is exactly what a program emits between
the link text and what follows it. The blank matters as much as the cell for the same reason. `Print`
fills the insert-mode shift and a wide character's trailing column from one `empty`, and carrying
only the attribute onto it leaves the second column of a wide glyph outside the link it sits in,
which shows up as a hover highlight with a hole in it.

**The parse is below the seam, unlike OSC 52's.** The adapter only sees the engine at `Feed`
boundaries, by which time every cell has been written — and OSC 8 is cell state that must exist
*before* the cells naming it are printed, where a clipboard write is an action and can be handed up
and performed later. So the adapter is left a straight read, the same shape as `ContinuesPreviousRow`.

**The engine does not judge a url and the application does, which is one predicate and not three.**
`TerminalHyperlink` is deliberately a string and deliberately not a `Uri`: a terminal's job is to
report that a program marked these cells as a link to this text, and whether that text is worth
following has a different answer in every host. `TerminalLinkTarget.FromProgram` is the allowlist —
absolute `http`/`https` only, which is what keeps a `file:` or UNC url off `OpenUrl`, since that url
came off a pseudo-terminal and would launch whatever it named. It is also the *only* way to construct
one, so the hand cursor, the underline and the click all ask the same question. A link this
application will not open therefore gets no affordance at all, rather than looking clickable and
doing nothing.

**Where the pointer is, is asked again every time rather than remembered.** The cell under a
stationary pointer changes whenever the shell prints, and no pointer event is delivered when it does
— the same force `FollowSelection` exists to absorb. A remembered link would leave the hand cursor
over blank space and the highlight on whatever text scrolled into its old id's place. The view owns
the hover point, in window space because a grid coordinate is already a resolution against a scroll
offset that moves without the pointer, and everything else asks it; the controller keeping a copy
would be two fields holding one fact, and the one read every frame is the one that goes stale. Hover
is established on mouse *enter* as much as on move, because the input system's refresh delivers no
move to a freshly hovered controller.

**The highlight is drawn by id and not by column range**, so a link that wrapped the right margin —
or that a reflow has moved — lights up whole rather than in the half the pointer is in, and nothing
drawing it needs to know where the link started. It is an overlay beside the selection rather than a
rule from `ICellStyler`: the styler is immutable and shared across panes, and being hovered is a fact
about the pointer and not about the cell. Only on hover, because a program emitting OSC 8 almost
always underlines the text with SGR itself and a permanent rule would double it.

**Following a link is a modified click and a gesture, for the reasons the other gestures are.** A
bare click is left alone deliberately — it belongs to whatever full-screen program may be reading the
mouse, and a gesture that changed meaning when the user started `vim` would be worse than one
modifier. `FollowingLink` is a case of `TerminalGesture` beside selecting and reporting because a
press cannot be two of them, and a link held in a field alongside would let one Cmd+click both open a
link and clear the selection. The target is resolved again at the release and compared against the
one the press saw, which is what makes pointer staleness harmless on this path: the screen can scroll
between press and release, and opening what *was* under the pointer would open something nobody
aimed at. Travel cancels rather than becoming a selection — the modifier is held, so travel means
they changed their mind.

**The gap was invisible until the seam widened, which is the argument for the golden format
changing with it.** OSC 8 had no handler and fell to the parser's fallback, so the url was discarded
rather than leaked: the grid was correct, nothing failed, and no test could observe the difference.
The snapshot format now records `links` and `urls`, because without them an engine that threw OSC 8
away would still pass every golden — which is how this stayed unnoticed through sixteen patches.

## Findings — the corpus is Windows-only

**Every committed recording is a Windows capture, and the plan said the opposite.** All five
inventories name a Windows binary: `claude.exe`, `cmd.exe` for `smoke`, and — for `vim`, `less` and
`git-log` — the Git-for-Windows MSYS builds under `C:\Program Files\Git`. `claude` and `smoke` also
carry `CSI ?9001h` (win32 input mode), which no Unix terminal emits. The risk table used to say
"Windows still needs its own corpus"; Windows is in fact the only platform that has one.

The MSYS detail matters as much as the ConPTY one: `vim.exe` and `less.exe` from Git for Windows are
not the binaries a macOS user runs, so even the two full-screen stress cases are pinned against a
different program, not merely a different transport.

**What that costs.** `CorpusReplayTests`, `CorpusPropertiesSpec` and every golden in `Goldens/` are
the engine's regression gate, and all of them assert against bytes ConPTY produced. A divergence that
only appears on a Unix pseudo-terminal — a sequence conhost normalises away, an encoding the Unix
line discipline delivers differently, anything downstream of the `?9001` input mode — passes the
whole suite in silence. That is the platform this is being developed on, so the gate is weakest
exactly where the work happens.

**Capturing it means rebuilding the probe first.** Module 8 was deliberately throwaway and was never
committed; only its output was. So a Unix corpus is not a recording session, it is: rebuild the PTY
logger with its escape-sequence decoder, capture `vim`, `less` and `git log` on macOS, and capture
`claude` by hand on an authenticated machine — the inventory format is already pinned by
`Corpus.Load`, which parses the terminal geometry out of the inventory header rather than a table, so
a new probe has to reproduce that header exactly.

**Until then, treat green as "green on Windows bytes".** Nothing in the suite is wrong; it is
narrower than it reads.

## Design — the title bar and tabs, before building

Written ahead of Phase 7 rather than after it, because three of these are decisions that are cheap now
and expensive once there are tabs on screen.

**A tab is a terminal, and a terminal is still a repository's.** The store already holds the shape this
needs — keyed by repo id, an `Active` projection that swaps on repo switch, instances made lazily and
ended when their repository closes — and the change is that the value behind a key becomes a list with
an active index rather than one instance. Switching repositories therefore swaps the whole strip, and a
repository comes back to the tabs it had, with the one that was in front still in front. The alternative,
a flat list of terminals with the repository as a column, was not taken: a shell's cwd is the repo root,
so a tab that outlived its repository would be sitting in a directory that may have been pruned — which
is the case `DropClosedRepos` already handles, and it only stays simple while a terminal belongs to
exactly one repository.

**The tab strip is the commit surface's, moved rather than copied.** Everything the terminal tabs need
is already built and already argued about there: the width cap with the label in a `Grow` so a long name
ellipsizes into the pill rather than stretching it, the trailing 1px divider, the close button consuming
its own press so it does not also arm activation, middle-click close on release with the arm cancelled on
exit, and the `HorizontalScrollArea` that pans the strip — vertical wheel included — once the tabs
overflow. Copying it would mean two of each of those decisions, and the second copy is the one that
silently stops matching. The move is the first step of the phase precisely so that it lands as a refactor
with a working surface behind it, not as a rewrite hidden inside a feature.

**Closing asks only when there is something to lose, and asking is asynchronous.** A tab holding no
process closes on the click; a tab holding a live shell puts up the same modal `ConfirmQuitDialog` uses,
broadcast as a `ShowDialogMessage` and answered later through the presenter's callback. That has a
consequence the pill's `OnClose` signature hides: **a close request is not a close.** The strip must be
correct in the window between them — the tab stays exactly where it was, still active if it was active,
still taking output, because the shell is still running and its output is still arriving. And the removal
that eventually runs must find the terminal **by identity, not by index**: the dialog is modal to the
window but the list underneath is not frozen, since a repository can be closed and a shell can exit while
it is up. Confirming a close on a terminal that has since gone is a no-op, not an off-by-one that ends
the wrong shell.

**The confirmation is the tab's, not the application's.** `ConfirmQuitDialog` names repositories because
that is what a reader recognises when the whole application is closing. A tab close is one shell and the
reader is looking straight at it, so the dialog names that one terminal — its title, or its shell — and
says what it is about to end. Two dialogs rather than one parameterised over "how many things and of what
kind", because the sentences are different sentences and the plural machinery would be carrying a case
that never has more than one item.

**The title is the engine's; the fallback is the tab's.** `TerminalState.Title` already carries what OSC
0/2 sets, which is what a shell writes and what a program running under it overwrites — so a tab's label
follows the running command, which is the whole reason a tab strip is legible with four shells open. An
idle tab has no shell and therefore no title, and an exited one keeps whatever it last said, so the label
is the title when there is one and the shell's file name otherwise. Two terminals on the same repository
with the same title are told apart by a trailing index; the index is positional and belongs to the strip,
not to the terminal, since it means "the second one you can see" and nothing else.

**The strip is drawn even with one tab.** It carries the title and the `+`, so hiding it until a second
terminal exists would mean the grid jumping down by the strip's height under whoever just opened one, and
no visible way to open the first extra tab. Same reasoning as the underline strip in the working-changes
pane, which is always laid out and only ever changes what it paints.

## Risks

| Risk | Note |
|---|---|
| XtermSharp coverage gaps | Most likely kitty keyboard, synchronized output (`?2026`), maybe truecolor edges. Phase 1 quantifies it; we own the source, so gaps are patches, not blockers. Narrowed on Windows: all three reach the engine intact, so the only question left is whether XtermSharp implements them, not whether the transport delivers them. |
| Unix spawn / controlling terminal | The genuinely fiddly native bit, and it bit. `posix_spawn` with `SETSID` plus a non-`O_NOCTTY` slave open is *not* the clean version — on macOS it acquires nothing, and the child lands with no controlling terminal, no `SIGWINCH` and no Ctrl-C. The pane works around it by starting the shell through `/bin/sh -c 'exec "$0" "$@"'`; `GitBench.Pty` itself still hands a bare child no terminal, and its own fix is `fork` + `login_tty` or Pty.Net's native helper. See Findings. |
| Glyph atlas is single-channel | `GlyphAtlas` is alpha-only, so color emoji are impossible without an RGBA path. Claude Code's UI is box-drawing and symbols, which JetBrains Mono covers — but emoji in *tool output* will tofu. Accepted for v1. |
| Keyboard ownership | Not a late bug-fix; a modality design decision in Phase 4, subject to the parent-owns-modality rule. |
| Streaming repaint cost | Rendering is instanced glyph quads with growable VBOs and damage-driven frames, so the GPU is not the worry — shaping and per-frame allocation are. Module 6 is the mitigation. |
| ConPTY quirks | Confirmed and bounded (see Findings). On resize it re-emits its buffer and sends us `CSI 8;rows;cols t`; there is no passthrough flag to turn that off. Costs redundant repaints, not correctness. |
| **No Unix corpus** | Every committed recording is a Windows capture, so the engine is pinned against ConPTY's output only and nothing asserts what a macOS or Linux pseudo-terminal produces. The two platforms are separate recordings asserting the same grid, not one stream — and only one of them exists. See Findings. |
| Terminal identity is repo-scoped in three places | `TerminalSessionStore` keys by repo id, `HasLiveShell(repoId)` answers per repository, and `ConfirmQuitDialog` names repositories. Tabs make the terminal the unit while the quit path still wants repository names, so the store has to answer both questions over the same list. Getting it wrong shows up as the quit confirmation naming a repository whose live shell was in a tab that is already closed — or worse, not naming one whose live shell is in a tab that is merely not on screen. |
| A close request is not a close | The confirmation is a modal answered through a callback, so the tab lives on between the middle click and the answer, still taking output. Anything that removes by index rather than by identity ends the wrong shell when the list moves under the dialog. |
| conhost owns DA1/DSR | Capability replies on Windows are conhost's, not ours, and we cannot override them. Fine for `claude`; a constraint on anything that negotiates through DA1. |
| Own-engine cost on Windows | Escaping conhost entirely means reimplementing a console host over the undocumented ConDrv protocol — months, plus a permanent break-on-update liability. Ruled out; the divergence above does not come close to justifying it. |
