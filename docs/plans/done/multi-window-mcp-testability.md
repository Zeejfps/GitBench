# Multi-window GUI testability for the MCP server

## Problem

The in-process MCP server (`GuiMcpServer`) lets an agent drive the live app, but it is
**single-window** in a **multi-window** app. Context menus, tooltips, and secondary windows are
real native OS windows the server can't see, so an agent can neither inspect nor click a context
menu — screenshots and snapshots only ever show the main window.

Root cause is one line:

```csharp
// GuiApp.cs:257
new GuiMcpServer(() => _mainHost.Root, _mainInput, _dispatcher, CaptureScreenshot);
```

Every tool inherits that single-window assumption:

- `gui_snapshot` walks only `_mainHost.Root` (`GuiMcpServer.Snapshot`, line 127)
- `gui_screenshot` redraws only `_app.MainWindow` (`GuiApp.CaptureScreenshot`, line 250)
- `gui_click` injects only into `_mainInput` (`GuiMcpServer.InjectClick`, line 203)

Fixing screenshots alone is insufficient: a context-menu item lives in the **popup's own**
`DesktopInputSystem` (`PopupWindowFactory.cs:269`), so even if the agent could *see* the menu,
`gui_click` would still inject into `_mainInput` and miss it.

## Key fact the fix rests on

Every window — main, popup, secondary — is a uniform `GuiWindowHost`
(`Window`, `Canvas`, `Input` (`DesktopInputSystem`), `Context`, `Root` — `GuiWindowHost.cs`).
`GuiApp` already references all three owners: `_mainHost`, `_popupFactory` (`_activePopups`),
`_secondaryWindows` (`_active`). The server just needs to iterate them instead of capturing one
root.

A view's `Position` is in **its own window's canvas space** — the same space
`InputSystem.SendMouseMovedEvent` expects (`DesktopInputSystem.HandleMouseButtonEvent:186-187`).
So a target found in a popup root needs **no coordinate translation**, only injection into that
popup's `Input`.

---

## Scope: items 1–3 (the MCP change)

### New type — `framework/ZGF.Gui.Desktop/GuiSurface.cs`

```csharp
public sealed record GuiSurface(
    string Role,                 // "main" | "context-menu" | "tooltip" | "secondary"
    IWindow Window,
    View? Root,
    DesktopInputSystem Input);   // the window's own input system — the routing key
```

No `IWindowCoordinates`: screen bounds come from `IWindow.GetPosition` + `Width`/`Height`; targets
need no translation (see above).

### Step 1 — Expose the popup/secondary hosts (mechanical accessors)

**`PopupWindowFactory.cs`**
- `PopupWindowFactory`: add `internal IReadOnlyList<PopupWindowImpl> ActivePopups => _activePopups;`
- `PopupWindowImpl`: add `internal DesktopInputSystem Input => _host.Input;`
  (`Root`, `Window`, `MousePassThrough` already exposed, lines 305-309)
- Role: in this codebase the only non-pass-through popup is a context menu
  (`ContextMenuManager.ShowContextMenu` → `MousePassThrough = false`; tooltips → `true`).
  So `MousePassThrough ? "tooltip" : "context-menu"`. Keeps the surface list decoupled from
  `ContextMenuManager`.

**`SecondaryWindowFactory.cs`**
- `SecondaryWindowFactory`: add `internal IReadOnlyList<SecondaryWindowImpl> Active => _active;`
- `SecondaryWindowImpl`: add `internal DesktopInputSystem Input => _host.Input;` and
  `internal View? Root => _host.Root;` (`Window` already public, line 107)

### Step 2 — `GuiApp` collects surfaces + per-window screenshot

**Surface collection** (rebuilt each call, so it always reflects live popups; pooled/released
popups simply drop out next call):

```csharp
private IReadOnlyList<GuiSurface> CollectSurfaces()
{
    var list = new List<GuiSurface> { new("main", _app.MainWindow, _mainHost.Root, _mainInput) };
    foreach (var s in _secondaryWindows.Active)
        list.Add(new GuiSurface("secondary", s.Window, s.Root, s.Input));
    foreach (var p in _popupFactory.ActivePopups)
        list.Add(new GuiSurface(p.MousePassThrough ? "tooltip" : "context-menu", p.Window, p.Root, p.Input));
    return list;
}
```

**Per-window screenshot.** The shared `GlRenderBackend` already reads back *whichever window renders
next* against *its own* canvas (`GlRenderBackend.cs:47` — the closure captures each window's
`canvas`). Today only main captures because `CaptureScreenshot` calls
`_app.MainWindow.RequestRedraw()`. Add a window-targeted variant that renders **synchronously** to
dodge the shared `_pendingScreenshotPath` field race:

```csharp
private void CaptureWindowScreenshot(IWindow window, string path, Action? done)
{
    _renderBackend.RequestScreenshot(path, done);
    window.MakeContextCurrent();
    window.RenderNow();                 // consumes the pending path against THIS window's canvas
    if (!ReferenceEquals(window, _app.MainWindow))
        _app.MakeMainContextCurrent();  // restore main GL context (cf. SecondaryWindowFactory.Update:86)
}
```

Synchronous `RenderNow` is an established pattern (`PopupWindowFactory.cs:87-88`); because it runs
inside the server's `_dispatcher.Post` (UI thread), `done` fires inline with no race against other
windows' animation redraws.

**Wiring:** `new GuiMcpServer(CollectSurfaces, _dispatcher, CaptureWindowScreenshot);`
Drop the `_mainInput` ctor param — input now arrives via surfaces.

### Step 3 — Make `GuiMcpServer` surface-aware

Ctor: `Func<IReadOnlyList<GuiSurface>> getSurfaces`, `IUiDispatcher`,
`Action<IWindow,string,Action?> captureScreenshot`.

**`gui_snapshot` → window forest.** Build one `UiSnapshot` per surface (reusing
`SnapshotBuilder.Build(root, surface.Input.InputSystem)` verbatim), wrap each with a header line:

```
=== window: main [0,0 1280x800] focused ===
ContainerView [0,0 1280x800]
  ...
=== window: context-menu [430,250 190x120] ===
ContextMenu [0,0 190x120]
  ContextMenuItem role=menuitem "Fetch" [...]
  ContextMenuItem role=menuitem "Pull" [...]
```

Add `Inspection/MultiWindowSnapshot.cs`:
`WindowSnapshot(string Role, RectI ScreenBounds, bool Focused, UiSnapshot Content)` +
`MultiWindowSnapshot(IReadOnlyList<WindowSnapshot> Windows)` with `Render(bool asJson)` over the
existing `UiSnapshot.ToText()`/`ToJson()` (`UiSnapshot.cs:44,53`). A typed pair (not string-concat
in the server) so the headless track can reuse it. Update the tool description to say it lists *all*
live windows.

**`gui_click` → resolve across surfaces, inject into the owner.** Replace single-root `ResolveView`
(line 243) with a topmost-first walk (reverse of the collected list → popups before secondary before
main), skipping `tooltip` (pass-through windows take no clicks):

```csharp
private (GuiSurface Surface, View View)? ResolveAcross(string? id, string? label, string? text, bool exact)
{
    var surfaces = _getSurfaces();
    for (var i = surfaces.Count - 1; i >= 0; i--)        // topmost (last-opened popup) first
    {
        var s = surfaces[i];
        if (s.Role == "tooltip" || s.Root is not { } root) continue;
        if (ResolveView(root, id, label, text, exact) is { } v) return (s, v);
    }
    return null;
}
```

`InjectClick` (line 203): add a `DesktopInputSystem input` parameter, pass the resolved surface's
`Input`. `view.Position.Center` is already correct in that window's canvas space. For the absolute
`x`/`y` path, add an optional `window` arg (role or snapshot index; default `main`) selecting which
surface's input/canvas space the coordinate targets. Report the window in the result:
`clicked context-menu #fetch at (…)`.

**`gui_type` / `gui_key` → focused surface.** Both target `_input` (main) today; route to
`surfaces.LastOrDefault(s => s.Window.IsFocused)` ?? main so menu keyboard nav
(`ContextMenuKbmController`) works. On macOS an open menu popup *is* the key window — relied on by
`GuiApp.HandleMainFocusChanged:118`.

**`gui_screenshot` → optional `window` arg.** Resolve target surface (default `main`, or the topmost
`context-menu` when one is open — the useful default for "show me the menu"), call
`captureScreenshot(surface.Window, path, done)`. Same temp-file / `ManualResetEventSlim` plumbing as
today (lines 189-201).

### Gotchas (each grounded in the code)

- **Coordinate space**: popup view `Position` is correct *only* when injected into the popup's
  `Input`, never `_mainInput`. This is why "just fix screenshots" fails.
- **Screenshot race**: `_pendingScreenshotPath` is a single shared backend field; synchronous
  `RenderNow` of the target window makes per-window capture deterministic.
- **GL context restore**: after capturing a non-main window, restore the main context
  (`_app.MakeMainContextCurrent()`) — same reason as `SecondaryWindowFactory.Update:82-86`.
- **Modality/topmost**: reverse iteration matches the arbiter z-order (submenu registers after
  parent, `PopupWindowFactory.cs:96`), so "click Fetch" hits the menu item, not a main-window
  namesake.
- **Pooled popups**: surfaces collected fresh per call, so a released popup
  (`PopupWindowFactory.Release`) drops out next call — no stale refs.

### Files touched

| File | Change |
|---|---|
| `GuiSurface.cs` | **new** — projection record |
| `Inspection/MultiWindowSnapshot.cs` | **new** — `WindowSnapshot` + forest render over `UiSnapshot` |
| `GuiMcpServer.cs` | surface-aware snapshot/click/type/key/screenshot; `_getRoot`→`_getSurfaces` |
| `GuiApp.cs` | `CollectSurfaces()`, `CaptureWindowScreenshot()`, updated ctor call |
| `PopupWindowFactory.cs` | `ActivePopups` + `PopupWindowImpl.Input` |
| `SecondaryWindowFactory.cs` | `Active` + `SecondaryWindowImpl.Input`/`Root` |

All under the `framework` submodule.

### Build & verify

- Compile: `dotnet build GitBench\GitBench.csproj --artifacts-path <scratchpad>` (pulls in the
  framework projects; isolated outputs).
- Live, via the MCP tools in-session: right-click a repo row (`gui_click button:right`),
  `gui_snapshot` → confirm a second `=== window: context-menu ===` block with the items;
  `gui_click` a menu item by label → confirm the action fires; `gui_screenshot window:context-menu`
  → confirm the menu's pixels return.

---

## Follow-up (separate change): headless context menus in `GuiTestHarness`

**Finding:** `GuiTestHarness` is single-window by construction — one `_root`, one `InputSystem`, one
`Mouse`, and a bare `Context()` with **no `IContextMenuHost` registered**
(`GuiTestHarness.Create:73-89`). So `ShowContextMenu` can't even be called in a harness test today;
menus get no headless coverage.

The seam already exists: `IContextMenuHost`'s own comment says *"other platforms can host menus as
in-canvas overlays"* (`IContextMenuHost.cs:31`). A `HeadlessContextMenuHost : IContextMenuHost`
would build the menu against an in-memory context (its own `InputSystem`) and track it as a **second
root** the harness can snapshot/click — no OS window. The new `MultiWindowSnapshot` type from Step 3
is what the harness would render those extra roots with.

This is **not free** from the MCP change (it needs the fake host + multi-root support in the
harness), but the MCP change supplies the rendering type it would build on. Recommend doing it as a
fast-follow once Steps 1–3 land, so menu behaviour gets deterministic CI coverage alongside the
live agent path.

### Effort

Steps 1–2 mechanical; Step 3 is the substance (~2 new small files + edits to 4). Harness follow-up
is a separate, comparable-sized change.
