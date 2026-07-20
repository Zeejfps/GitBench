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

1. Dump the export table of each of the four natives and record for each RID whether
   `glfwSetTextInputFocus` is present. Expected: absent everywhere. This must not rest on a string
   search — but it needs no bespoke tooling either: `dumpbin /exports` (PE), `nm -D --defined-only`
   (ELF) and `nm -gU` (Mach-O) all read the real table, and `nm` walks the export trie itself, so the
   macOS case is a one-liner rather than an investigation.
2. Reproduce the symptom on macOS and on Linux/X11, not just Windows. The Cocoa and X11 paths differ
   enough that "shortcuts are dead in CJK mode" should be confirmed, not assumed.
3. Record the current behaviour of `GLFW_IME` on each platform so Phase 4's removal can be judged
   against it.

**Exit:** a written per-RID symbol table and a reproduction on all three platforms.

### Phase 1 — build the natives

Build GLFW from `clear-code/glfw@im-support` for `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.

GLFW is a small CMake project with no dependencies beyond system libs, so the compilation itself is a
handful of lines per platform. The work is in where the job lives, the flags that decide what the
binary depends on, and the verification that stops a bad native from landing.

#### Where the job lives

**The framework repo, not this one.** `framework/` is a submodule of
`Zeejfps/ENV-Game-Framework`, which is where `Glfw.NET/Native/` lives — and which currently has **no
`.github/workflows` at all**. This is the first workflow that repo will have. GitBench's
`release.yml` consumes the natives; it does not produce them.

**`workflow_dispatch`, emitting artifacts a human then commits.** Not push-triggered — a native bump
is a deliberate act, not a side effect of a commit.

**Keep vendoring the binaries in git.** The alternative considered was publishing a
`ZGF.Glfw.Natives` NuGet runtime pack with a `runtimes/<rid>/native/` layout — no binaries in git,
proper versioning. Rejected: the framework is consumed as a submodule and is not distributed as a
package anywhere, so a feed buys nothing but auth setup and a second version axis to keep in sync
with the submodule pointer. The thing that actually failed last time was provenance, not delivery.
Fix provenance.

#### Per-platform builds

Common to all: `-DBUILD_SHARED_LIBS=ON -DGLFW_BUILD_EXAMPLES=OFF -DGLFW_BUILD_TESTS=OFF
-DGLFW_BUILD_DOCS=OFF`.

- **Windows** (`windows-latest`, MSVC, `-A x64`). Add
  **`-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded`** — static CRT. LWJGL builds this way; a stock MSVC
  build takes a `vcruntime140.dll` dependency the current native does not have, and that failure is
  invisible until someone runs the app on a clean machine. `imm32` linkage comes from GLFW's own
  CMake and needs nothing from us.
- **Linux** — build inside a **`ubuntu:22.04` container step**, not on the bare runner.
  `ubuntu-latest` is 24.04 (glibc 2.39) and its output will not load on anything older. Enable
  **both X11 and Wayland**, matching the current binary's surface: `Native/README.md` documents
  Wayland being present and `Glfw`'s static ctor pinning to X11 via `GLFW_PLATFORM`, so building
  Wayland out would silently change what that pin means. Needs the X11 dev headers plus
  `libwayland-dev` / `wayland-protocols` / `extra-cmake-modules`.
- **macOS** — **no `lipo`.** One `macos-latest` job with
  `-DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"` and a pinned `-DCMAKE_OSX_DEPLOYMENT_TARGET` produces
  the universal dylib directly. Then put **the same universal file in both RID slots**, rather than
  keeping `osx-x64` universal and `osx-arm64` thin. That deletes the entire "thin slice under the
  wrong runtime" failure class instead of documenting it a second time; at that point the csproj's
  two macOS RID conditions are cosmetic and the folders could collapse to a single `osx/`.
  Note that `release.yml` already builds macOS on real Intel (`macos-15-intel`) and ARM
  (`macos-latest`) runners — so the universal requirement is driven only by RID-less local dev
  builds, not by shipping.

#### Verification, in the same workflow

Building is the easy half. These gates are what earn the CI job:

- **Pin the SHA as a workflow input** with a default constant, recorded in `Native/README.md`
  alongside the existing LWJGL provenance. The branch is a moving PR head; an unpinned build is
  unreproducible.
- **Print `git log --oneline b35641f..HEAD` into the job summary.** This answers open question 2 as a
  build artifact rather than as a separate investigation.
- **Assert exports and fail the job otherwise** — `glfwSetTextInputFocus` plus the three preedit
  entry points, via the same `dumpbin` / `nm -D` / `nm -gU` calls as Phase 0.
- **Diff the exported symbol set old-vs-new, failing on anything that disappeared.** This is the
  automated form of "review anything that disappears"; a human eyeballing two symbol dumps is the
  step that gets skipped.
- **Emit SHA-256 per file** into a manifest written to `Native/README.md`.

Then extend `GlfwImeNativeTests` to assert the recorded checksum against the on-disk binary. That is
the real anti-silent-revert guard: the existing `IsSupported` probe catches a *stock* native, but
would not catch a patched-but-different one — and a native bump becoming a deliberate two-file change
is the point, not a cost.

#### Sequencing — this is not a blocking gate

Phase 1 reads as though nothing can proceed until all four natives exist. It is not, because Phase 2's
`IsTextInputFocusSupported` probe degrades **per RID** by design. So:

1. Land the workflow producing artifacts, committing no natives. Verify exports on all four.
2. Bump **`win-x64` alone** and ship Phases 2–4 against it. Linux and macOS keep today's behaviour
   until their natives land.
3. Bump the remaining RIDs.

This turns one large risky step into three small ones, and fixes the bug first on the platform where
it has actually been reproduced.

**Risks:** X11/Wayland build dependencies on the Linux runner; the PR head may have drifted from
`b35641f` in ways that affect the preedit path we already depend on — hence the regression pass in
Phase 7. **Notarization** is the one to record now rather than discover later: ad-hoc signing is fine
while GitBench ships unsigned, but the moment the app is notarized, a bundled dylib needs the same
Developer ID and hardened runtime. That is a `vpk pack` concern, not a build-time one, so it does not
change this phase — it changes the release job.

**Exit:** four natives, reproducible from a pinned SHA by a CI job, each exporting
`glfwSetTextInputFocus`, with checksums recorded and asserted by a test.

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

Two documents go stale the moment Phase 1's macOS decision lands and should be corrected in the same
pass, or they will read as live constraints:

- **`Glfw.NET.csproj`.** Its macOS comment explains selecting by target RID "because building osx-x64
  on an Intel runner still has `IsOSPlatform(OSX)==true`", and that `osx-x64` carries the universal
  dylib. Once *both* slots are universal the two RID conditions select identical bytes and the
  hazard they guard against cannot occur. Either collapse to a single `osx/` folder with one
  condition, or keep the RID layout and rewrite the comment to say the slots are universal by policy
  — but do not leave a comment describing a trap that no longer exists.
- **`Native/README.md`.** The provenance section describes extracting binaries from LWJGL jars, and
  the verification advice says a macOS string search cannot find the export so "just run the app and
  check `GlfwIme.IsSupported()`". Both are superseded: provenance becomes the pinned SHA plus the
  checksum manifest, and verification becomes `nm -gU` (Phase 0) plus the checksum test (Phase 1).
  The warning not to substitute a distro package or upstream release archive stays — it is still
  true, and still the failure mode this file exists to prevent.

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
  `cocoa_window.m` — which Phase 1 makes cheap rather than prohibitive: the natives build from source
  in CI, so this is a patch file applied to the pinned checkout before `cmake`, carried the same way
  the pinned SHA is. Still contingent on observation, not planned work — but if it is needed, the
  cost is a patch file and a symbol-diff re-run, not a change of strategy. Note that a local patch is
  the one thing that makes the build no longer reproducible from a SHA alone, so it must be committed
  next to the workflow and named in `Native/README.md`.

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
