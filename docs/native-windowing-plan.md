# Plan: native windowing with our own abstraction layer

Goal: replace the vendored GLFW (`framework/Glfw.NET`) with per-OS native backends
(Cocoa, Win32, X11) behind a single abstraction, so we ship **zero third-party
native binaries** and own window chrome/lifecycle outright.

## Why this is tractable here

- **The GLFW dependency is a thin waist:** ~30 functions + 8 callbacks
  (create/destroy, poll, should-close, get/set size+pos, framebuffer size, cursor
  pos, show/hide/focus, swap interval/buffers, `MakeContextCurrent`,
  `GetProcAddress`, monitor/video-mode; callbacks for size, framebuffer-size,
  focus, close, key, char, mouse-button, scroll, cursor-enter).
- **macOS is already ~80% native:** `MetalWindow` uses GLFW only to make the
  NSWindow + pump events, then renders via our own Metal/Objc layer
  (`Objc.cs`, `MetalApi.cs`, `CAMetalLayer`).
- **No IME today:** `GlfwInputSystem` sets no char callback; text comes from
  key events via `ZGF.KeyboardModule.GlfwAdapter`. So native parity = key/mouse
  event mapping only — **IME is not a regression risk** (it's unsupported either
  way; can be added later per-OS as an enhancement).
- **The backend seam already exists:** `ZGF.Gui/PlatformBackend.Resolve(config)`
  already picks Metal (mac) vs OpenGL (win/linux). New backends slot in here.

## Target abstraction — the sealed contract (as built in Phase 0)

We extended the existing `IApp`/`IWindow` directly rather than adding new `IPlatform*`
interfaces — the waist was already there, it just leaked GLFW. Each native backend
implements exactly this contract (neutral types live in `ZGF.Core/Input.cs`):

```
IApp
  IReadOnlyList<MonitorWorkArea> Monitors            // work areas for popup placement
  (existing) MainWindow, Windows, OnTick, Run, CreateWindow/CreatePopupWindow, MakeMainContextCurrent

IWindow
  IntPtr WindowHandle        // backend-internal handle (GLFW window today)
  IntPtr NativeHandle        // OS handle: Win32 HWND | Cocoa NSWindow | X11 Window
  Width, Height (points), DpiScale, IsVisible, IsFocused, IsPointerOver, NeedsRedraw
  events: OnResize, OnFramebufferResize, OnFocusChanged, OnClose,
          OnKey(KeyboardKey, InputAction, KeyModifiers),
          OnMouseButton(int buttonIndex, InputAction, KeyModifiers),
          OnScroll(double, double), OnPointerEnter(bool)
  Show/Hide/Focus, SetPosition/SetSize, GetPosition, GetCursorPosition,
  SetIcon(IReadOnlyList<WindowIconImage>), RequestRedraw, RenderNow, MakeContextCurrent
```

Input binds to `IWindow` events (`DesktopInputSystem`), never to `GLFW.Window`.

Note there is **no `OnChar`/text-input event** — the app has no IME/char path today (see
below). Add one per-OS later as an enhancement, not as migration parity.

---

## Phase 0 — Seal the waist (GLFW still underneath, no behavior change) — ✅ DONE

The highest-value, lowest-risk step; useful even if we never finish the migration
(also de-risks a future Silk.NET move). **Do this first regardless of intent.**

Implemented: neutral input types (`ZGF.Core/Input.cs`: `InputAction`, `KeyModifiers`,
`WindowIconImage`, `MonitorWorkArea`); `IWindow`/`IApp` gained `NativeHandle`, `IsFocused`,
`IsPointerOver`, `GetPosition`, `GetCursorPosition`, `SetIcon`, neutral input events, and
`IApp.Monitors`; `OpenGlWindow`/`MetalWindow` raise neutral events and expose the OS handle
(Win32/X11/Cocoa); `GlfwInputSystem` → `DesktopInputSystem` bound to `IWindow`; chrome,
decorators, coordinates, popup/secondary factories, and `ContextMenuManager` all consume the
sealed surface. Verified: zero `GLFW`/`Glfw.` references remain in `ZGF.Gui` or the `GitBench`
app; GitBench, GitBench.Tests, and ZGF.Gui.Tests all build clean.

**What Phase 0 established (read before starting any native backend):**

- The seal boundary is `ZGF.Core`. `using GLFW` / `Glfw.` now appears **only** in
  `ZGF.Core` (`OpenGlApp`, `OpenGlWindow`, `MetalApp`, `MetalWindow`, `GlfwMonitors`,
  `Native*`) and the keyboard adapter. A native backend is a new pair of
  `IApp`/`IWindow` implementations in `ZGF.Core` selected by `PlatformBackend.Resolve`;
  nothing above `ZGF.Core` changes.
- **Correction to the original plan:** `GuiApp`'s `if (_app is OpenGlApp) glViewport(...)`
  is **not** a GLFW leak (it uses `ZGF.Core` + `OpenGL.NET` types) and was left in place.
  It's *render-backend* coupling, not windowing — see the seam below.
- **Render-frame wiring is the remaining backend coupling** the native phases touch.
  `PopupWindowFactory`/`SecondaryWindowFactory` branch on `_window is OpenGlWindow` /
  `is MetalWindow` to install the per-window `RenderFrame` closure (GL clear+swap, or
  Metal `CAMetalLayer` + command buffer), and `GuiApp` branches on `_app is OpenGlApp`
  for `glViewport`. These key off the *window/app concrete type*, which the windowing
  swap changes — a native `CocoaWindow` must still expose `.Layer`/`RenderFrame` so the
  Metal closure binds, or that wiring moves behind an interface. Decide per phase.
- Confirmed **no IME/char path** (no GLFW char callback was ever set), so native backends
  reach parity without text-composition work.

## Phase 1 — Native macOS (Cocoa) backend

Smallest gap, biggest payoff (kills the mac dylib, cleans the Metal loop).

- New `CocoaApp : IApp` + `CocoaWindow : IWindow` implementing the contract above:
  `NSApplication` run loop, `NSWindow` create (borderless/floating variants for
  popups), `NSEvent` → the neutral `OnKey`/`OnMouseButton`/`OnScroll`/`OnPointerEnter`
  events, `backingScaleFactor` → `DpiScale`, `NativeHandle` → the NSWindow,
  `Monitors` via `NSScreen`. Reuse `Objc.cs`/`MetalApi.cs` and the existing
  `CAMetalLayer` attach from `MetalWindow` (keep `.Layer`/`RenderFrame` so the
  factories' Metal render closures bind unchanged).
- Point `PlatformBackend.ResolveMetal` at `CocoaApp` instead of `MetalApp(GLFW)`;
  update the `is MetalWindow` checks to the new window type (or a shared interface).
- **Risks:** event-loop integration with the existing tick/redraw loop;
  reentrancy during the pump (we already guard this); multi-window (share one
  `MTLDevice` — trivial vs GL share lists).
- **Acceptance:** mac runs with zero GLFW; drop `osx-*` dylibs from the bundle.

## Phase 2 — Native Windows (Win32) backend

- `Win32App`/`Win32Window` implementing the contract: `RegisterClassEx` +
  `CreateWindowEx`, `WndProc` message pump, WGL context via
  `wglCreateContextAttribsARB` (GL 4.1 core), GL **context share lists** for the
  shared font atlas, Per-Monitor-V2 DPI (`WM_DPICHANGED`), `WM_ENTERSIZEMOVE` timer
  pump (keep rendering during drags), `WM_KEYDOWN`/mouse/focus/close → the neutral
  events, `NativeHandle` → the HWND, `Monitors` via `EnumDisplayMonitors`. Keep
  `RenderFrame` so the factories' GL render closures bind (update the
  `is OpenGlWindow` checks to the new window type, or a shared `IGlWindow` interface).
- `WindowsWindowChrome`/`WindowsPopupDecorator` already take `NativeHandle` (HWND)
  directly post-Phase 0 — no change needed beyond pointing the backend at them.
- **Acceptance:** win runs with zero GLFW; drop `glfw3.dll`.

## Phase 3 — Native Linux/X11 backend (Ubuntu)

Largest lift; target **X11 only** (Wayland apps run via XWayland).

- `X11App`/`X11Window` implementing the contract: Xlib/xcb + GLX (or EGL) context,
  XKB keyboard mapping → `KeyboardKey`, EWMH/`_NET_WM` hints for borderless +
  floating + positioning, RandR for `Monitors`/DPI, XInput2 for scroll, X selections
  for clipboard (we already touch `GetX11SelectionString`). `NativeHandle` → the X11
  Window. Keep `RenderFrame`/`is OpenGlWindow` wiring as in Phase 2.
- **Defer native Wayland** as a separate decision (no global positioning,
  mandatory client-side decorations — conflicts with our absolute-positioned
  popups).
- **Acceptance:** Ubuntu runs with zero GLFW; delete vendored `Glfw.NET` (and the
  `Native/linux-x64` README/binary, and `NativeLibraryResolver`).

## Phase 4 — Retire GLFW

- Delete `framework/Glfw.NET`, `ZGF.KeyboardModule.GlfwAdapter`, `Native/*`
  binaries, and `NativeLibraryResolver`.
- Sample/test apps that use GLFW directly (`NodeGraphApp`,
  `QuadTreeRendererProgram`, `OpenGlWrapper.Tests`, `ZGF.Gui.Tests`) are **not
  GitBench** — either migrate them to the new backends or keep a minimal GLFW
  shim project just for them. Decide per-app; they don't block GitBench shipping.
  Note Phase 0 added a `ZGF.Core` → `ZGF.KeyboardModule.GlfwAdapter` reference (for
  GLFW key mapping); remove it here once no backend uses GLFW.

---

## Cross-cutting risk register

| Risk | Notes / mitigation |
|---|---|
| HiDPI points-vs-pixels | `WindowToGuiCoords` relies on window-size vs framebuffer split; reproduce per-OS (backingScale / WM_DPICHANGED / Xft.dpi). |
| Multi-window context sharing | Shared GL font atlas needs WGL/GLX share lists; Metal shares one device for free. |
| Event-loop reentrancy | Existing code notes the view tree isn't reentrant mid-pump; keep the deferred-clear pattern. |
| Win32 modal resize loop | Pump renders via a timer during `WM_ENTERSIZEMOVE`. |
| Headless tests | `ZGF.Gui.Tests` constructs a real `OpenGlApp`; provide an offscreen/no-op backend or keep GLFW for tests. |
| IME / Unicode text | Not supported today, so not a parity blocker — but if added later, it's per-OS (`WM_IME_*`, `interpretKeyEvents`, XIM/IBus). |

## Sequencing & effort (rough)

1. Phase 0 — ✅ **done**. Small, independently valuable; the waist is sealed.
2. Phase 1 (macOS) — small/medium. **Recommended next** (GLFW is already vestigial there).
3. Phase 2 (Win32) — medium.
4. Phase 3 (X11) — largest.
5. Phase 4 — cleanup.

A **hybrid is viable indefinitely** (e.g. native macOS + GLFW on win/linux): now that
Phase 0 has sealed the waist, backends mix per-platform without touching shared code.
