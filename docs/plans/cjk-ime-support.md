# CJK Text Input — IME composition support

> GitBench can *render* Chinese, Japanese and Korean today, but it cannot *type* them. Latin and
> Cyrillic work because they are one-keystroke-one-character, which is all GLFW's char callback
> delivers. CJK is not: you type romaji/pinyin/jamo, an IME shows a **composition (preedit)** string
> and a candidate list, and only on commit does real text exist. There is no IME layer anywhere in
> the codebase. This plan adds one. Phases are ordered outside-in — each lands something runnable.

> **Status: phases 1–5 implemented.** The composition path is built and covered headlessly
> (`TextInputImeTests`, `TextInputUnicodeTests`); the full suite is green. Still open, in order:
> **manual verification on a real IME** (nothing here has been typed into by a human yet — the tests
> are synthetic), the **3.3.7 → 3.5.0 GLFW regression pass** per platform, building the natives
> ourselves in CI instead of vendoring LWJGL's, and phases 6–7.

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
- **Not done:** building the natives ourselves in CI. Vendoring LWJGL's build is the shortcut taken
  here; owning the build (pinned `clear-code/glfw@im-support`, CMake, ~2 min per RID) remains open.
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

## Phase 6 — atlas capacity

`framework/ZGF.Fonts/GlyphAtlas.cs` is a single 2048×2048 skyline-packed atlas with **no eviction**:
`TryReserve` simply fails when full and the glyph is silently dropped. Thousands of ideographs across
several sizes and weights can plausibly exhaust it. Needs growth or eviction, plus a visible signal
rather than silent tofu.

## Phase 7 — in-app candidate window (optional)

The patch exposes `GLFW_MANAGE_PREEDIT_CANDIDATE` + `glfwSetPreeditCandidateCallback`, which lets us
render candidates ourselves instead of accepting the OS panel. We already have keyboard-capable
popups from the searchable-context-menu work. Consistent cross-platform look, but a nice-to-have —
not part of the first cut.

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
