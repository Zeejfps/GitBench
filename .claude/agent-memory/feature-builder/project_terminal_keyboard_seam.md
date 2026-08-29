---
name: terminal-keyboard-seam
description: Decisions taken while building terminal key input — the reserved set, F5, AltGr, and the traps in InputSystem that any future keyboard work will hit
metadata:
  type: project
---

Terminal pane keyboard input (branch `terminal`, built 2026-08-28). The decisions below were taken by me, not the user, and several are flagged in the delivery report as pending ratification.

**Why:** A terminal must swallow Esc, Tab, arrows and most Ctrl chords, which collides head-on with `AppKeybindController`. The user's rule was "the terminal wins everything except a small reserved set (mode switching and repo hotkeys stay with the app)" — deliberately a rule rather than an enumeration, because any enumeration of what a terminal swallows is incomplete.

**How to apply:**

- **The reserved set ended up as exactly two rules:** any chord including `Super`, and `PrimaryModifier`+`Alpha1..9`/`Numpad1..9` (repo hotkeys). Nothing else. F5, Escape, Tab, arrows, `Ctrl+B`, `Ctrl+K`, `Ctrl+C` all go to the shell. There is no keyboard mode-switching in the app (the mode switcher is click-only), so "mode switching stays with the app" cost nothing.
- **No framework change was needed for focus modality**, contrary to the initial worry. `InputSystem.DispatchKeyboardKeyEvent` hands the event to `_focusedComponent` *first*, in `Bubbling` phase, before it walks `_focusQueue` where `AppKeybindController` lives. A focused controller that consumes simply wins. Check this before proposing arbitration changes.
- **`_focusQueue` is built by hit-testing from the hovered view's ancestor chain, not from registration.** So "the terminal declined the key" is necessary but *not sufficient* for an app keybind to fire — with the pointer over nothing, a declined key reaches nobody. Pre-existing framework behaviour; this feature is the first to depend on it.
- **`InputModifiers` mixes lock state (`CapsLock`, `NumLock`) with chording modifiers in one flags enum, and the lock bits are set on most real hardware.** Any predicate written as `modifiers == InputModifiers.Control` is right on a dev machine and wrong everywhere else, and any translation that copies the bits into an xterm modifier parameter turns every arrow key into `CSI 1;33 C`. `AppKeybindController.RelevantMask` exists for exactly this; I widened it and `PrimaryModifier` to `internal` so the terminal shares one definition instead of restating it.
- **Known accepted cost, worth raising if a European-layout user appears:** Windows reports AltGr as `Control|Alt`. On a German/French/Polish layout AltGr+Q types `@`, and the legacy rule encodes that chord as `ESC 0x11` and consumes it as a Command — which suppresses the character. Fixing it properly needs a seam that defers the key decision until the OS says whether a character followed; xterm and Windows Terminal both special-case Ctrl+Alt for this reason.
- **`TerminalKey` deliberately has no `Ctrl+[` / `Ctrl+]` / `Ctrl+\`** (0x1B/0x1D/0x1C — the vim chords). Not an oversight: `KeyboardKey` is a *physical* position, so mapping physical `LeftBracket` to `0x1B` hardcodes a US layout, which is the exact thing `TextInputEvent`'s own doc warns against. Deferred to the user as a follow-up.
- **`TerminalViewModel` never learns its shell exited.** `IsAcceptingInput` is true forever once `Running`; after the user types `exit`, keystrokes vanish into `TerminalSession.Write`'s `catch (ObjectDisposedException)` and nothing tells them. `TerminalRenderState` has `Starting`/`Running`/`Failed` but no `Exited`. Out of scope here (session lifecycle was explicitly deferred) — it is a state, not a controller fix.
- **Verifying a live shell is cheap and worth doing in phase 0**, before any test exists: a throwaway console project in the scratchpad referencing `GitBench.Pty` spawns ConPTY directly and answers "does this actually work" in one run. It also caught that **`pwsh` is not installed on this machine** — `ShellCommand` falls back to `powershell.exe`, and there is no fallback below `ShellCommand`, so hardcoding `pwsh.exe` dies with `Win32Exception (2)` inside `ConPtySession.StartChild`.
