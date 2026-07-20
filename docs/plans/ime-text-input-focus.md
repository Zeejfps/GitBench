# IME text-input focus — scope the IME to text fields on all three platforms

**Status:** proposed. Supersedes the "IME stays on outside a text field" compromise recorded in
`cjk-ime-support.md` (Phase 6) and in `GlfwImeBridge.SetEnabled`'s doc comment.

## The bug

With a CJK input method active, every bare-letter keyboard shortcut is dead. Press `T` over the
commit list and no tag dialog opens: the IME swallows the key into a composition before the app ever
sees it. Same for `B`, `C`, `V`, `F`, `Space`, and `J`/`K` in the review window.

The IME is enabled for the whole window at all times, so it competes with the app for every letter,
everywhere — not just in the commit box and the search field where it belongs.

## Root cause

`GlfwImeBridge.SetEnabled` only ever turns the IME **on**:

```csharp
public void SetEnabled(bool enabled)
{
    if (_preeditCallback == null || !enabled) return;
    Glfw.SetInputMode(_window, GlfwIme.Ime, 1);
}
```

Disabling was removed because `glfwSetInputMode(GLFW_IME, 0)` degraded Microsoft Pinyin to
alphanumeric passthrough process-wide. That observation was correct. The diagnosis attached to it —
that the call "detaches the window's IME context" — was not.

`GLFW_IME` maps to `_glfw.platform.setIMEStatus`, which is the IME's **conversion mode**: the
Chinese-vs-alphanumeric toggle the input method itself owns. On macOS it goes further and selects the
keyboard input source by locale. Turning that off is a global, user-visible state change, and the
process-wide degradation is what that call *does* — not a bug in it.

The patch carries a second, entirely separate API for the thing we actually want.

## The mechanism upstream already provides

`glfwSetTextInputFocus(window, focused)` (`src/input.c:1043`), added by the same IM-support patch
(`glfw/glfw#2130`, head `clear-code/glfw@im-support`):

```c
GLFWAPI void glfwSetTextInputFocus(GLFWwindow* handle, int focused)
{
    _GLFW_REQUIRE_INIT();
    _GLFWwindow* window = (_GLFWwindow*) handle;
    assert(window != NULL);
    focused = focused ? GLFW_TRUE : GLFW_FALSE;
    window->textInputFocusInitialized = GLFW_TRUE;
    window->textInputFocus = focused;
    _glfw.platform.setTextInputFocus(window, focused);
}
```

Every platform gates on one helper:

```c
static GLFWbool textInputFocusDisabled(_GLFWwindow* window)
{
    return window->textInputFocusInitialized && !window->textInputFocus;
}
```

`textInputFocusInitialized` is sticky-once-set, so **until the API is called even once, behaviour is
unchanged**. That makes adoption opt-in and the rollback trivial: stop calling it.

It is implemented properly on all four platforms, each using that platform's canonical mechanism:

| platform | mechanism | source |
|---|---|---|
| Win32 | `ImmAssociateContext(hwnd, NULL)`, saving the `HIMC`; re-associate to restore | `win32_window.c:2913` |
| X11 | `XSetICFocus` / `XUnsetICFocus` on the window's `XIC` | `x11_window.c:3435` |
| Wayland | `zwp_text_input_v3` `enable`/`disable` + `commit` (v1 fallback) | `wl_window.c:3991` |
| Cocoa | conditionally skips `interpretKeyEvents:` in `-keyDown:` | `cocoa_window.m:568` |

### Correcting the record on macOS

Earlier in this investigation I read `_glfwSetTextInputFocusCocoa` —

```c
void _glfwSetTextInputFocusCocoa(_GLFWwindow* window, GLFWbool focused)
{
    if (!focused)
        _glfwResetPreeditTextCocoa(window);
}
```

— and concluded macOS was unfixable through this API. **That was wrong.** The platform hook is thin
because the gate lives in the event path, not in a state-setter. `-keyDown:` reads the flag directly:

```objc
- (void)keyDown:(NSEvent *)event
{
    const int key = translateKey([event keyCode]);
    const int mods = translateFlags([event modifierFlags]);

    if (![self hasMarkedText])
        _glfwInputKey(window, key, [event keyCode], GLFW_PRESS, mods);

    if (!window->textInputFocusInitialized || window->textInputFocus)
        [self interpretKeyEvents:@[event]];
    else
    {
        NSString* characters = [event characters];
        if (characters)
            [self insertText:characters replacementRange:[self selectedRange]];
    }
}
```

Not calling `interpretKeyEvents:` *is* the AppKit way to disable the IME — there is no "turn the IME
off" call on macOS; the design is that you don't route the event to it. This is what SDL3 does
(`Cocoa_HandleKeyEvent` gates on `SDL_TextInputActive`) and the same idea Qt uses via
`[self.inputContext handleEvent:]` gated on the focus object's input hints. So macOS needs no native
code from us.

Note the `else` branch still delivers characters via `insertText:`, so `GLFW_CHAR` keeps firing for
plain typing when text-input focus is off. That is harmless here — the `KeyClaim` model already
decides whether a character is allowed to type, and a shortcut key claims itself as a command.

## Why we cannot call it today

The vendored natives predate it.

- `framework/Glfw.NET/Native/` holds four LWJGL 3.3.4 binaries, built from GLFW commit
  `b35641f4a3c62aa86a0b3c983d163bc0fe36026d`.
- The PE export table of `win-x64/glfw3.dll` and the ELF dynamic symbols of `linux-x64/libglfw.so.3`
  contain the three preedit entry points and **no** `glfwSetTextInputFocus`.
- The macOS dylibs compress export names into a Mach-O prefix trie, so a string search cannot answer
  the question either way — the trie must be parsed (Phase 0).
- **LWJGL 3.4.2-snapshot still does not expose it.** Their binding lists
  `glfwSetPreeditCallback`, `glfwSetIMEStatusCallback`, `glfwSetPreeditCandidateCallback`,
  `glfwGetPreeditCursorRectangle`, `glfwSetPreeditCursorRectangle`, `glfwGetPreeditCandidate` and
  `glfwResetPreeditText` — but not `glfwSetTextInputFocus`.

So **upgrading LWJGL cannot deliver this, now or later.** Their fork is pinned to an im-support
commit older than the function. The only route to the proper fix is building the natives ourselves —
which `cjk-ime-support.md` already lists as the outstanding shortcut:

> **Not done:** building the natives ourselves in CI. Vendoring LWJGL's build is the shortcut taken here.

This plan is where that debt comes due. Everything else here is small; Phase 1 is the work.

## Rejected alternatives

**Port `_glfwSetTextInputFocusWin32` ourselves via imm32 P/Invoke.** Tempting — `IWindow.NativeHandle`
is already the HWND, and it is ~15 lines. Rejected: it fixes one platform of three, leaves macOS and
Linux broken, and commits us to maintaining a parallel implementation of a function that already
exists upstream and that we would then have to keep in sync. It also spreads IME state across two
owners (GLFW's `textInputFocus` and ours), which is how the current confusion started.

**Swizzle `GLFWContentView` on macOS.** Already rejected in `cjk-ime-support.md` on the grounds that
it fixes macOS only. Now additionally unnecessary: upstream's `keyDown:` gate does the job.

**Keep toggling `GLFW_IME`.** Wrong API, as above. It should not be called at all after this work.

**Do nothing and document it.** This is the status quo, and the cost is that CJK users have no
single-key shortcuts anywhere in the app. Worth stating plainly so the choice is explicit.

## Plan

### Phase 0 — verify the ground truth

1. Parse the Mach-O export trie of both macOS dylibs and the PE/ELF tables of the other two; record
   for each RID whether `glfwSetTextInputFocus` is present. Expected: absent everywhere. This is the
   premise of Phase 1 and must not rest on a string search.
2. Reproduce the symptom on macOS and on Linux/X11, not just Windows. The Cocoa and X11 paths differ
   enough that "shortcuts are dead in CJK mode" should be confirmed, not assumed.
3. Record the current behaviour of `GLFW_IME` on each platform so Phase 4's removal can be judged
   against it.

**Exit:** a written per-RID symbol table and a reproduction on all three platforms.

### Phase 1 — build the natives

Build GLFW from `clear-code/glfw@im-support` for `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.

- **Pin the commit.** Record the exact SHA in `Native/README.md` alongside the existing LWJGL
  provenance. The branch is a moving PR head; an unpinned build is unreproducible.
- **CI, not a workstation.** Three GitHub Actions runners (windows/ubuntu/macos). A hand-built binary
  nobody can reproduce is how the current situation arose.
- **Keep the `osx-x64` slot universal.** `lipo` the two macOS slices into one fat binary — the csproj's
  RID-less macOS fallback bundles the `osx-x64` file, and a thin x86_64 dylib fails to load under an
  arm64 runtime. This constraint is already documented and is easy to regress.
- **Match the current build's surface.** The existing natives dynamically link `imm32` on Windows.
  Diff the exported symbol set old-vs-new and review anything that disappears.
- **Checksums.** Record SHA-256 per file so a silent revert is detectable — the existing
  `GlfwImeNativeTests` was written because the Windows DLL was once reverted to stock mid-implementation.

**Risks:** X11/Wayland build dependencies on the Linux runner; macOS codesigning/notarization for the
dylibs; the PR head may have drifted from `b35641f` in ways that affect the preedit path we already
depend on — hence the regression pass in Phase 7.

**Exit:** four natives, reproducible from a pinned SHA by a CI job, each exporting
`glfwSetTextInputFocus`, with checksums recorded.

### Phase 2 — bind it

In `framework/Glfw.NET/GlfwIme.cs`:

```csharp
[DllImport(Glfw.LIBRARY, EntryPoint = "glfwSetTextInputFocus", CallingConvention = CallingConvention.Cdecl)]
public static extern void SetTextInputFocus(Window window, int focused);
```

Add a **separate** capability probe. `IsSupported` currently probes `glfwSetPreeditCallback`; text-input
focus is a strictly newer capability and must not be gated on the older symbol, or a half-upgraded
native throws `EntryPointNotFoundException` at runtime instead of degrading:

```csharp
public static bool IsTextInputFocusSupported => _tif ??= ProbeExport("glfwSetTextInputFocus");
```

Extend `GlfwImeNativeTests` to assert it against the on-disk binary, matching the existing guard.

### Phase 3 — wire it through the seam

The abstraction already models this. `ImeCoordinator` tracks which window holds an editing field
(`_editingCarets`, `_composing`) and calls `IImeWindow.SetImeMode(bool)` only on change. That is
exactly the signal `glfwSetTextInputFocus` wants; it has simply been landing on the wrong native call.

1. `GlfwImeBridge.SetEnabled(bool)` → forward **both** directions to `GlfwIme.SetTextInputFocus`,
   guarded by `IsTextInputFocusSupported`. No `IsOSPlatform` switch: the platform difference lives in
   GLFW now, which is the point.
2. **Arm the flag at startup.** `textInputFocusInitialized` is sticky, and until it is set GLFW keeps
   legacy always-on behaviour. Call `SetTextInputFocus(window, false)` once when each window is
   created, or the fix silently does nothing until the first field is blurred.
3. **Every top-level window, not just the main one.** Secondary windows (review, diff pop-out) and
   popup windows each own a GLFW window and a `DesktopInputSystem`. A popup that takes keyboard focus
   and is never armed keeps swallowing letters. `PopupWindowFactory` pools popups, so arming must
   happen on creation *and* survive `DesktopInputSystem.Reset()`.
4. Rename to say what it means — `SetTextInputFocus` through `IWindow`, `IImeWindow` and
   `GlfwImeBridge`. `SetImeEnabled`/`SetImeMode` describe the API we are abandoning. `IImeWindow` has
   one test fake (`ImeCoordinatorTests.FakeWindow`) to update.

No change to `IImeHost` or to `BaseTextInputKbmController`: fields already report editing start/stop.

### Phase 4 — remove the workaround

Delete the `Glfw.SetInputMode(_window, GlfwIme.Ime, 1)` call and the `GlfwIme.Ime` constant. Nothing
should touch IME *conversion mode* — it is the user's setting, not ours.

Rewrite the `GlfwImeBridge` doc comment. As written it records a diagnosis we have now disproved, and
it is the reason the compromise reads as permanent. Correct `cjk-ime-support.md` Phase 6 the same way
and close its open item.

### Phase 5 — per-platform hardening

Pitfalls the upstream implementations handle and that our call sites must not defeat:

- **Windows.** Reset the preedit *before* disassociating, or a half-composed string is orphaned in the
  UI. The saved-`HIMC` field doubles as the disabled flag, so both directions are idempotent — do not
  add our own duplicate call that could overwrite it with `NULL`. The association must be restored
  before `DestroyWindow`; verify pooled popup teardown does not leak it.
- **X11.** `XUnsetICFocus` does not change the IC's focus window, and IM events can still arrive —
  keep the preedit callbacks defensive rather than assuming silence. Over-the-spot style bails out of
  IME management entirely upstream, so verify which style we get.
- **Wayland.** `enable` resets all state to initial values, so cursor rectangle and content type must
  be re-sent after every re-enable. Only one text input may be enabled per seat. In practice we pin
  Linux to X11 via `GLFW_PLATFORM`, so this is XWayland — but the constraint should be recorded, not
  discovered later.
- **macOS.** If the candidate window or input-source indicator still appears with focus off, the
  follow-up is overriding `-inputContext` to return `nil` (what Chromium does). That needs a patch to
  `cocoa_window.m`, so treat it as contingent on observation, not as planned work.

### Phase 6 — tests

The existing harness dispatches through `InputSystem` and never reaches GLFW, so it can cover the
routing but not the native gate.

- `ImeCoordinatorTests` — `FakeWindow` asserts `SetTextInputFocus(true)` on field focus,
  `false` on blur, exactly once per transition, and `false` at window creation (the arming call).
- Coverage for the popup/secondary-window case, which is where this is most likely to be missed.
- `GlfwImeNativeTests` — assert the new export, per Phase 2.
- The native gate itself is not unit-testable from C#; it belongs to Phase 7.

### Phase 7 — verification

Per platform, with a real IME (Microsoft Pinyin / macOS Pinyin / ibus or fcitx):

1. Bare-letter shortcuts work with the IME active and no field focused — `T`, `B`, `C`, `V`, `F`,
   `Space`, and `J`/`K` in the review window.
2. Composition still works in the commit box and the search field: preedit renders, candidate window
   sits on the caret, commit inserts the right text.
3. Enter picks a candidate mid-composition and does **not** submit the dialog.
4. Blur mid-composition discards rather than commits, and does not leave a stale preedit drawn.
5. Focus a field, blur, re-focus — composition still works (the regression that produced the original
   workaround; this is the specific thing to hammer).
6. Popup and secondary windows behave like the main window on all of the above.
7. Latin and Cyrillic layouts unaffected — the control that proves the gate is not over-broad.
8. The 3.3.7/3.4.0 → new-build GLFW regression pass per platform, since Phase 1 moves the natives.

## Relationship to the `KeyClaim` work

Independent, and both are needed. `KeyClaim` decides whether a character that the OS produced is
allowed to type; this plan decides whether the OS produces one at all. The `T`-types-into-the-tag-dialog
bug is a Latin-layout bug that `KeyClaim` fixes and this plan does not touch. Conversely no claim model
can recover a keystroke the IME consumed before the app saw it.

## Open questions

1. Does the symptom actually reproduce on macOS and Linux? Phase 0. If it does not, the priority of
   those platforms drops sharply, though the fix stays the same shape.
2. How far has `im-support` drifted from `b35641f`? A large drift raises the regression risk in the
   preedit path we already ship and depend on.
3. Is there appetite for upstreaming? The gap is that LWJGL's fork predates the function; a nudge on
   [LWJGL#946](https://github.com/LWJGL/lwjgl3/issues/946) may be cheaper long-term than owning a
   native build — but it cannot be scheduled, so this plan does not depend on it.
