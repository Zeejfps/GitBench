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
| The factor | One number, `S = osContentScale × userUiScale`, owned by the window and pushed into the canvas. Not two scales threaded separately — every consumer wants the product. |
| Logical size | `canvas.Width = windowPixels / S`. The viewport stays at native pixels. This is the whole change; everything else is following it through. |
| User scale values | A discrete ladder — 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 — not a free slider. A dropdown in Settings → Appearance, matching Theme and Language. Free text invites 1.03 and the rounding artefacts that come with it. |
| Live or restart | **Live.** Font variants are keyed by device pixel size (`GetSizedVariant`), so a scale change bakes new glyphs rather than corrupting old ones. A restart-required setting would be a self-inflicted limitation. |
| OS scale on Windows | `glfwGetWindowContentScale`, plus the `ContentScaleChanged` callback for monitor-to-monitor drags. |
| OS scale on macOS | Unchanged. `MetalWindow.ComputeDpiScale` already reads `backingScaleFactor` and is correct. |
| Persistence | One `float UiScale` on `Preferences`, clamped on load. The OS content scale is never persisted — it's a property of the monitor, read fresh. |
| Automation | `GuiDriver` keeps reporting **logical** coordinates. They stop equalling screen pixels; the `verify` skill's coordinate clicking has to be re-based (see Risks). |
| Out of scope | Per-monitor scale for a window straddling two displays; per-window scale; text-only zoom (`Cmd +/-` on the diff alone). |

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

## Modules

- **`IWindow.ContentScale`** (new, `ZGF.Desktop`) — the OS-reported scale, distinct from the existing
  `DpiScale`. `OpenGlWindow` reads `glfwGetWindowContentScale`; `MetalWindow` keeps
  `backingScaleFactor`. `GlfwWindowBase` subscribes `ContentScaleChanged` and recomputes.
- **`IUiScale`** (new, `ZGF.Gui.Desktop`) — the user factor as an observable, so a change fans out to
  every live window. Backed by `State<float>` in `AppServices`, persisted like Theme and Language.
- **`GuiWindowHost`** — `SetRoot` and `HandleResize` divide by `S`; `RefreshDpiScale` becomes
  `Canvas.UpdateDpiScale(Window.ContentScale * uiScale)` and also re-sizes the root, because a scale
  change without a relayout is a no-op.
- **`DesktopInputSystem`** — divide cursor coordinates by `S`. Six `_window.GetCursorPosition` call
  sites (lines 47, 56, 140, 221, 285, 355), all in this one file, covering the event path, the
  polled hover path and the capture path. `CanvasToScreen` (line 117) already delegates to
  `IWindowCoordinates` and needs nothing.
- **`PopupWindowFactory`** — the one place where logical and screen sizes must genuinely diverge:

  ```csharp
  popup.Resize(rect.Width, rect.Height);          // logical  — line 88
  popup.Window.SetSize(rect.Width, rect.Height);  // screen px — line 89
  ```

  `root.MeasureWidth()` (line 82) is logical; `ResolveRect` (line 250) clamps against monitor work
  areas in screen pixels. The measure must be scaled up before it meets a monitor rect, and back
  down before it meets `popup.Resize`.
- **`Preferences` / `PreferencesStore`** — `float UiScale { get; init; } = 1f`, clamped to the ladder
  on load. Note the warning from `file-browser.md`: a parse throw inside `PreferencesStore.Load`
  hits the catch-all and returns `Preferences.Default`, **wiping every other preference**. A float is
  safer than an enum here, but clamp rather than validate-and-throw.
- **`SettingsDialog`** — a third `SettingRow` in Appearance, dropdown, instant apply.

## Phases

Each phase names its tests, per `terminal.md`'s discipline.

1. **The factor, user side only.** `IUiScale`, `Preferences.UiScale`, `GuiWindowHost` dividing by it,
   viewport at native pixels. No OS content scale yet. Tests: canvas logical size for a given window
   size at 1.0/1.25/1.5/2.0; viewport equals framebuffer at every scale; a rounding test that logical
   size is never 0 for a tiny window.
2. **Input.** Cursor division in `DesktopInputSystem`. Tests: a click at a known device pixel resolves
   to the expected logical point at each scale; drag deltas scale; the hover poll's bounds test agrees
   with the click test (they read the same coords through different paths today).
3. **Popups and menus.** The measure-to-screen conversion, `ResolveRect` against real monitor rects,
   submenu anchoring. Tests: a menu whose logical size would overflow the monitor flips at 1.5 the
   same way it flips at 1.0; a submenu anchors to its parent item at a non-integral scale; a pooled
   popup reacquired after a scale change gets the new size, not the old one.
4. **Settings UI.** The dropdown, instant apply, persistence, clamp-on-load. Tests: an out-of-range
   or `NaN` persisted value clamps rather than throwing (assert the *other* preferences survive).
5. **The OS content scale.** `IWindow.ContentScale`, `glfwGetWindowContentScale`, the change callback,
   folding it into `S`. This is the phase that actually fixes Windows, and by now everything it needs
   already works. Tests: fake window reporting 1.5 yields the same canvas geometry as user scale 1.5.
6. **Secondary and Review windows.** `SecondaryWindowFactory` re-sync on scale change, window size
   persistence (`Preferences.WindowWidth` etc. stay screen pixels — confirm the restore path doesn't
   round-trip them through logical).

**Phases 1–4 are verifiable on macOS.** The user factor and the OS factor multiply into the same `S`,
so setting UI scale to 1.5 here exercises the identical path that fixes Windows. The Windows machine
is only needed for phase 5.

## Open questions

- **Does the ladder move the window, or the content inside it?** Scaling content in a fixed window
  means less fits on screen — correct, and what every editor does. Worth confirming that's the intent
  rather than "make the window bigger too."
- **Non-integral scales and 1px borders.** `BorderSizeStyle.All(1)` at 1.5 is 1.5 device pixels. The
  glyph path already rounds in device space; the rect path may not. Needs a look at whether borders
  should snap to whole device pixels, and whether that's a canvas concern or a style concern.
- **What is the default?** `1.0 × osContentScale` is the honest default and will change how the app
  looks for every existing Windows user the moment phase 5 lands. That's the fix working as intended,
  but it is a visible change on upgrade and may deserve a release note.
- **Minimum window size.** Window size limits are in screen pixels; at 2.0 the logical area halves,
  and a layout with a large `MinWidth` could overflow a small window. Unclear whether any current
  layout is close to that bound.
- **Should the automation driver report logical or physical?** Logical keeps `GuiDriver` consistent
  with the view tree it dumps; physical keeps coordinate clicks stable against screenshots.

## Risks

- **Popup placement is the historically fiddly code.** It is where the pointer-ownership and pooled
  window-taint bugs came from, and phase 3 edits its arithmetic. The mitigation is that anchors go
  through `IWindowCoordinates` and are already correct — only *sizes* are being touched — but this is
  the phase to be slow in.
- **A pooled popup outliving a scale change.** Popup windows are pooled and reacquired. A scale change
  while one is cached leaves a window whose canvas and OS size disagree. `RefreshDpiScale` is already
  called on every acquire (`PopupWindowFactory.cs:96`); it now has to re-size too.
- **The `verify` skill breaks silently.** `SKILL.md` documents bottom-up-Y coordinate clicking against
  a 1:1 space. At any scale ≠ 1 those coordinates land somewhere else, and the failure looks like "the
  click did nothing" rather than an error. Update the skill in the same change as phase 2.
- **Fractional scales on multi-monitor.** Dragging between a 1.0 and a 1.5 monitor fires
  `ContentScaleChanged` mid-frame. The popup path already handles per-monitor DPI for text; the main
  window has never had to re-layout for it.
- **Atlas growth.** Every distinct scale bakes a new set of sized font variants and they are not
  evicted. Harmless when someone picks a scale once; worth a glance if scale ever becomes a live
  slider rather than a ladder.
