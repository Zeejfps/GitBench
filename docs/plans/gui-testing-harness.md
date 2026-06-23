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
  (`ZGF.Gui.Tests/KbmInputTests.cs:76,81`), bypassing `InputSystem.HitTest` → z-order →
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

Facts that ground the design (all verified against the current code):

- **Input is keyboard-only.** `IWindow` exposes only `OnKey/OnMouseButton/OnScroll/OnPointerEnter`
  (`ZGF.Desktop/IWindow.cs:23-26`); a `TextInputView`'s controller turns key events into characters
  via `KeyboardKey.ToChar(shift)` (`BaseTextInputKbmController.cs:232`). So `Type()` must synthesize
  `KeyboardKeyEvent`s, not char events.
- **Identity already exists.** `Widget.Id` (init prop, `Widgets/Widget.cs:17`) propagates to
  `View.Id` (`Widget.cs:32`; `View.Id` is `public string? { get; set; }`, `View.cs:140`). Query
  selectors reuse `Id` — no new concept.
- **GUI coords are Y-up**, matching layout. The harness drives input **directly in GUI space** (it
  has no window, so it skips `DesktopInputSystem.WindowToGuiCoords`'s Y-flip at
  `DesktopInputSystem.cs:256`). Caller coordinates are therefore the same space as `View.Position`
  (origin bottom-left, `RectF { Left, Bottom, Width, Height }`, `Top = Bottom + Height`). Addressing
  elements by `Id`/center via `ClickOn` lets tests avoid reasoning about Y-up at all.

## Corrections folded in from code validation

The first draft of this plan had two inaccuracies that change the design; both are resolved below.

1. **`InputSystem` namespace + construction.** `InputSystem` lives in **`ZGF.Gui.Desktop.Input`**
   (`Input/InputSystem.cs`), not `ZGF.Gui.Desktop`. The class the original plan was thinking of is
   **`DesktopInputSystem`** (`ZGF.Gui.Desktop`), a *separate* OS-bridge that owns the `Mouse`, does
   window→GUI coordinate conversion, and consults `PointerOwnershipArbiter`. The harness does **not**
   instantiate `DesktopInputSystem` (it would require an `IWindow` + canvas). Instead the harness
   owns a **bare `InputSystem` + a `Mouse`** and replays the exact event-struct construction
   `DesktopInputSystem` performs (`DesktopInputSystem.cs:95-167,184-239`), minus window/coordinate/
   arbiter concerns. This matches what existing tests and `GuiApp` already do — register the bare
   `InputSystem` as the service (`GuiApp.cs:72` `context.AddService(_mainInput.InputSystem)`;
   `KbmInputTests.cs` `ctx.AddService(input)`).

2. **`View.Children` is `protected`** (`View.cs:212`), and `ChildrenCollection` exposes only the
   non-generic `IEnumerable` (deliberately — no boxing `IEnumerable<View>`). Extension methods in a
   separate `ZGF.Gui.Testing` assembly cannot reach a `protected` member, so the query API needs a
   **public read-only downward-traversal seam on `View`** (see Component 2). `View.Parent` and
   `View.SiblingIndex` are already public; the seam is symmetric with those.

## Decisions

- Harness lives in a **new shared `ZGF.Gui.Testing` class library** (mirrors the
  `ZGF.Gui.Benchmarks` / `ZGF.Gui.MemoryDiagnostics` convention, but `OutputType` is a library, not
  `Exe`) so both `ZGF.Gui.Tests` and `GitBench.Tests` can use it.
- The harness uses a **bare `InputSystem` + `Mouse`** and drives `Send…Event` directly (correction 1
  above) — single-window, no arbiter/popup wiring in Phase 1.
- `RecordingCanvas` text metrics are **selectable**: synthetic by default, real FreeType opt-in. The
  FreeType path is a stretch goal, not a Phase-1 blocker (it touches production rendering code — see
  Component 1).
- Query traversal goes through a **public `View` seam** (correction 2 above): add
  `public int ChildCount => _children.Count;` and `public View ChildAt(int index) => _children[index];`
  to `View` — zero-alloc, read-only, exposes no mutation, symmetric with `Parent`/`SiblingIndex`.
  (Alternative considered: a single `public IReadOnlyList<View> ChildViews` — rejected as it widens
  the type surface and the comment on `ChildrenCollection` explicitly avoids a LINQ surface. Second
  alternative: `protected internal Children` + `[InternalsVisibleTo("ZGF.Gui.Testing")]` — rejected
  because it leaks the *mutable* `ChildrenCollection` to the test assembly.)

## New project: `framework/ZGF.Gui.Testing`

- `ZGF.Gui.Testing.csproj` — `net10.0`, **library** (no `<OutputType>Exe`), `Nullable=enable`,
  `ImplicitUsings=enable`. ProjectReferences:
  - `ZGF.Gui` (View, Context, Widget, ICanvas + draw structs, FrameTicker)
  - `ZGF.Gui.Desktop` (`InputSystem`, `Mouse`, the event structs, `EventPhase`, `MouseButton`,
    `InputState`, `InputModifiers` — all under `ZGF.Gui.Desktop.Input`)
  - `ZGF.KeyboardModule` (explicit — `KeyboardKey` appears in the harness's public `KeyDown`/`Type`
    signatures; it flows transitively through `ZGF.Gui.Desktop`, but reference it directly so the
    public surface is self-contained)
  - `ZGF.Geometry` (explicit — `PointF`/`RectF` appear in public signatures; also transitive)
  - `ZGF.Fonts` — **only** if the `FreeTypeTextMeasurer` ships in Phase 1; omit otherwise.
  - It does **not** need `ZGF.Desktop`/`IWindow` (the harness has no window).
- Register in the solution(s) that build the GUI tests (`GitBench.sln`, `framework/ENV Game Framework.sln`).
- Add a ProjectReference from `ZGF.Gui.Tests/ZGF.Gui.Tests.csproj` (and later `GitBench.Tests`).

Convention reminder: **no code comments** (per project preference); match surrounding style.

## Component 0 — framework seam (`ZGF.Gui/View.cs`)

Add the public read-only child accessors that the query API needs (correction 2):

```
public int ChildCount => _children.Count;
public View ChildAt(int index) => _children[index];
```

This is the only change to production code required for Phase 1 (the FreeType `TextMeasurement`
extraction in Component 1 is optional). It is additive and read-only.

## Component 1 — `RecordingCanvas : ICanvas` (`RecordingCanvas.cs`)

A capturing implementation of `ICanvas` (`ZGF.Gui/ICanvas.cs`). `ICanvas` has **eleven** members the
harness must implement — seven draw methods, three clip methods, **two text-measure methods, and two
image-size queries** (the original plan omitted the latter two):

```
void DrawRect(in DrawRectInputs);  void DrawText(in DrawTextInputs);  void DrawImage(in DrawImageInputs);
void DrawBoxShadow(in DrawBoxShadowInputs);  void DrawLine(in DrawLineInputs);
void DrawCircle(in DrawCircleInputs);  void DrawBezier(in DrawBezierInputs);
bool TryGetClip(out RectF);  void PushClip(RectF);  void PopClip();
float MeasureTextWidth(ReadOnlySpan<char>, TextStyle);  float MeasureTextLineHeight(TextStyle);
int GetImageWidth(string imageId);  int GetImageHeight(string imageId);
```

For each draw method, append the input struct plus the current clip rect and a monotonically
increasing sequence index into typed lists (`Rects`, `Texts`, `Images`, `BoxShadows`, `Lines`,
`Circles`, `Beziers`). Maintain a clip `Stack<RectF>` for `PushClip/PopClip/TryGetClip` (mirror
`FakeCanvas`: `TryGetClip` returns the top or `false` when empty). Expose `InDrawOrder()` (merge by
sequence) for snapshot-style assertions, plus `Reset()` to clear lists + clip stack + sequence
between frames (called by the harness per render). `GetImageWidth/Height` return a configurable
default (0, matching `FakeCanvas`), overridable for image-layout tests.

Text metrics delegate to a small seam
`ITextMeasurer { float MeasureTextWidth(ReadOnlySpan<char>, TextStyle); float MeasureTextLineHeight(TextStyle); }`:

- `SyntheticTextMeasurer` (default) — deterministic `8px/char` width, `16px` line height (matches the
  existing `FakeCanvas`, `ZGF.Gui.Tests/FakeCanvas.cs:27-28`), keeping layout tests fast and
  platform-independent.
- `FreeTypeTextMeasurer` (**opt-in, stretch**) — real advance widths/line height via
  `ZGF.Fonts/FreeTypeFontBackend` (CPU-only HarfBuzz shaping). **Risk note:** the real measurement in
  `RenderedCanvasBase` is *not* a standalone helper — `MeasureLineWidth` (`RenderedCanvasBase.cs:565`)
  and `ResolveFont` (`:597`) are private and depend on instance state: a `FreeTypeFontBackend`
  (`_fonts`), a default `FontHandle`, a `_fontsByFamily` map, and `_dpiScale`. Producing a
  `FreeTypeTextMeasurer` therefore means extracting that font-resolution + measurement logic into a
  CPU-only `TextMeasurement` type in `ZGF.Gui` that both `RenderedCanvasBase` and the measurer
  construct. Treat this as its **own isolated, separately-verified change** (it touches the production
  render path); do not couple it to the harness landing. The `ZGF.Gui.Tests` project already copies
  `Inter-Regular.ttf` to its output, so a FreeType-backed test has a font to load when this lands.

`RecordingCanvas` ctor takes `ITextMeasurer? measurer = null` (null ⇒ `SyntheticTextMeasurer`).

## Component 2 — Tree query API (`ViewQueryExtensions.cs`)

Extension methods over `View`, walking children **via the public `ChildCount`/`ChildAt` seam**
(Component 0) recursively (run after Mount+Layout so bound children exist):

- `View? FindById(this View, string id)` / `IEnumerable<View> FindAllById(...)` — match `View.Id`.
- `T? FindByType<T>(this View) where T : View` / `IEnumerable<T> FindAllByType<T>()`.
- `View? Find(this View, Func<View,bool>)` / `FindAll(...)`.
- `IEnumerable<View> Descendants(this View)` (the shared walker, recursing through `ChildAt`).
- `FindByText(string)` — best-effort: match views exposing text (e.g. `TextView`). Verify the text
  accessor during implementation; ship `FindById`/type/predicate as the primary selectors regardless.

Note `View.Id` defaults to `null`; `FindById` only finds views whose `Widget.Id` was set (or whose
`View.Id` was assigned directly). Self-inclusion: `Descendants` excludes `this`; the `Find*` helpers
match the subtree under `this` (document whether `this` is considered — recommend matching `this` too
so `root.FindById(...)` finds a root with that id).

## Component 3 — `GuiTestHarness` (`GuiTestHarness.cs`)

A headless host modeled on `GuiWindowHost.SetRoot` + `DrawContent` (`ZGF.Gui.Desktop/GuiWindowHost.cs:38,58`).
Factory: `GuiTestHarness.Create(Func<Context,View> content, int width = 800, int height = 600,
Action<Context>? configure = null, ITextMeasurer? measurer = null)`.
(The `Func<Context,View>` shape matches `GuiApp._contentFactory`, `GuiApp.cs:28`.)

Setup:
1. `var ctx = new Context();`
2. `var canvas = new RecordingCanvas(measurer);  ctx.Canvas = canvas;`
3. `var input = new InputSystem();  ctx.AddService(input);` — bare `InputSystem` from
   `ZGF.Gui.Desktop.Input`; this is the type widgets `Require<InputSystem>()`.
4. `var mouse = new Mouse();` — the harness-owned `ZGF.Gui.Desktop.Input.Mouse`; referenced by every
   synthetic mouse event.
5. `var ticker = new FrameTicker();  ctx.AddService<IFrameTicker>(ticker);`
6. `configure?.Invoke(ctx);` — lets a test register extra services widgets `Require<>` (clipboard,
   `IContextMenuHost`/`IPopupWindowFactory` fakes, VMs). Unregistered required services throw
   `InvalidOperationException` naming the type (`Context.Require`, `Context.cs:95`) — acceptable; the
   harness grows fakes as widgets need them.
7. `var root = content(ctx);  root.Width = width;  root.Height = height;`
8. `root.OnRedrawNeeded = () => _redrawCount++;` — `OnRedrawNeeded` is a `public Action?`
   (`View.cs:205`), invoked when dirty reaches the parent-less root (`View.cs:485-486`). Set it on the
   actual root (the harness uses `content` directly as root, unlike `GuiApp` which wraps content in a
   sized `ContainerView`).
9. `root.Mount();  root.LayoutSelf();` — Mount registers controllers with `InputSystem`; `LayoutSelf`
   assigns `View.Position`, which `HitTest` needs (`InputSystem.cs:463`). Both must precede any input.

Public surface:
- **Tree/layout:** `Root`, `Canvas` (the `RecordingCanvas`), `Input`, `Context`, `RedrawCount`,
  `Layout()` (re-run `root.LayoutSelf()` — cheap when clean, dirty-tracked), `Resize(w,h)`
  (set `root.Width/Height`, then `Layout()`).
- **Render capture:** `Render()` → `canvas.Reset(); root.LayoutSelf(); root.DrawSelf(canvas);`
  returns `canvas`. (Mirrors `GuiWindowHost.DrawContent`, `GuiWindowHost.cs:61-62`.)
- **Pointer** (owns the shared `Mouse mouse`; builds the same structs `DesktopInputSystem` does and
  sends them through `input` with `EventPhase.Capturing`). To keep hit-testing correct after state
  changes, `MoveTo`/`Click` call `Layout()` first (no-op when clean):
  - `MoveTo(x,y)` — `mouse.Point = new PointF(x,y);` then
    `var e = new MouseMoveEvent { Mouse = mouse, Phase = EventPhase.Capturing }; input.SendMouseMovedEvent(ref e);`
    (this builds the hover `_focusQueue` dispatch path via `RefreshHover`/`BuildPath`,
    `InputSystem.cs:307,359,534`).
  - `Press(button = MouseButton.Left)` / `Release(...)` — `mouse.Press/Release(button)` then
    `var e = new MouseButtonEvent { Mouse = mouse, Button = button, State = InputState.Pressed (or Released),
    Modifiers = InputModifiers.None, Phase = EventPhase.Capturing }; input.SendMouseButtonEvent(ref e);`
  - `Click(x,y,button = Left)` — `MoveTo` **then** `Press` **then** `Release` (hover-first is
    required: `SendMouseButtonEvent` dispatches through the `_focusQueue` that only a prior move
    builds; without the move the click goes nowhere — see Verification #3).
  - `ClickOn(View)` / `ClickOn(string id)` — click the center of `view.Position`
    (`(Left + Width/2, Bottom + Height/2)`, already GUI-space; uses the query API).
  - `Scroll(dx,dy)` — `new MouseWheelScrolledEvent { Mouse = mouse, DeltaX = dx, DeltaY = dy,
    Phase = EventPhase.Capturing }; input.SendMouseScrollEvent(ref e);`
- **Keyboard:** `KeyDown(KeyboardKey key, InputModifiers mods = InputModifiers.None)`, `KeyUp(...)`,
  `PressKey(...)` (down+up) — each builds `new KeyboardKeyEvent { Key = key, State = Pressed/Released,
  Modifiers = mods, Phase = EventPhase.Capturing }` and calls `input.SendKeyboardKeyEvent(ref e)`.
  `Type(string)` — char→`(KeyboardKey, shift)` map for the ASCII subset, applying Shift; documented as
  best-effort, with `PressKey` as the escape hatch for unmapped keys. **Robustness:** a unit test
  asserts the map round-trips against the framework — for every `(char → key, shift)` entry,
  `key.ToChar(shift) == char` (`KeyboardKey.ToChar`) — so the harness map can't silently drift from
  the controller's decoding.
- **Clock:** `Tick(seconds)` → `ticker.Tick(seconds); Layout();`. `Advance(seconds, step = 1/60f)`
  steps in frames (mirrors `GuiApp.TickAnimations`, which clamps each `Tick` dt to ≤0.1s,
  `GuiApp.cs:246-254`; stepping at 1/60 stays well under that). Animations subscribe via
  `Require<IFrameTicker>().Add(...)`.
- **Dispose:** `root.Unmount(); ctx.Dispose();` (`Context.Dispose` disposes only Context-owned
  singletons; the caller-added `InputSystem`/`Mouse` are GC'd, `Context.cs:168`).

Single-window only in Phase 1 (bare `InputSystem`, no `PointerOwnershipArbiter`/popup wiring).

## Component 4 — Example tests (in `ZGF.Gui.Tests`, proving each gap closed)

- `HarnessSmokeTests.cs` — build a tree with a clickable widget `Id="ok"` that increments a counter;
  `harness.ClickOn("ok")`; assert the counter incremented **and** `RedrawCount > 0`. Exercises
  hit-test→dispatch (A) + query (B) + redraw notify.
- `RenderCaptureTests.cs` — tree drawing a known rect; `harness.Render()`; assert
  `harness.Canvas.Rects` contains it with expected bounds/color (C).
- `HarnessClockTests.cs` — a widget animating via `IFrameTicker`; `harness.Tick(0.5f)`; assert
  advanced state (D).
- `KeyMapRoundTripTests.cs` — assert the `Type()` char→key map round-trips against
  `KeyboardKey.ToChar` (guards the keyboard surface against drift).

## Files to create / modify

- **Create:** `framework/ZGF.Gui.Testing/ZGF.Gui.Testing.csproj`, `RecordingCanvas.cs`,
  `ITextMeasurer.cs` (+ `SyntheticTextMeasurer`; `FreeTypeTextMeasurer` only if the stretch path
  ships), `ViewQueryExtensions.cs`, `GuiTestHarness.cs`.
- **Modify:** `framework/ZGF.Gui/View.cs` (add `ChildCount`/`ChildAt` — Component 0);
  `GitBench.sln` / `framework/ENV Game Framework.sln` (add project);
  `framework/ZGF.Gui.Tests/ZGF.Gui.Tests.csproj` (add ProjectReference). **Optional/separate change:**
  extract a CPU-only `TextMeasurement` helper from `framework/ZGF.Gui/RenderedCanvasBase.cs` (only
  when the FreeType measurer ships; verify it independently as it touches the render path).
- **Add test files** under `framework/ZGF.Gui.Tests/`.

## Risks & open questions

- **Required services beyond the defaults.** Widgets that `Require<IContextMenuHost>` /
  `IPopupWindowFactory` / clipboard / a VM will throw on Mount unless `configure` registers a fake.
  Phase 1 ships the harness with `InputSystem`/`IFrameTicker`/`Canvas` only; document the throw and
  grow no-op fakes as real tests hit them.
- **Layout-before-hit-test ordering.** Any state change that moves/adds/removes views must be
  followed by a layout pass before the next hit test. `MoveTo`/`Click` call `Layout()` defensively;
  tests that assert on positions should call `Layout()`/`Render()` after mutating state.
- **FreeType extraction blast radius.** The `TextMeasurement` refactor changes production rendering;
  keep it out of the harness's critical path and verify it on its own.
- **Multi-window / popups / arbiter** are explicitly out of scope (a future phase). Tests that need a
  context menu or popup cannot be written against Phase 1.

## Deferred (not in Phase 1)

- Pixel/bitmap snapshot backend (completing `SoftwareRenderedCanvas`) — draw-command capture covers
  most visual regressions without GPU/flake.
- Multi-window / popup / context-menu / arbiter harness (would wrap `DesktopInputSystem` +
  `PointerOwnershipArbiter` instead of a bare `InputSystem`).
- `FreeTypeTextMeasurer` + the `TextMeasurement` extraction, if not taken as a Phase-1 stretch.
- Full Unicode/IME text entry beyond the ASCII map.

## Verification

1. Build: `dotnet build "GitBench.sln"` (and `framework/ENV Game Framework.sln`) — confirms the new
   project, the `View.ChildCount/ChildAt` seam, and references compile.
2. Tests: `dotnet test framework/ZGF.Gui.Tests/ZGF.Gui.Tests.csproj` — the existing suite still
   passes and the new example tests pass, proving end-to-end input, render capture, clock control,
   and the keyboard map work headlessly.
3. Sanity (the real-dispatch proof): the `ClickOn("ok")` test must **fail** if `MoveTo` is removed
   from `Click` before `Press` — confirming the harness drives the real hover→`_focusQueue`→dispatch
   path (`SendMouseButtonEvent` over a `_focusQueue` that only a prior move builds), not a shortcut.
