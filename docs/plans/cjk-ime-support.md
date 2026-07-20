# CJK Text Input — IME composition support

> GitBench can *render* Chinese, Japanese and Korean today, but it cannot *type* them. Latin and
> Cyrillic work because they are one-keystroke-one-character, which is all GLFW's char callback
> delivers. CJK is not: you type romaji/pinyin/jamo, an IME shows a **composition (preedit)** string
> and a candidate list, and only on commit does real text exist. There is no IME layer anywhere in
> the codebase. This plan adds one. Phases are ordered outside-in — each lands something runnable.

> **Status: phases 1–6 implemented.** The composition path is built and covered headlessly
> (`TextInputImeTests`, `TextInputUnicodeTests`, `ImeCoordinatorTests`); the full suite is green.
> Still open, in order: **manual verification on a real IME** (nothing here has been typed into by a
> human yet — the tests are synthetic), the **GLFW regression pass** per platform, and phases 7–8.
> Phase 8 is Windows-only and opens with a spike that may end it.
>
> Two items from this list have since closed in `ime-text-input-focus.md`: the natives are now built
> in CI from a pinned commit, and the "IME stays on outside a text field" compromise in Phase 6 is
> gone — see that document rather than trusting Phase 6's closing notes on their own.

## What already works (do not rebuild)

The rendering half of CJK is done and is not the problem:

- **Shaping + fallback** — `framework/ZGF.Fonts/FreeTypeFontBackend.cs`: FreeType + HarfBuzz,
  `ShapeWithFallback` itemizes a run by cmap coverage across registered fallbacks.
- **CJK fonts from the OS** — `GitBench/Platform/SystemFonts.cs` + `GitBench/App/AppHostSetup.cs`:
  one system font per script family (JP/SC/KR), nothing bundled.
- **CJK line breaking** — `framework/ZGF.Gui/TextWrapper.cs`: UAX-14-lite `IsWide` classes plus
  kinsoku (`IsNoBreakBefore` / `IsNoBreakAfter`).
- **Layout-independent text entry** — `framework/ZGF.Gui.Desktop/Input/TextInputEvent.cs`: a `Rune`
  committed by the OS, never a decoded physical key.

## The gap

Stock GLFW (3.3.7 on Windows, 3.4.0 on macOS — they are currently **mismatched**) exposes no IME
API. Concretely, in GLFW 3.4's `cocoa_window.m`, `GLFWContentView` conforms to `NSTextInputClient`
but *stores marked text in an ivar and never surfaces it*, and `firstRectForCharacterRange` returns
a zero rect at the window origin. On Win32, all `WM_IME_*` messages fall through to `DefWindowProc`.

The consequences, in order of severity:

1. **No preedit.** While composing, the app renders nothing; the composition lives in an OS window.
2. **Candidate window is misplaced** — it sits at the window origin instead of following the caret.
3. **Keys leak during composition.** GLFW's `keyDown:` calls `_glfwInputKey()` *before*
   `interpretKeyEvents:`, and the Win32 path translates by scancode. So pressing **Enter to select a
   candidate can also submit the commit** mid-composition. This one is destructive, not cosmetic.

## Decision: vendor a patched GLFW

Use the GLFW IME patch ([glfw/glfw#2130](https://github.com/glfw/glfw/pull/2130), head
`clear-code/glfw@im-support`). It implements **Win32, Cocoa, X11 and Wayland behind one API**:
`glfwSetPreeditCallback`, `glfwSetPreeditCursorRectangle`, `glfwResetPreeditText`,
`glfwSetInputMode(GLFW_IME)`, plus `glfwSetPreeditCandidateCallback`.

**Rejected — swizzling `GLFWContentView` from C#.** It fixes macOS *only*: Windows would still need a
separate imm32 HWND-subclassing implementation and Linux would get nothing. It also means
`class_replaceMethod` on ~5 Objective-C methods, hand-matching the `NSRect` return ABI (`sret` on
x86_64 vs. register-passed HFA on arm64), walking `NSAttributedString` runs to recover underline
clauses, and suppressing GLFW's original `insertText:` IMP to avoid double-insertion — all through
native function pointers under NativeAOT. Several hundred lines of ABI-sensitive interop for one
platform.

**Rejected — writing our own IME engine.** Korean is a deterministic automaton and genuinely easy,
but Japanese kana→kanji and Chinese pinyin→Han need a weighted lexicon plus phrase-level ranking —
conversion quality *is* the product. Users also lose their configured input method, learned
dictionary, and muscle memory (pinyin vs. shuangpin vs. wubi vs. zhuyin). Every serious toolkit
(Chromium, Qt, Flutter, Avalonia, VS Code, Zed) uses the OS IME. Note this changes nothing about the
framework work below: the composition event stream, preedit rendering and key gating are needed
either way — only the *engine* would differ.

**The patch is not merged** (open since 2022; maintainer said "will review"). Mitigation: pin a
commit, don't chase master. It is actively rebased and is shipped in production by **LWJGL 3.3.4**,
whose prebuilt natives already carry it.

## Phase 0 — spike (DONE — passed)

Verified before committing to any of this:

- Parsed the Mach-O export trie and the PE export table of LWJGL 3.3.4's natives: **all four RIDs
  (win-x64, linux-x64, macos-x64, macos-arm64) export the full preedit API.** The Windows DLL
  dynamically links `imm32` and calls `ImmGetCompositionStringW` / `ImmSetCandidateWindow`.
- Swapped LWJGL's `glfw.dll` in as `framework/Glfw.NET/Native/win-x64/glfw3.dll` (import name is
  `glfw3`, so a rename is all that is needed).
- Added `framework/Glfw.NET/GlfwIme.cs` (P/Invokes + `PreeditCallback` + an `IsSupported()` probe, so
  an unpatched native degrades gracefully instead of throwing `EntryPointNotFoundException`).
- Wired a preedit logger into `OpenGlWindow` (`ImeSpikeLog` → `%TEMP%\gitbench-ime-spike.log`).

**Result — confirmed on Windows with Microsoft Pinyin:**

```
[ime] IME enabled on window (isMain=True). Patched GLFW detected.
[ime] preedit "n"      caret=1 blocks=[1] focused=0
[ime] preedit "ni"     caret=2 blocks=[2] focused=0
[ime] preedit "ni'h"   caret=4 blocks=[4] focused=0
[ime] preedit "ni'hao" caret=6 blocks=[6] focused=0
[ime] preedit END (committed or cancelled), caret=0
```

Committed text (你好) reached the text field and rendered through the existing system-font fallback.
Latin typing was unaffected. No regression observed from the 3.3.7 → 3.4 bump (title bar, popups,
resize).

**Two findings that change the design:**

1. **Preedit is the IME's own formatted string, not a replay of keystrokes.** Microsoft Pinyin
   inserted the syllable-separator apostrophe in `ni'hao` — nobody typed it. Render exactly what GLFW
   hands us; never reconstruct the composition from key events.
2. **`preedit END` is identical for commit and for cancel** — an empty preedit either way. The event
   cannot distinguish them. On commit the text arrives separately on the char callback; on cancel
   nothing does. So composition-end handling must be driven by the char path (see Phase 2).

## Phase 1 — native supply (DONE)

> The spike's artifacts did **not** survive: by the time this was implemented, `GlfwIme.cs` and the
> `OpenGlWindow` hook were gone from the tree and `win-x64/glfw3.dll` was back to stock GLFW 3.3.7
> (no `glfwSetPreedit*` exports). Phase 0/1 was re-landed from scratch rather than resumed.

All four natives now carry the IM-support patch, taken from **LWJGL 3.3.4** (which builds from a GLFW
fork carrying it) rather than compiled here — see `framework/Glfw.NET/Native/README.md` for
provenance and how to reproduce. GLFW is zlib-licensed, so shipping the binaries is fine with the
notice retained (`Native/LICENSE.glfw`).

- Upstream GLFW commit `b35641f4a3c62aa86a0b3c983d163bc0fe36026d`, reporting version **3.5.0**.
- Verified every RID exports the full preedit API before vendoring. On macOS a string search finds
  nothing — Mach-O compresses export names into a prefix trie, so the trie has to be parsed.
- **The `osx-x64` slot must stay universal.** LWJGL ships *thin* dylibs, but the csproj's RID-less
  macOS fallback (plain `dotnet run`) bundles the `osx-x64` file, so a thin x86_64 dylib would fail
  to load under an arm64 runtime on Apple Silicon. The two thin slices are `lipo`'d into one fat
  binary.
- Unifies the version skew as a side effect (was win 3.3.7 / linux 3.3.6 / macOS 3.4.0).
- **Linux:** `NativeLibraryResolver` already prefers the app-local `libglfw.so.3` over the distro's
  unpatched one, and that ordering is now load-bearing — if the distro copy won, Linux would silently
  lose the IME.
- **Done, since:** building the natives ourselves in CI. Vendoring LWJGL's build was the shortcut
  taken here, and it came due in `ime-text-input-focus.md` — no LWJGL release exports
  `glfwSetTextInputFocus`, so the shortcut could not reach the fix at any version. The natives now
  build from a pinned `clear-code/glfw@im-support` commit via the framework repo's `glfw-natives`
  workflow, with provenance and checksums in `Glfw.NET/Native/README.md`.
- **Not done:** the regression pass on the 3.3.7 → 3.5.0 bump (title bar, popups, resize) on each
  platform.

## Phase 2 — composition event stream (framework) (DONE)

Mirrors the existing `TextInputEvent` path:
`GLFW callback → IWindow.OnPreedit → DesktopInputSystem → InputSystem → controller`.

- `IWindow` gains `OnPreedit` plus `SetImeEnabled` / `SetPreeditCursorRect` / `ResetPreedit`. Both
  backends compose one `GlfwImeBridge` (`ZGF.Desktop/Input/`) rather than duplicating the wiring —
  `MetalWindow` drives windowing through GLFW too, so one native serves both.
- `PreeditText` restates GLFW's **UTF-32 code points** and code-point-indexed block sizes as a UTF-16
  string with UTF-16 offsets, so the rest of the text stack indexes it like any other string. Astral
  characters are where the two indexings diverge, and the conversion is what keeps them aligned.
- `CompositionEvent` routes through `InputSystem.SendCompositionEvent` to the focused controller,
  including the same searchable-context-menu hand-off the char path has.
- Committed text still arrives on the char path, so Latin typing is untouched.
- An empty preedit means only "drop the preedit" — it cannot distinguish commit from cancel.
- The IME is enabled only while a field is being edited (`BeginEditing`/`EndEditing`). Two GitBench
  rename controllers were hand-rolling `StartEditing` + `StealFocus` and so would never have turned
  it on; they now go through `BeginEditing`.

## Phase 3 — preedit in `TextInputView` (DONE)

- The composition lives in `_preedit`, apart from `_buffer`, and never notifies `TextValue`. Drawing
  and measuring read `_composed` — the buffer with the preedit spliced in at the caret — which equals
  the buffer when nothing is composing, so every draw path can read it unconditionally.
- One exception notifies: a composition started over a **selection** deletes it, and that deletion is
  a real edit. Without the notification, cancelling would leave the field rendering empty while the
  bound value still held the replaced text.
- Clause underlines come from the blocks (focused clause heavier), drawn through the same
  line-clipping the selection highlight uses, since either can straddle a soft wrap.
- The caret rect feeds `glfwSetPreeditCursorRectangle` so the OS candidate window follows the caret.
  `DesktopInputSystem` owns the conversion — GUI coordinates are Y-up from a bottom-left origin, the
  IME wants Y-down from a top-left one.
- The composition is reset on blur, click-away and focus loss. `BaseTextInputKbmController.OnFocusLost`
  is **sealed** and delegates to a new `OnFocusLostCore`, because four subclasses already overrode it
  without calling base and would each have silently dropped the IME teardown.

## Phase 4 — key gating while composing (DONE)

- The `IsComposing` gate lives in `BaseTextInputKbmController.OnKeyboardKeyStateChanged`, which
  consumes the key outright. **No separate gate in `AppKeybindController` is needed**, and adding one
  would be redundant: `SendKeyboardKeyEvent` dispatches to the focused component *first* and returns
  on consume, so the field structurally blocks the app's keybindings. That is asserted rather than
  assumed — see `EnterWhileComposing_DoesNotReachTheApp`.
- `TextInputImeTests` (15 tests) is the real deliverable: preedit/commit/cancel, keys not leaking,
  keys not editing the buffer, the gate not latching after the composition ends, composition at a
  mid-string caret, selection replacement, blur, interleaved Korean commits, astral-safe block
  offsets, and a Latin no-regression control.
- The leak tests were **initially vacuous** and had to be fixed: `InputSystem` builds its dispatch
  path from the *hover* chain, so with the pointer parked nowhere no ancestor controller is in it and
  "the key did not reach the app" passed no matter what. The tests now hover the field first, and
  `KeysAfterComposing_ReachTheAppAgain` is the control that keeps them honest. Deleting the gate
  fails exactly the three protection tests — verified.

## Phase 5 — editing-model correctness (DONE — was already fixed)

**Every code fix listed here had already landed** (the "fixed a bunch of text input bugs" commit),
which this plan predates. Confirmed against the current source:

- `MoveCaretLeft`/`MoveCaretRight` step through `TextBoundaries.Prev`/`Next`, and `Delete()` removes a
  whole cluster — not a raw `_caretIndex--` over UTF-16 code units.
- `TextInputView.GetLines` *does* call `TextWrapper.WrapRanges`, so kinsoku applies to the multi-line
  field.
- `TextBoundaries` has an `Ideographic` character class, so Ctrl+Arrow steps through a CJK run
  instead of treating it as one word.
- `GitBench/Controls/TextMeasure.cs` no longer exists; truncation is `ZGF.Gui/TextEllipsis.cs`, which
  has its own tests.

What was genuinely missing was the **coverage** the plan flagged: a `TypesAstralPlaneChars` test for
insertion but none for navigating or deleting. Added to `TextInputUnicodeTests` — backspace over an
astral char, arrow keys stepping over one without landing between the surrogate halves, and a CJK
word-jump.

## Phase 6 — CJK in popup-hosted fields (searchable context menus) (DONE)

Two live search boxes are affected: the repo picker (`RepoBarContextMenu.ShowSearchable`) and the
Review window's base-branch picker (`ReviewHeaderBar.cs:85`, which calls the same helper). Both take
Latin and neither takes CJK. This is a feature that is broken, not one that is missing, so it comes
before the atlas and the candidate window.

### Why it fails

A popup gets its own window and its own `DesktopInputSystem` (`PopupWindowFactory`), so its text
field resolves `InputSystem.ImeHost` to the *popup's* input system and enables the IME on the
*popup's* window. But a borderless popup never takes OS keyboard focus on Windows
(`WS_EX_NOACTIVATE`) — which is exactly why `DesktopInputSystem` already forwards keys and characters
from the host window to the focused menu. So the IME composes against the **host** window, whose
`GLFW_IME` mode is off: nothing composes, the keystrokes arrive as plain characters, and the box
takes `nihao` literally. The forwarding branch in `HandlePreeditEvent` is unreachable for want of a
preedit to forward.

**The Review window is the case that constrains the fix.** The host window is not always the main
window — a menu opened from a secondary window composes against *that* window. Nothing here may
reach for `MainWindow`.

The tempting fix — enable the IME on every app window — is **wrong and would regress the commit box**:
the menu's field disabling on close would turn the IME off under a main-window field that is still
editing, and that field would never turn it back on (it takes neither branch of the press handler
while `IsEditing` is already true).

### The fix — assert the IME from focus, don't toggle it from the field

Stop treating the IME mode as a one-shot toggle owned by the field. The three questions — which
window composes, which component receives the preedit, where the caret is — all have the same answer
today (whichever window has focus, unless a focused modal is up), but that rule currently lives
duplicated across `HandleKeyEvent` / `HandleTextEvent` / `HandlePreeditEvent` and the IME mode
doesn't consult it at all. Name it once and drive all four from it.

A new `ImeCoordinator` (`ZGF.Gui.Desktop`), one per app, holds:

- the registered per-window input systems (main, secondary, popup — the three `new
  DesktopInputSystem` sites);
- `Dictionary<DesktopInputSystem, RectF> _editingCarets` — every field currently editing, keyed by
  the window it lives in, with its caret rect in *that window's* canvas coordinates.

`DesktopInputSystem`'s `IImeHost` implementation stops talking to its own window and forwards to the
coordinator instead: `SetImeEnabled(true/false)` adds/removes this window's entry, `SetImeCaretRect`
updates it, `ResetComposition` resets the *focused* window's composition (that is where it lives).
**`BaseTextInputKbmController` does not change** — its four existing call sites are already right;
they were just being answered by the wrong window.

Then `ImeCoordinator.Update()`, called each tick from `GuiApp.HandleTick` after
`_secondaryWindows.Update()`, asserts the invariant:

- **typing target** = `arbiter.TopmostModal()` when it holds focus, else the OS-focused window's own
  input system — the same rule `HandleKeyEvent` already applies, lifted out of it and reused;
- **enabled** = the OS-focused window gets `SetImeEnabled(_editingCarets.ContainsKey(target))`; every
  other window gets it off. Push only on change — it's a native call;
- **caret** = the target's rect, put through the *owning* window's `IWindowCoordinates.ToScreenPoints`
  and then rebased into the focused window's client rect by subtracting its position. Going via
  screen points reuses `WindowCoordinates` and deletes the bespoke Y-flip currently in
  `DesktopInputSystem.SetImeCaretRect`.

The dictionary, rather than a single slot, is what kills the regression the old note feared: with a
commit-box field editing *and* a menu search field editing, both are in the map. The menu's field
removes only its own entry on close, the next tick re-derives the truth — IME still on, caret rect
back on the commit box — and nothing has to remember to turn anything back on. Idempotent and
self-healing, which is the whole point.

macOS needs no special case. A borderless popup there *does* take key status, so the focused window
*is* the popup; the same derivation enables the IME on it and dispatches locally.

### What landed

Built as designed above. `ImeCoordinator` (`ZGF.Gui.Desktop/Input/`) holds the registered windows
and the editing-caret dictionary, and `GuiApp.HandleTick` calls `Update()` **last** — after
`_contextMenuManager.Update()`, not just after `_secondaryWindows.Update()`, so a menu the tick's own
input closed is already gone when the IME state is re-derived. `BaseTextInputKbmController` is
unchanged, as predicted.

Two things the design note did not anticipate, both found while building:

- **`IImeHost` and the native switches had to be split into two interfaces.** `DesktopInputSystem`
  now implements both: `IImeHost` is what a *field* asks for ("I am editing, here is my caret") and
  forwards to the coordinator; the new `IImeWindow` is what the coordinator *does* to a window
  (`SetImeMode` / `SetImeCursorRect` / `ResetImeComposition` / `CanvasToScreen`). Collapsing them
  into one interface collides on `SetImeEnabled` — the field's request and the native switch are the
  same signature and no longer the same thing, which is the entire point of the phase.
- **`InputSystem.Reset()` drops the focused component without raising `OnFocusLost`.** A popup's
  search field therefore never ends its own edit session when the menu closes, so it would sit in the
  editing set forever and a pooled, hidden popup would keep asserting the IME. `DesktopInputSystem.Reset()`
  now clears its own entry. Nothing about this is visible from the field's side.

The bespoke Y-flip in `SetImeCaretRect` is gone: the caret goes through `WindowCoordinates.ToScreenPoints`
and the composing window rebases it by subtracting its own position. The "this branch is currently
unreachable" comment in `HandlePreeditEvent` is gone too — it is live on Windows now. The three
forwarding branches (`HandleKeyEvent` / `HandleTextEvent` / `HandlePreeditEvent`) now share one
`TypingMenu()` helper reading `ImeCoordinator.FocusedModal()`, so key, character and composition
routing cannot drift apart from each other or from the IME mode.

`ImeCoordinatorTests` (10 tests, fake windows — `GuiTestHarness` is single-window and cannot reach
the routing) covers the four states that matter plus the macOS shape (a popup that *does* hold OS
focus composes against itself, no platform branch) and reset routing. **The tests were mutation-checked:**
reverting the rule to "compose on the window the field lives in" fails 7 of the 10, including the
central one. The three survivors are exactly the cases where the target *is* the focused window, so
they cannot distinguish the two rules — that is correct, not a gap.

### Verified on a real IME — and what it overturned

Typed with Microsoft Pinyin on Windows 11: the commit box, the Review window's base-branch picker,
and the repo picker all compose 你好, with the preedit rendered inline and the OS candidate window on
the caret. The routing works exactly as designed — the log shows `composing=w1 target=w2` with the
preedit forwarded `→ menu`: the *host* window composes for a field living in the popup.

Getting there overturned two things this document asserted as fact:

1. **A borderless popup DOES take OS keyboard focus on Windows.** The premise above — "`WS_EX_NOACTIVATE`,
   so keys land on the host and must be forwarded" — is false. Clicking a menu's search box makes the
   popup the focused window (`glfwGetWindowAttrib(FOCUSED)` is true and `WM_CHAR` arrives on the
   popup's own callback, unforwarded). The coordinator handles this without a special case, because
   it derives the composing window from focus rather than assuming; but the *reason* Phase 6 was
   needed is not the reason stated here.
2. **Turning `GLFW_IME` off is destructive — but the diagnosis attached to that was wrong.**
   The observation held: `glfwSetInputMode(GLFW_IME, 0)` degraded Pinyin to alphanumeric passthrough
   **for the whole process**, so a commit box that composed a moment earlier started typing a literal
   `nihao`. Latent because the disable used to fire rarely; Phase 6's coordinator toggles on every
   window-focus change, which made it fire every time and read as a Phase 6 regression.

   What was wrong was the conclusion that the call "detaches the window's IME context". `GLFW_IME` is
   the IME's **conversion mode** — the user's own Chinese-vs-alphanumeric toggle — so a process-wide,
   user-visible change is what that call *does*, not a bug in it. We were reaching for the wrong API
   and reading its correct behaviour as breakage.

**Superseded — this compromise is no longer in effect.** It was resolved by
`docs/plans/ime-text-input-focus.md`, which switched to `glfwSetTextInputFocus`, the separate API the
same IM-support patch provides for exactly this: it gates whether the IME may consume the window's
keystrokes, and touches the conversion mode not at all. It works on all four platforms via each
platform's own mechanism (`ImmAssociateContext` on Win32, `XSetICFocus`/`XUnsetICFocus` on X11,
`zwp_text_input_v3` on Wayland, skipping `interpretKeyEvents:` on Cocoa).

Both directions are now forwarded, so **the IME is off outside a text field** and bare-letter
shortcuts survive a CJK input method. The cost recorded here — letters swallowed into a composition
instead of reaching a keybinding — is paid off, and the open item is closed. Reaching it required
building the GLFW natives ourselves, since no LWJGL release exports the function; that is the "not
done: building the natives ourselves in CI" debt from this document's own shortcut list, and it is
now done.

## Phase 7 — atlas capacity

`framework/ZGF.Fonts/GlyphAtlas.cs` is a single 2048×2048 skyline-packed atlas with **no eviction**:
`TryReserve` simply fails when full and the glyph is silently dropped. Thousands of ideographs across
several sizes and weights can plausibly exhaust it. Needs growth or eviction, plus a visible signal
rather than silent tofu.

## Phase 8 — in-app candidate window (Windows-only; spike first, and it may not survive it)

The patch exposes `GLFW_MANAGE_PREEDIT_CANDIDATE` + `glfwSetPreeditCandidateCallback`, which let us
render the candidate list ourselves instead of accepting the OS panel. Four facts, read off the
header and the Win32 backend at the commit our natives are actually built from (`744c3de`, upstream
`b35641f`), change what this phase is:

1. **It is Windows-only.** `glfw3.h` on both `glfwSetPreeditCandidateCallback` and
   `glfwGetPreeditCandidate`: *"@macos @x11 @wayland Don't support this function. The callback is not
   called."* And on the hint, in `intro.md`: *"@win32 Only the OS currently supports this hint."* All
   four vendored natives *export* the symbols — the macOS export trie has them — but only the Win32
   backend implements them. **So the stated motivation for this phase, a consistent cross-platform
   look, is backwards**: it would give Windows our list and leave macOS and Linux on the OS panel.
   The remaining honest motivations are a themed candidate list on Windows and control over its
   placement. Decide whether that's worth it before writing any code.
2. **It is an init hint, not an input mode.** `GLFW_MANAGE_PREEDIT_CANDIDATE` is `0x00050004`, in the
   init-hint range: `glfwInitHint` before `glfwInit`, process-wide, for the app's lifetime. There is
   no per-window or per-field toggle. `Glfw.NET` already binds `glfwInitHint`; the call site would be
   `OpenGlApp.cs:20` / `MetalApp.cs:31`.
3. **Turning it on hides the OS candidate window.** In `win32_window.c`'s `WM_IME_SETCONTEXT` the
   hint is precisely what strips `ISC_SHOWUICANDIDATEWINDOW`. We do not render *alongside* the OS
   panel, we *replace* it — so a popup that is wrong, empty or mispositioned leaves the user with a
   preedit and no candidates, i.e. unable to type CJK at all. Same class of destructive, silent
   failure as the leaked Enter key in Phase 4.
4. **The list comes from IMM32, and the modern Windows IMEs may not feed it.** `getImmCandidates`
   calls `ImmGetCandidateListW` off `IMN_OPENCANDIDATE` / `IMN_CHANGECANDIDATE`. Microsoft Pinyin and
   MS-IME Japanese on Windows 11 are TSF text services that draw candidates from their own UI
   process; the IMM32 compatibility layer reliably delivers *composition* (which is why the Phase 0
   spike saw preedit) but is well known to be unreliable for *candidates*.

### Phase 8.0 — the spike that can kill the phase (do this first)

Everything below is conditional on it, and it is half a day. Add `Hint.ManagePreeditCandidate =
0x00050004`, call `Glfw.InitHint` before `Glfw.Init()`, P/Invoke `glfwSetPreeditCandidateCallback` +
`glfwGetPreeditCandidate` in `GlfwIme.cs`, log every callback to a file — the shape of the Phase 0
spike. Then type with **Microsoft Pinyin** and **MS-IME Japanese** on Windows 11 and answer:

1. Does the callback fire with a non-zero `candidates_count`?
2. Does `glfwGetPreeditCandidate` return real text for each index in the page?
3. Did the OS candidate panel actually disappear?

If 1 or 2 fails, stop and record the negative result here — that *is* the deliverable. If 3 fails we
would be drawing a second list under the OS's, which is worse than doing nothing. Try one third-party
IME (Google Japanese Input) too, so a failure can be attributed to MS rather than to the patch.

### Phase 8.1+ — only if the spike passes

- **Binding.** `GlfwIme.cs` gains the two P/Invokes and a `CandidatesSupported` probe that checks the
  export **and** the OS — the symbol exists on macOS but never calls back, so an export probe alone
  would lie. `PreeditCandidates` joins `PreeditText` in `ZGF.Desktop/Input/`: the visible page as
  UTF-16 strings, selected index, page start/size, total count. Reuse `PreeditText`'s UTF-32→UTF-16
  conversion — candidates arrive as code points and carry the same astral-plane hazard.
- **Event.** `IWindow.OnPreeditCandidates`; `GlfwImeBridge` registers the callback and marshals it,
  holding the delegate alive as it already does for preedit.
- **Popup.** A `CandidatePopupService` shaped like `PopupTooltipService`: `MousePassThrough = true`,
  never modal, never takes capture, never dismisses the field. That is forced, not stylistic — the
  API is read-only. There is no `glfwSelectPreeditCandidate`, so **the user cannot click a
  candidate**; every selection still goes through the IME's keys. Microsoft Pinyin users click
  candidates today, so this phase *takes a capability away* on the one platform it ships to. Anchor
  it on the caret rect the `ImeCoordinator` from Phase 6 already computes, with `PopupRequest.Place`'s
  flip fallback for a caret near the bottom of the screen. Hide on empty candidates, on empty
  preedit, and on the `ResetComposition` path — the popup must not outlive the field, the same
  invariant Phase 3 set for `_preedit`.
- **Widget.** A `CandidateListView`: one row per candidate in the visible page, numbered 1–9
  positionally, selected row on the existing `RowSelectionStyles` token rather than a new colour.
- **Kill switch.** The hint stays **opt-in and defaulted off** (setting or env var) until real users
  have typed against it. The failure mode — no candidates at all — is invisible to every test we can
  write and total for the user who hits it.
- **Tests.** `GuiTestHarness.SendCandidates` mirroring `SendComposition`, driving a fake popup factory
  to assert show/update/hide and selection tracking; plus a `GlfwImeNativeTests`-style export
  assertion. Be honest that these pin *our* contract, not IMM32's: the thing that can actually break
  is unreachable from a headless test, which is why the kill switch exists.

## Testing

`GuiTestHarness.SendComposition` / `EndComposition` / `Compose` inject synthetic composition events,
which is the only way to test this: the existing `Typist` uses `SendInput` with `KEYEVENTF_UNICODE`,
posting `WM_CHAR` directly and **bypassing the IME entirely**.

`GlfwImeNativeTests` asserts `GlfwIme.IsSupported` against the actually-bundled native, which is the
one thing the synthetic tests cannot cover: that the binary on disk is the patched build and that the
runtime probe resolves against it. It is not ceremony — it caught the vendored Windows DLL being
silently reverted to stock mid-implementation, which would have left the whole feature inert with
every other test still green.

**Still outstanding — nobody has typed CJK into this yet.** The tests are synthetic, and they pin the
contract the framework implements, not what a real IME does. The manual matrix is unrun: Japanese
(romaji→kana→kanji, the most demanding), Simplified Chinese (pinyin), Korean (jamo, composes
in-place), per platform, plus a Latin/Cyrillic regression check.

Worth watching for on first real use, since the synthetic tests cannot reach them: whether the OS
candidate window actually lands on the caret (the coordinate conversion has never been checked
against a real IME), and the true ordering of commit-chars versus the end-of-composition event — the
implementation deliberately tolerates either order rather than assuming one.

## Out of scope

- **Writing an IME engine** (see rejected alternatives).
- **Color emoji** — the atlas is single-channel 8-bit gray; `TryGetGlyph` rejects any non-`GRAY`
  pixel mode. Orthogonal, already noted in `docs/plans/done/localization.md`.
- **Locale-aware Han variant selection** — today the earliest-registered fallback wins for shared
  ideographs (existing, documented caveat in `SystemFonts.cs`).
