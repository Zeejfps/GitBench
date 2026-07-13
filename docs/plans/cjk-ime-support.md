# CJK Text Input — IME composition support

> GitBench can *render* Chinese, Japanese and Korean today, but it cannot *type* them. Latin and
> Cyrillic work because they are one-keystroke-one-character, which is all GLFW's char callback
> delivers. CJK is not: you type romaji/pinyin/jamo, an IME shows a **composition (preedit)** string
> and a candidate list, and only on commit does real text exist. There is no IME layer anywhere in
> the codebase. This plan adds one. Phases are ordered outside-in — each lands something runnable.

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

## Phase 1 — native supply

> **Current working-tree state (from the spike, not yet production-ready):**
> `framework/Glfw.NET/Native/win-x64/glfw3.dll` is **LWJGL's build, borrowed** — fine for the spike,
> not something to ship. The `osx-*` and `linux-x64` natives are **still stock**, so `IsSupported()`
> returns false there and the IME is inert (degrades gracefully — it does not crash). The spike's
> `ImeSpikeLog` and the `OpenGlWindow` preedit hook are temporary and must be replaced by Phase 2's
> event stream.

Own the patched native properly instead of borrowing LWJGL's build.

- Build `clear-code/glfw@im-support` (pinned commit) for `win-x64` (MSVC), `osx` universal
  (`-DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"`), `linux-x64` (oldest supported Ubuntu, for glibc).
  GLFW is CMake with zero dependencies; the builds are ~2 minutes each. This is new CI surface —
  today the natives are *downloaded*, not compiled.
- Unifies the Windows/macOS version skew (3.3.7 vs 3.4.0) as a side effect.
- **Linux caveat:** `NativeLibraryResolver` prefers the distro's `libglfw.so.3`, which is unpatched.
  The vendored `.so` must win, or Linux silently keeps no IME (`IsSupported()` returns false).
- Regression pass on the version bump — see spike exit criteria.

## Phase 2 — composition event stream (framework)

Mirror the existing `TextInputEvent` path, which is the template:
`GLFW callback → IWindow.OnPreedit → DesktopInputSystem → InputSystem → controller`.

- `IWindow`: add `event Action<PreeditText>? OnPreedit`, implemented in **both** `OpenGlWindow`
  (Windows/Linux) and `MetalWindow` (macOS).
- `PreeditText`: the composition string (GLFW hands over **UTF-32 code points**), the caret offset,
  and the attributed blocks (`blockSizes` + `focusedBlock`) that carry the clause underlines.
- New `CompositionEvent` alongside `TextInputEvent`, routed through `InputSystem` to the focused
  component. **Committed text keeps arriving on the existing char path** — preedit is purely
  additive, so Latin typing is untouched (confirmed in Phase 0).
- **Commit vs. cancel is not distinguishable from the end event** (Phase 0 finding #2). Treat an
  empty preedit as "composition over, drop the preedit", and let the char callback deliver the
  committed text as it already does. Do **not** try to insert preedit text on end — that would
  double-insert on commit and wrongly insert on cancel.
- Enable the IME **only while a text field is focused** (`SetInputMode(Ime, 1)` on `StartEditing`,
  `0` on `StopEditing`). Otherwise a Japanese IME would start composing when `j`/`k` is pressed to
  navigate the commit list.

## Phase 3 — preedit in `TextInputView`

`framework/ZGF.Gui.Desktop/Components/TextInput/TextInputView.cs`.

- Hold the composition string **separately from `_buffer`**, displayed at the caret, and **do not
  fire `TextValue` change notifications** for it — otherwise the commit VM sees half-typed pinyin.
- Render clause underlines from the attributed blocks; highlight `focusedBlock`.
- Feed the caret rect back so the candidate window follows it: `GetCaretRect()` (already exists) →
  window coordinates → `glfwSetPreeditCursorRectangle`.
- `glfwResetPreeditText` on blur / Escape / focus loss, so a composition never survives its field.

## Phase 4 — key gating while composing

**Revised after Phase 0.** The destructive bug I predicted — Space/Enter selecting a candidate *and*
leaking through to the app (typing a space, submitting the commit) — **did not reproduce**. The
patched GLFW appears to already suppress keys the IME consumed, which stock GLFW does not do. That is
an unadvertised bonus of the patch.

Keep the work anyway, downgraded from "build the gate" to "verify and guard":

- An `IsComposing` gate in `BaseTextInputKbmController` and `GitBench/App/AppKeybindController.cs`,
  as defence in depth: while a composition is live, Enter/Escape/arrows/Space belong to the IME.
- **Regression tests are the real deliverable here** — the failure mode (a commit submitted
  mid-composition) is destructive and silent, and we are relying on an unmerged upstream patch to
  prevent it. Cover it in `GuiTestHarness` so a future GLFW bump can't quietly regress it.
- Ordering works in our favour if a gate is needed: composition begins on an *earlier* keystroke, so
  `IsComposing` is already true by the time the Enter key event arrives.

## Phase 5 — editing-model correctness (independent of IME; do it regardless)

Real bugs found while reading, all in `TextInputView` unless noted:

- `MoveCaretLeft`/`MoveCaretRight` do a raw `_caretIndex--`/`++` on a UTF-16 `char[]`, and `Delete()`
  removes exactly one code unit. **Arrow keys land between surrogate halves and backspace over an
  astral character (emoji, CJK Ext-B) orphans a surrogate.** There is a `TypesAstralPlaneChars` test
  for insertion but none for navigating or deleting.
- `TextInputView` **does not use `TextWrapper`** — it wraps per-`char` with no CJK rules, so all the
  kinsoku work does not apply to the multi-line commit description field.
- `GitBench/Controls/TextMeasure.cs TruncateToFit` slices without the surrogate guard that
  `TextView.Ellipsize` correctly has.
- `IsWordChar` is `char.IsLetterOrDigit`, so a whole CJK run is one "word" for Ctrl+Arrow.

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

Because the composition stream is a framework-level event, `GuiTestHarness` can inject synthetic
composition events and cover preedit/commit/cancel and the key-gating headlessly. This is the only
way to test it: the existing `Typist` uses `SendInput` with `KEYEVENTF_UNICODE`, which posts
`WM_CHAR` directly and **bypasses the IME entirely**.

Manual matrix: Japanese (romaji→kana→kanji, the most demanding), Simplified Chinese (pinyin),
Korean (jamo, composes in-place). Per platform. Plus Latin/Cyrillic regression — preedit must not
disturb the existing char path.

## Out of scope

- **Writing an IME engine** (see rejected alternatives).
- **Color emoji** — the atlas is single-channel 8-bit gray; `TryGetGlyph` rejects any non-`GRAY`
  pixel mode. Orthogonal, already noted in `docs/plans/done/localization.md`.
- **Locale-aware Han variant selection** — today the earliest-registered fallback wins for shared
  ideographs (existing, documented caveat in `SystemFonts.cs`).
