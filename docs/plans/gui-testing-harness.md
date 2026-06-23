# GUI Testing Harness (Phase 1)

## Context

The GUI framework (`framework/ZGF.Gui`, `ZGF.Gui.Desktop`) is already ~70% testable: it has the
hard seams most GUI frameworks lack — an `ICanvas` drawing abstraction, GPU-free deterministic
layout (`View.LayoutSelf`), CPU text metrics, Context DI, and an `InputSystem` that accepts
synthetic events. An xUnit suite already covers layout, mount lifecycle, and controller logic
without a window/GPU.

What's missing for "proper" testing is the integration layer that turns those seams into ergonomic
tests:

- **Gap A — integration input.** Tests call `controller.OnMouseButtonStateChanged(...)` directly
  (`ZGF.Gui.Tests/KbmInputTests.cs:73`), bypassing `InputSystem.HitTest` → z-order →
  `PointerOwnershipArbiter` → focus path → capture/bubble. The routing — exactly where the
  recurring context-menu/popup bugs live — is never exercised end-to-end.
- **Gap B — no element queries.** Tests hold direct `View` references; there's no
  "find the button and click it" over a built tree.
- **Gap C — no render capture.** `FakeCanvas` discards every draw call; you can't assert what drew.
- **Gap D — no clock control.** Animations advance off wall-clock delta; tests can't step them.
- **Gap E — no assembled headless host.** Each test hand-rolls Context + InputSystem + root.

The outcome of Phase 1: a `GuiTestHarness` that mounts a real widget tree headlessly, lays it out at
a viewport, drives input through the **real** dispatch path, controls the clock, captures draws, and
queries the tree — closing gaps A–E. (Pixel/bitmap snapshots are deferred to a later phase.)

Two key facts grounded the design:
- Input is **keyboard-only** — `IWindow` exposes only `OnKey/OnMouseButton/OnScroll/OnPointerEnter`
  (`ZGF.Desktop/IWindow.cs:23-26`); `TextInputView`'s controller turns key events into characters.
  So `Type()` must synthesize `KeyboardKeyEvent`s, not char events.
- Identity already exists: `Widget.Id` (init prop) propagates to `View.Id`
  (`ZGF.Gui/Widgets/Widget.cs:32`). Query selectors reuse `Id` — no new concept.
- GUI coords are **Y-up**, matching layout (`DesktopInputSystem.cs:256`); the harness uses the same
  space as `View.Position`.

## Decisions

- Harness lives in a **new shared `ZGF.Gui.Testing` library** (mirrors the `ZGF.Gui.Benchmarks` /
  `ZGF.Gui.MemoryDiagnostics` convention) so both `ZGF.Gui.Tests` and `GitBench.Tests` can use it.
- `RecordingCanvas` text metrics are **selectable**: synthetic by default, real FreeType opt-in.

## New project: `framework/ZGF.Gui.Testing`

- `ZGF.Gui.Testing.csproj` — `net10.0`; ProjectReferences `ZGF.Gui`, `ZGF.Gui.Desktop`, `ZGF.Fonts`.
- Register in the solution(s) that build the GUI tests (`GitBench.sln`, `ENV Game Framework.sln`).
- Add a ProjectReference from `ZGF.Gui.Tests/ZGF.Gui.Tests.csproj` (and later `GitBench.Tests`).

Convention reminder: **no code comments** (per project preference); match surrounding style.

## Component 1 — `RecordingCanvas : ICanvas` (`RecordingCanvas.cs`)

A capturing implementation of `ICanvas` (`ZGF.Gui/ICanvas.cs`). For each draw method, append the
input struct plus the current clip rect and a monotonically increasing sequence index into typed
lists (`Rects`, `Texts`, `Images`, `BoxShadows`, `Lines`, `Circles`, `Beziers`). Maintain a clip
`Stack<RectF>` for `PushClip/PopClip/TryGetClip`. Expose `InDrawOrder()` (merge by sequence) for
snapshot-style assertions, plus `Reset()` to clear between frames (called by the harness per render).

Text metrics via a small seam `ITextMeasurer { float MeasureTextWidth(ReadOnlySpan<char>, TextStyle);
float MeasureTextLineHeight(TextStyle); }`:
- `SyntheticTextMeasurer` (default) — deterministic `8px/char`, `16px` line height (matches existing
  `FakeCanvas`), keeping layout tests fast and platform-independent.
- `FreeTypeTextMeasurer` — wraps `ZGF.Fonts/FreeTypeFontBackend` (CPU-only HarfBuzz shaping) for real
  advance widths/line height. **Implementation note:** the `TextStyle → FontHandle` resolution +
  line measurement currently lives inside `RenderedCanvasBase` (private `MeasureLineWidth`). If it
  isn't reusable standalone, extract it into a CPU-only helper in `ZGF.Gui` (e.g. `TextMeasurement`)
  called by both `RenderedCanvasBase` and `FreeTypeTextMeasurer` — a small, behavior-preserving
  refactor. Default stays synthetic; real metrics are opt-in via the harness.

`RecordingCanvas` ctor takes `ITextMeasurer? measurer = null` (null ⇒ synthetic).

## Component 2 — Tree query API (`ViewQueryExtensions.cs`)

Extension methods over `View`, walking the live `Children` collection
(`ZGF.Gui/View.cs:227 ChildrenCollection`) recursively (run after Mount+Layout so bound children
exist):
- `View? FindById(this View, string id)` / `IEnumerable<View> FindAllById(...)` — match `View.Id`.
- `T? FindByType<T>(this View) where T : View` / `IEnumerable<T> FindAllByType<T>()`.
- `View? Find(this View, Func<View,bool>)` / `FindAll(...)`.
- `IEnumerable<View> Descendants(this View)` (the shared walker).
- `FindByText(string)` — best-effort: match views exposing text (e.g. `TextView`). Verify the text
  accessor during implementation; ship `FindById`/type/predicate as the primary selectors regardless.

## Component 3 — `GuiTestHarness` (`GuiTestHarness.cs`)

A headless mirror of `ZGF.Gui.Desktop/GuiWindowHost`. Factory:
`GuiTestHarness.Create(Func<Context,View> content, int width = 800, int height = 600,
Action<Context>? configure = null, ITextMeasurer? measurer = null)`.

Setup (mirrors `GuiWindowHost.SetRoot` + `DrawContent`):
1. `var ctx = new Context();`
2. `var canvas = new RecordingCanvas(measurer);  ctx.Canvas = canvas;`
3. `var input = new InputSystem();  ctx.AddService(input);` (`InputSystem` lives in `ZGF.Gui.Desktop`)
4. `var ticker = new FrameTicker();  ctx.AddService<IFrameTicker>(ticker);`
5. `configure?.Invoke(ctx);` — lets a test register extra services widgets `Require<>` (clipboard,
   popup/context-menu hosts, VMs). Unregistered required services throw — acceptable; harness grows.
6. `var root = content(ctx);  root.Width = width; root.Height = height;`
7. `root.OnRedrawNeeded = () => _redrawCount++;  root.Mount();  root.LayoutSelf();`

Public surface:
- **Tree/layout:** `Root`, `Canvas` (RecordingCanvas), `Input`, `Context`, `RedrawCount`,
  `Layout()` (re-run `LayoutSelf`), `Resize(w,h)`.
- **Render capture:** `Render()` → `canvas.Reset(); root.LayoutSelf(); root.DrawSelf(canvas);`
  returns `canvas`.
- **Pointer (mirrors `DesktopInputSystem` exactly, shared `Mouse _mouse`):**
  - `MoveTo(x,y)` — `_mouse.Point=(x,y)`; `SendMouseMovedEvent(Capturing)` (builds the hover
    `_focusQueue` dispatch path).
  - `Press(button=Left)` / `Release(button=Left)` — `_mouse.Press/Release`;
    `SendMouseButtonEvent(Pressed/Released, Capturing)`.
  - `Click(x,y,button=Left)` — `MoveTo` **then** `Press` **then** `Release` (hover-first is required;
    without the move there is no dispatch path and the click goes nowhere).
  - `ClickOn(View)` / `ClickOn(string id)` — click the center of `view.Position` (uses query API).
  - `Scroll(dx,dy)` — `SendMouseScrollEvent`.
- **Keyboard:** `KeyDown(key, mods=None)`, `KeyUp(...)`, `PressKey(...)` (down+up),
  `Type(string)` — char→`(KeyboardKey, shift)` map for the ASCII subset, applying Shift; documented
  as best-effort, with `PressKey` as the escape hatch for unmapped keys.
- **Clock:** `Tick(seconds)` → `ticker.Tick(seconds)` then `Layout()`; `Advance(seconds, step=1/60f)`
  steps in frames (mirrors `GuiApp.HandleTick`'s TickAnimations).
- **Dispose:** `root.Unmount(); ctx.Dispose();`

Single-window only in Phase 1 (no `PointerOwnershipArbiter`/popup wiring).

## Component 4 — Example tests (in `ZGF.Gui.Tests`, proving each gap closed)

- `HarnessSmokeTests.cs` — build a tree with a clickable widget `Id="ok"` that increments a counter;
  `harness.ClickOn("ok")`; assert counter incremented **and** `RedrawCount > 0`. Exercises
  hit-test→dispatch (A) + query (B) + redraw notify.
- `RenderCaptureTests.cs` — tree drawing a known rect; `harness.Render()`; assert
  `harness.Canvas.Rects` contains it with expected bounds/color (C).
- `HarnessClockTests.cs` — a widget animating via `IFrameTicker`; `harness.Tick(0.5f)`; assert
  advanced state (D).

## Files to create / modify

- **Create:** `framework/ZGF.Gui.Testing/ZGF.Gui.Testing.csproj`, `RecordingCanvas.cs`,
  `ITextMeasurer.cs` (+ `SyntheticTextMeasurer`, `FreeTypeTextMeasurer`), `ViewQueryExtensions.cs`,
  `GuiTestHarness.cs`.
- **Modify:** `GitBench.sln` / `framework/ENV Game Framework.sln` (add project);
  `framework/ZGF.Gui.Tests/ZGF.Gui.Tests.csproj` (add ProjectReference); possibly extract
  `TextMeasurement` helper from `framework/ZGF.Gui/RenderedCanvasBase.cs` (only if the FreeType
  measurer can't reuse it as-is).
- **Add test files** under `framework/ZGF.Gui.Tests/`.

## Deferred (not in Phase 1)

- Pixel/bitmap snapshot backend (completing `SoftwareRenderedCanvas`) — draw-command capture covers
  most visual regressions without GPU/flake.
- Multi-window / popup / context-menu / arbiter harness.
- Full Unicode/IME text entry beyond the ASCII map.

## Verification

1. Build: `dotnet build "GitBench.sln"` (and the framework solution) — confirms the new project and
   references compile.
2. Tests: `dotnet test framework/ZGF.Gui.Tests/ZGF.Gui.Tests.csproj` — the existing suite still
   passes and the three new example tests pass, proving end-to-end input, render capture, and clock
   control work headlessly.
3. Sanity: the `ClickOn("ok")` test failing if `MoveTo` is omitted before `Press` confirms the
   harness drives the real hover/dispatch path (not a shortcut).
