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
| PTY | Ours, thin. `IPtySession` with two implementations: ConPTY (Win10 1809+) and `openpty` + `posix_spawn(POSIX_SPAWN_SETSID)` on macOS/Linux. Vendor Pty.Net's native-helper approach only if the controlling-terminal dance fights back. |
| Renderer | Ours, a ZGF.Gui widget painting a cell grid. Requires a new cell-grid text path on `ICanvas` (see Modules). |
| Input | Ours. We own the key encoder, so the **kitty keyboard protocol** is implemented natively and Shift+Enter works with no `/terminal-setup` step. |
| Testing | **Recorded byte corpora replayed against golden grid snapshots.** We cannot drive a subscription-authenticated `claude` from CI, so we capture real sessions by hand once, commit the bytes, and replay them forever. Engine tests are pure `bytes → grid`. |
| Placement | A third mode in the switcher: **Changes │ History │ Terminal**. One session per repo, cwd at the repo root. |
| Shell | The user's interactive login shell, so PATH/nvm/rc files are live — same reasoning as `GitProcessRunner.GitLaunch.Shell`. `TERM=xterm-256color`, `COLORTERM=truecolor`. |
| Relationship to the assistant | **Unrelated.** `docs/plans/assistant.md` builds an in-process agent over domain tools; this hosts a shell. They share no code and neither blocks the other. Running `claude` in the terminal is not the assistant backend. |
| Out of scope for v1 | Tabs and splits, ligatures, color emoji, inline image protocols (kitty/iTerm2), scrollback reflow on resize, in-terminal search, shell integration marks. |

## Modules

Seven pieces plus one throwaway. Each has a real boundary; the middle three are where the weeks go.

**1. `ZGF.Terminal.Pty` — the process side.**
`IPtySession`: spawn (argv, cwd, env), a byte read stream, a byte write path, `Resize(cols, rows)`,
exit notification, dispose. Two implementations behind it. Signals come free — the kernel line
discipline turns Ctrl-C into SIGINT for the foreground process group, we just pass the byte. Reader
runs on its own thread and hands batches to the UI thread via the existing `IUiDispatcher`.

**2. `ZGF.Terminal.Vt` — the engine seam.**
`ITerminalEngine`: `Feed(ReadOnlySpan<byte>)` in, and out of it a grid, a damage region, a response
byte stream (DA/DSR replies go back up the PTY), and observable terminal state — title, cursor
position/visibility/shape, alt-screen flag, bracketed-paste flag, kitty-keyboard flags, mouse mode.
The vendored XtermSharp adapter implements this. Everything downstream talks only to the seam.

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
it a place to land.

**Phase 1 — PTY and probe.** Get a shell spawned on all three platforms and log what comes back.
Record `claude`, `vim`, `less`, `git rebase -i` into committed corpora. **Deliverable: the sequence
inventory.** It answers "how far off is XtermSharp?" with a diff instead of a guess, and it is the
gate on the next phase.

**Phase 2 — engine in.** `ITerminalEngine`, the vendored adapter, and the replay tests green against
the Phase 1 corpora. No UI yet — this phase is headless and fully testable.

**Phase 3 — pixels.** The canvas glyph-run path, then the grid renderer. First light is `claude`
visibly running inside the Terminal mode, even if the keyboard is still crude.

**Phase 4 — keyboard.** The encoder, kitty enhancement, paste, mouse, and the focus-modality
decision: the terminal must swallow Esc, Tab, Ctrl-C, arrows and most chords, which collides head-on
with `AppKeybindController` and every dialog's Esc handling. Also the Cmd/Ctrl+C ambiguity —
copy-selection versus SIGINT.

**Phase 5 — session and polish.** Resize and SIGWINCH, scrollback and wheel, selection and copy,
window title, OSC 8 links, per-repo lifecycle, settings.

**Phase 6 — conformance and throughput.** The corpus suite as a regression gate, streaming-repaint
performance, and optionally `esctest` for anything the corpora miss.

## Risks

| Risk | Note |
|---|---|
| XtermSharp coverage gaps | Most likely kitty keyboard, synchronized output (`?2026`), maybe truecolor edges. Phase 1 quantifies it; we own the source, so gaps are patches, not blockers. |
| Unix spawn / controlling terminal | The genuinely fiddly native bit. `forkpty` in a managed runtime is what everyone does and nobody likes; `posix_spawn` with `SETSID` plus a non-`O_NOCTTY` slave open is the clean version. Budget real time here, isolated in Phase 1. |
| Glyph atlas is single-channel | `GlyphAtlas` is alpha-only, so color emoji are impossible without an RGBA path. Claude Code's UI is box-drawing and symbols, which JetBrains Mono covers — but emoji in *tool output* will tofu. Accepted for v1. |
| Keyboard ownership | Not a late bug-fix; a modality design decision in Phase 4, subject to the parent-owns-modality rule. |
| Streaming repaint cost | Rendering is instanced glyph quads with growable VBOs and damage-driven frames, so the GPU is not the worry — shaping and per-frame allocation are. Module 6 is the mitigation. |
| ConPTY quirks | It reflows and re-renders on its own terms; Windows will need its own round of corpus work. |
