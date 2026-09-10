# UI scale — one factor from logical points to device pixels

> **Framing:** on a large high-DPI Windows monitor the app renders tiny. The cause is not that the
> UI is written in pixels — it isn't — but that the framework derives its DPI scale from the
> framebuffer-to-window ratio, and on Windows that ratio is *always 1* no matter what the display
> scaling is set to. Windows display scaling is never read.
>
> Two things fall out of one mechanism: **honouring the OS content scale** (a bug) and **a
> user-settable UI scale** (a feature). Both are "how many device pixels is one logical point",
> and neither is worth building without the other.
>
> The expensive half of retrofitting UI scale — converting a codebase of pixel constants into
> scalable units — is already done and nobody had to mean it. `Sizes.RowHeight = 22`,
> `DialogFrame.WidthStandard = 480f`, every `Spacing` and `FontSize` token is already a logical
> point riding on an existing logical→device split. **None of them change.** What changes is that
> logical size stops being hardwired to the OS window size.

## Decisions

| Area | Decision |
|---|---|
| The factor | One number, `S = osContentScale × userUiScale`, owned by the window and pushed into the canvas as its `DpiScale`. Not two scales threaded separately — every consumer wants the product. |
| Logical size | **`canvas.Width = framebufferPixels / S`** — derived from the framebuffer, *not* from `Window.Width`. See "Four coordinate spaces" below: `Window.Width` is screen points, which already differ from pixels on macOS, and dividing those by `S` halves the UI on a Retina panel. Framebuffer-derived, the canvas invariant `deviceSize = logical × DpiScale` holds exactly and the viewport needs no separate plumbing. |
| User scale values | A discrete ladder — 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 — not a free slider. A dropdown in Settings → Appearance, matching Theme and Language. Free text invites 1.03 and the rounding artefacts that come with it. |
| Live or restart | **Live.** Font variants are keyed by device pixel size (`GetSizedVariant`), so a scale change bakes new glyphs rather than corrupting old ones. A restart-required setting would be a self-inflicted limitation. |
| OS scale on Windows | `glfwGetWindowContentScale`, plus the `ContentScaleChanged` callback for monitor-to-monitor drags. |
| OS scale on macOS | Unchanged. `MetalWindow.ComputeDpiScale` already reads `backingScaleFactor` and is correct. |
| Persistence | One `UiScale` on `Preferences` — a float in the file, snapped to the ladder as it is parsed. The OS content scale is never persisted — it's a property of the monitor, read fresh. |
| Automation | `GuiDriver` keeps reporting **logical** coordinates, and keeps *accepting* them — `ClickTool` injects `x`/`y` straight into the input system and `gui_snapshot` reports logical view positions, so snapshot-derived clicking is unaffected. Only `gui_screenshot` pixels stop being 1:1 with click coordinates (see Risks). |
| Out of scope | Per-monitor scale for a window straddling two displays; per-window scale; text-only zoom (`Cmd +/-` on the diff alone). |

## Four coordinate spaces, one converter, four types

The reason this plan has been wrong twice in the same place — once about input, once about macOS — is
that four different spaces are all called "coordinates" and all typed `PointF`/`PointI`/`int`. The
checker cannot tell them apart, so every conversion is a convention someone has to remember.

| Space | Units | Y | Where it appears |
|---|---|---|---|
| **Canvas** | logical points | **bottom-up** | the view tree, layout, `Sizes.*` tokens, `canvas.Width`, `GuiDriver` click/snapshot coords |
| **Window** | screen points, window-relative | top-down | `Window.Width/Height`, `GetCursorPosition`, `GetFrameSize` |
| **Screen** | screen points, desktop-absolute | top-down | `Window.GetPosition`, `SetPosition`, `SetSize`, monitor work areas, popup placement |
| **Device** | framebuffer pixels | top-down | `glViewport`, `glReadPixels`, `SetDrawableSize`, glyph baking |

Window and Screen differ only by the window origin. Device relates to Window by the **backing ratio**
(framebuffer ÷ window size — 2 on a Retina panel, 1 on Windows), which is what today's
`ComputeDpiScale` actually measures. Canvas relates to Device by `S`.

**The types.** New readonly structs in `ZGF.Geometry`, each wrapping the existing primitive with no
implicit conversion in either direction — an implicit conversion would switch the checker back off:

- `CanvasPoint` / `CanvasRect` / `CanvasSize` (float)
- `WindowPoint` / `WindowSize`
- `ScreenPoint` / `ScreenRect`
- `DeviceSize`

**Scope them to the boundary, not the view tree.** Every widget's `Width`/`Height`/`Position` stays a
bare float. Retyping the whole tree is an enormous change for a space that has no *other* space to be
confused with, and Rule 2 says don't add structure you haven't been forced into. The confusion is all
at the window/input/popup edges, so that is where the types go: `CanvasPoint` is what the converter
returns and what unwraps into the tree, one call deep.

**The converter.** One type — `WindowSpace` — owns every conversion between these four, and is the
only place the Y flip, the origin offset or `S` is written down:

```csharp
public readonly struct WindowSpace
{
    public float Scale { get; }          // S = contentScale × uiScale
    public WindowSize WindowSize { get; }
    public DeviceSize DeviceSize { get; }
    public ScreenPoint Origin { get; }

    public CanvasSize  CanvasSize { get; }        // DeviceSize / S
    public CanvasPoint ToCanvas(WindowPoint p);
    public CanvasPoint ToCanvas(ScreenPoint p);
    public ScreenPoint ToScreen(CanvasPoint p);
    public ScreenRect  ToScreen(CanvasRect r);
    public ScreenSize  ToScreen(CanvasSize s);    // popup sizing
    public CanvasSize  ToCanvas(ScreenSize s);
}
```

`GuiWindowHost` owns it and rebuilds it from the window on demand — it is a snapshot, and nothing
stores one across a resize or a monitor drag. Two existing functions collapse into it and are
deleted: `WindowCoordinates.ToScreenPoints` (`MainWindowCoordinates.cs:18-27`) and
`DesktopInputSystem.WindowToGuiCoords` (`DesktopInputSystem.cs:519-528`), which are today the same
transform written twice, in opposite directions, in two files. `IWindowCoordinates` survives as the
public seam and delegates.

## What already exists — verified

Checked against the code, not assumed. This section is most of why the estimate is days and not weeks.

- **The logical/device split is real and load-bearing.** `RenderedCanvasBase` measures and lays out in
  logical points and converts at draw time — `invScale = 1f / _dpiScale` appears at lines 529, 592,
  683, 709, 758. It is not a cosmetic field.
- **Glyphs already bake at arbitrary scale.** `RenderedCanvasBase.cs:853` bakes at
  `FontSize * DpiScale` and resolves through `_fonts.GetSizedVariant(baseFont, pixelSize)`, which is
  keyed by device pixel size. A scale change therefore *cannot* produce stale glyphs — it produces
  new atlas entries. `UpdateDpiScale` (line 246) sets a float and nothing else, and that is correct.
- **There is precedent at a non-integral scale.** `ZGF.Gui.Tests/GlyphRunTests.cs:93,128` already run
  the glyph pipeline at `dpiScale: 1.25f`.
- **SVG re-rasterizes at scale.** `SvgView.cs:140-143` sizes its raster at `fit * c.DpiScale`. Icons
  stay crisp; they will not blur at 1.5. Lucide glyphs are font-rendered and follow the text path.
- **The logical→screen seam already exists and is already correct.** `WindowCoordinates.ToScreenPoints`
  (`MainWindowCoordinates.cs:18-27`) computes `canvasPoint.X * (winW / (float)_canvas.Width)`. That
  ratio is 1 today and silently becomes `S` the moment canvas width stops equalling window width — so
  **every anchor that goes through `IWindowCoordinates` starts working with no edit.** It is threaded
  into the main window (`GuiApp.cs:94`), secondary windows (`SecondaryWindowFactory.cs:72`), popups
  (`PopupWindowFactory.cs:340`) and the context-menu manager (`ContextMenuManager.cs:41`).
- **The input path's logical/device seam already exists too, and is the mirror of the anchor one.**
  Every cursor coordinate that reaches the view tree goes through
  `DesktopInputSystem.WindowToGuiCoords` (`DesktopInputSystem.cs:519-528`), which computes
  `_canvas.Width / _window.Width` — exactly the inverse of `WindowCoordinates.ToScreenPoints`. That
  ratio is 1 today and silently becomes `1/S`, so **input needs no arithmetic edit either** (see
  Phase 2).
- **The terminal rides the same split.** `TerminalGridView` passes a logical `CellAdvance` and the
  canvas converts per-cell in device space with rounding (`RenderedCanvasBase.cs:602`), so cell drift
  across a wide row is already handled rather than newly introduced.
- **Tests are insulated.** `GuiTestHarness.cs:118` constructs its canvas at `dpiScale: 1f`, so the
  existing suite is unaffected by default and can opt in per-fixture.
- **The settings surface exists.** `Features/Settings/SettingsDialog.cs` has an Appearance section
  and an instant-apply contract; a scale dropdown is a third `SettingRow` next to Theme and Language.

## What is actually broken

**`framework/ZGF.Desktop/Backends/OpenGl/OpenGlWindow.cs:20`**

```csharp
protected override float ComputeDpiScale()
{
    Glfw.GetFramebufferSize(GlfwWindow, out var fbW, out var fbH);
    Glfw.GetWindowSize(GlfwWindow, out var winW, out var winH);
    if (winW > 0 && winH > 0)
        return MathF.Max((float)fbW / winW, (float)fbH / winH);
    return 1f;
}
```

On Windows, GLFW screen coordinates *are* pixels: `GetWindowSize` and `GetFramebufferSize` return the
same numbers at 100%, 150% and 200% alike, so this returns 1.0 always. On macOS the ratio is 2 on a
Retina panel and the result happens to be right. `Glfw.NET` binds the correct API — `ContentScale`
(`NativeWindow.cs:133`) and `ContentScaleChanged` (`NativeWindow.cs:39`) — and `ZGF.Desktop` never
calls either.

**Raising `DpiScale` alone does not fix it.** The viewport is derived from it:

```csharp
var fbW = (int)MathF.Round(Width * DpiScale);   // OpenGlRenderedCanvas.cs:188-191
glViewport(0, 0, fbW, fbH);
```

With `Width` still equal to the window's pixel width, setting `DpiScale = 1.5` produces a viewport
1.5× the real framebuffer: the UI zooms *and* the right and bottom edges fall off the window. The
missing piece is `GuiWindowHost.cs:50-51`, which hardwires logical size to OS window size:

```csharp
root.Width  = Window.Width;
root.Height = Window.Height;
```

**But `Window.Width` is the wrong numerator.** It is *screen points*, not pixels. On macOS a Retina
window is 800 points wide against a 1600px framebuffer with `backingScaleFactor = 2`, so
`glfwGetWindowContentScale` returns 2 there and `800 / 2 = 400` — the plan as first written would have
halved the UI on every Mac while fixing Windows. The two coincide only where the backing ratio is 1,
which is exactly the platform the plan was written on.

Deriving from the framebuffer instead is correct on both:

```
logical = framebufferPixels / S        DpiScale = S
```

macOS Retina at `uiScale = 1`: `S = 2 × 1 = 2`, logical = `1600 / 2` = 800, `DpiScale = 2` — identical
to today's behaviour, which was already right. Windows at 150%: `S = 1.5`, logical = `1500 / 1.5` =
1000, `DpiScale = 1.5`. Windows at 150% with `uiScale = 1.25`: `S = 1.875`, logical = 800.

This also disposes of a rounding problem the previous draft had to work around. Because
`deviceSize = logical × DpiScale = (framebuffer / S) × S`, the canvas's device size *is* the
framebuffer by construction — no separate device dimensions to plumb, and `round(Width * DpiScale)` at
`OpenGlRenderedCanvas.cs:188` (`glViewport`) and `:73` (`ReadFramebufferRgba`) agree with the real
framebuffer to within the one pixel that integer logical size can cost. Feed `Resize` the framebuffer
size and let it store the logical quotient; the two sites keep working.

`GuiWindowHost` therefore needs the framebuffer size, which it has no access to today — `OnResize`
carries screen points. Add `FramebufferWidth`/`FramebufferHeight` to `IWindow` (both backends already
call `Glfw.GetFramebufferSize` internally) and drive the host from `OnFramebufferResize` as well as
`OnResize`.

## Modules

- **`IWindow.ContentScale`** (new, `ZGF.Desktop`) — the OS-reported scale, distinct from the existing
  `DpiScale`. `OpenGlWindow` reads `Glfw.GetWindowContentScale`; `MetalWindow` keeps
  `backingScaleFactor`. `GlfwWindowBase` registers `Glfw.SetWindowContentScaleCallback` and
  recomputes. This is the **static** `Glfw` API (`Glfw.cs:191`, `Glfw.cs:237`) — `NativeWindow` is a
  wrapper class `ZGF.Desktop` does not use. The callback delegate needs a field to root it, like
  every other callback in that constructor, or the GC collects it and the app dies on a monitor drag.
  X and Y scales can differ; take X, matching `ComputeDpiScale`'s existing convention.
  **`Hint.ScaleToMonitor` stays unset** — GLFW would then resize windows by content scale itself and
  double-scale against `S`.
- **`IUiScale`** (new, `ZGF.Gui.Desktop`) — the user factor as an observable, so a change fans out to
  every live window. Backed by `State<float>` in `AppServices`, persisted like Theme and Language.
- **`WindowSpace`** (new, `ZGF.Geometry` types + `ZGF.Gui.Desktop` converter) — the single conversion
  point described above. Build this **first**: every other module below is expressed in terms of it,
  and the two bugs this plan has already had were both "which space is this number in".
- **`GuiWindowHost`** — owns the `WindowSpace` for its window. `SetRoot` and `HandleResize` take their
  size from `space.CanvasSize`; `RefreshDpiScale` becomes a `RefreshScale` that rebuilds the space
  from `Window.ContentScale * uiScale` and re-sizes the root, because a scale change without a
  relayout is a no-op.
- **`DesktopInputSystem`** — **no arithmetic change**, only a re-typing. `WindowToGuiCoords` (line
  519) already divides by `window/canvas`, so the event path (line 355) and the capture path (line
  221) follow `S` for free; delete it and call `space.ToCanvas(windowPoint)` instead. The remaining
  `GetCursorPosition` sites stay entirely in **Window** space and must be **left alone**:
  `IsCursorInsideWindow` (line 140) against `_window.Width` plus `GetFrameSize`, the hover bounds test
  (line 285) against `_window.Width`, and the driven-pointer threshold (lines 47/56) against a stored
  physical point. Dividing any of these by `S` — as an earlier draft of this plan said to —
  double-scales the cursor; once they are typed `WindowPoint`, it stops compiling instead.
  `CanvasToScreen` (line 117) delegates to `IWindowCoordinates` and needs nothing.
- **`PopupWindowFactory`** — the one place where logical and screen sizes must genuinely diverge:

  ```csharp
  popup.Resize(rect.Width, rect.Height);          // logical  — line 88
  popup.Window.SetSize(rect.Width, rect.Height);  // screen px — line 89
  ```

  `root.MeasureWidth()` (line 82) is logical; `ResolveRect` (line 250) clamps against monitor work
  areas in screen pixels. The measure must be scaled up before it meets a monitor rect, and back
  down before it meets `popup.Resize`. Two decisions follow:

  - **`PopupRequest.Place` takes screen pixels.** It is handed the logical measure today
    (`IPopupWindowFactory.cs:25`) and composes it with a screen anchor (`ContextMenuManager.cs:85`)
    — a mix that only works while `S = 1`. Scale the measure up *inside the factory* before calling
    `Place`, and divide the resolved rect back down before `popup.Resize`. `ContextMenuManager`
    then stays wholly in screen space and needs **no edit**.
  - **Reorder the acquire path.** A popup's own `S` is not known until it is positioned — it may
    land on a monitor of a different content scale than the window that opened it. Today the order
    is measure, place, `Resize`, `SetSize`, `SetPosition`, `RefreshDpiScale` (line 96). It becomes
    place, `SetSize`/`SetPosition`, `RefreshDpiScale`, then `Resize` at the *final* scale. The
    measure itself uses the **anchor monitor's** scale (`Glfw.GetMonitorContentScale`, picked with
    the same anchor logic `GetMonitorWorkArea` already uses), not the opening window's.
- **`Preferences` / `PreferencesStore`** — `float? UiScale` on `FileShape`, parsed on load into a
  `UiScale` value type on `Preferences` whose only constructor snaps to the nearest rung and maps
  absent/`NaN`/out-of-range to 1.0. Storing a raw float in the file shape is deliberate: the warning
  from `file-browser.md` is that a parse throw inside `PreferencesStore.Load` hits the catch-all and
  returns `Preferences.Default`, **wiping every other preference** — which is exactly what an enum
  converter would do here. Snapping at the boundary means nothing downstream can be handed a 1.03 or
  a `NaN`, so `S = window.ContentScale * uiScale.Factor` has one shape everywhere.
- **`SettingsDialog`** — a third `SettingRow` in Appearance, dropdown, instant apply. The row label,
  its description and any non-numeric menu labels are new UI strings, so they go in **all seven**
  `Strings/*.json` (ar, en, es, ja, ko, ru, zh-Hans) or the source generator fails the build with
  LOC004. The rung labels themselves are numeric ("125%") and need no key.

## Phases

Each phase names its tests, per `terminal.md`'s discipline.

0. **Confirm the Windows process is DPI-aware.** One-line check before anything is built: log
   `Glfw.GetWindowContentScale` on the Windows box at 150%. A DPI-*unaware* process is lied to by
   Windows and gets 1.0 back regardless of the display setting, which would look exactly like the API
   being broken and would invalidate phase 5's premise. GLFW sets per-monitor-v2 awareness inside
   `glfwInit`, but this repo statically links a vendored GLFW and an app manifest can override it, so
   this is worth five minutes up front rather than a day of confusion at the end.
1. **The coordinate types and the converter.** `CanvasPoint`/`CanvasRect`/`CanvasSize`,
   `WindowPoint`/`WindowSize`, `ScreenPoint`/`ScreenRect`, `DeviceSize`, and `WindowSpace`. Port
   `WindowCoordinates.ToScreenPoints` and `DesktopInputSystem.WindowToGuiCoords` onto it and delete
   both. `IWindow.FramebufferWidth`/`Height`. **Pure refactor: `S` is still 1 and nothing moves on
   screen.** Tests: a canvas point round-trips through `ToScreen`/`ToCanvas` at scale 1, 1.5 and 2
   and on a Retina-shaped window (backing ratio 2); the Y flip is applied exactly once in each
   direction; a window whose origin is negative (a monitor left of the primary) still round-trips.
   Ship this and confirm the app is byte-identical before touching a scale.
2. **The factor, user side only.** `IUiScale`, `Preferences.UiScale`, `WindowSpace` built from
   `framebuffer / S`, `GuiWindowHost` sizing the root from `space.CanvasSize`. No OS content scale
   yet. Tests: canvas logical size for a given *framebuffer* size at 1.0/1.25/1.5/2.0; the canvas's
   device size equals the real framebuffer at every scale; a Retina-shaped window (800 points, 1600
   px, backing ratio 2) yields logical 800 at `uiScale = 1` — the macOS regression guard; a rounding
   test that logical size is never 0 for a tiny window.
3. **Input — verification, not arithmetic.** The conversion moved to `WindowSpace` in phase 1, so
   this phase proves nothing double-scales. Tests: a click at a known window point resolves to the
   expected canvas point at each scale; drag deltas scale; the hover poll's bounds test agrees with
   the click test (they read the same coordinate through different paths, one staying in Window
   space and one converting, and that asymmetry is correct); `IsCursorInsideWindow` still answers in
   Window space at 1.5.
4. **Popups and menus.** Moving `Place` to `ScreenRect`, the reordered acquire path,
   `ResolveRect` against real monitor rects, submenu anchoring. Tests: a menu whose logical size
   would overflow the monitor flips at 1.5 the same way it flips at 1.0; a submenu anchors to its
   parent item at a non-integral scale; a pooled popup reacquired after a scale change gets the new
   size, not the old one; a menu measured against an anchor monitor whose scale differs from the
   opening window's is sized for the anchor monitor.
5. **Settings UI.** The dropdown, instant apply, persistence, clamp-on-load. Tests: an out-of-range
   or `NaN` persisted value clamps rather than throwing (assert the *other* preferences survive).
6. **The OS content scale.** `IWindow.ContentScale`, `glfwGetWindowContentScale`, the change callback,
   folding it into `S`. This is the phase that actually fixes Windows, and by now everything it needs
   already works. Tests: fake window reporting 1.5 yields the same canvas geometry as user scale 1.5;
   a fake Retina window (contentScale 2, backing ratio 2) yields the same canvas geometry as
   contentScale 1 with backing ratio 1 — the two ways of reporting "2" must not compound.
7. **Secondary and Review windows.** `SecondaryWindowFactory` re-sync on scale change, window size
   persistence (`Preferences.WindowWidth` etc. stay screen pixels — confirm the restore path doesn't
   round-trip them through logical), and the persisted *logical* positions that a scale change can
   strand: `AssistantPanelX`/`Y` are canvas coordinates, and the canvas shrinks as `S` rises, so a
   panel parked near the right edge at 1.0 is off-canvas at 2.0. Clamp on restore. The persisted
   logical *widths* — `RepoBarWidth`, `BranchesWidth`, `CommitDetailsWidth`, `FileBrowserWidth` — are
   scale-independent by construction and are correct untouched; note that here so nobody "fixes"
   them later.

**Phases 1–5 are verifiable on either platform.** The user factor and the OS factor multiply into the
same `S`, so setting UI scale to 1.5 anywhere exercises the identical path that fixes Windows. Only
phase 0 and phase 6 need the Windows box specifically — but the Retina-shaped test cases in phases 1,
2 and 6 are what keep macOS from regressing, and they run headless anywhere.

## Open questions

- **Non-integral scales and 1px borders.** `BorderSizeStyle.All(1)` at 1.5 is 1.5 device pixels. The
  glyph path already rounds in device space; the rect path may not. Needs a look at whether borders
  should snap to whole device pixels, and whether that's a canvas concern or a style concern.
- **What is the default?** `1.0 × osContentScale` is the honest default and will change how the app
  looks for every existing Windows user the moment phase 5 lands. That's the fix working as intended,
  but it is a visible change on upgrade and may deserve a release note.
- **Minimum window size.** Window size limits are in screen pixels; at 2.0 the logical area halves,
  and a layout with a large `MinWidth` could overflow a small window. Unclear whether any current
  layout is close to that bound.
- **Rounding the logical canvas.** Logical size is an `int`, so 1000px at 1.5 becomes 667 and the
  effective mapping is 1.4993 rather than 1.5 — glyphs baked at 1.5 get resampled by 0.05%, which is
  invisible. The alternative is a float-dimensioned canvas, which is a much larger change for
  nothing. Recording the choice so it isn't rediscovered as a bug.

## Risks

- **The two ways a platform can report "2".** macOS delivers scale by making screen points bigger
  than pixels (backing ratio 2, content scale 2, points-per-logical 1); Windows delivers it by
  putting more pixels in a point (backing ratio 1, content scale 1.5, points-per-logical 1.5).
  `glfwGetWindowContentScale` returns a number in both cases and they mean operationally different
  things. Deriving logical size from the framebuffer is what makes one formula correct on both — but
  it is the trap this plan fell into twice, so the Retina-shaped fixture is not optional.
- **Popup placement is the historically fiddly code.** It is where the pointer-ownership and pooled
  window-taint bugs came from, and phase 3 edits its arithmetic. The mitigation is that anchors go
  through `IWindowCoordinates` and are already correct — only *sizes* are being touched — but this is
  the phase to be slow in.
- **A pooled popup outliving a scale change.** Popup windows are pooled and reacquired. A scale change
  while one is cached leaves a window whose canvas and OS size disagree. `RefreshDpiScale` is already
  called on every acquire (`PopupWindowFactory.cs:96`); it now has to re-size too.
- **Screenshot pixels stop matching click coordinates.** Narrower than it first looks:
  `GuiDriver.ClickTool` injects `x`/`y` directly into the input system as *logical* canvas points, and
  `gui_snapshot` reports logical view positions, so the flow `SKILL.md` actually documents — click by
  coordinates because virtualized rows expose no views — keeps working untouched. What breaks is
  reading a coordinate off `gui_screenshot`, whose pixels are device. One note in `SKILL.md` covers
  it. Worth having `WindowSnapshot` carry the surface's scale too, since it already reports window
  bounds in screen pixels next to view positions in logical points and nothing says which is which.
- **Fractional scales on multi-monitor.** Dragging between a 1.0 and a 1.5 monitor fires
  `ContentScaleChanged` mid-frame. The popup path already handles per-monitor DPI for text; the main
  window has never had to re-layout for it.
- **A popup measured on one monitor and shown on another.** Phase 3's reorder makes the popup's
  final scale authoritative, but the measure that decided its size happened before placement. The
  anchor-monitor scale is the right input and the anchor is already how `GetMonitorWorkArea` picks a
  monitor, so the two agree in every case except a menu clamped across a monitor boundary — where it
  was already being clamped and the size is already a compromise.
- **Atlas growth.** Every distinct scale bakes a new set of sized font variants and they are not
  evicted. Harmless when someone picks a scale once; worth a glance if scale ever becomes a live
  slider rather than a ladder.
