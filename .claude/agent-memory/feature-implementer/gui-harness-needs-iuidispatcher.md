---
name: gui-harness-needs-iuidispatcher
description: GuiTestHarness registers InputSystem/IFrameTicker/IContextMenuHost/SvgImageCache but never IUiDispatcher — any widget calling ctx.Require<IUiDispatcher>() dies in a harness that forgets it
metadata:
  type: project
---

`ZGF.Gui.Testing.GuiTestHarness.Create` seeds the context with `InputSystem`, `IFrameTicker`,
`SvgImageCache` and `IContextMenuHost` only. `Context.Require<T>` can construct a concrete class but
never an interface, so a widget whose `CreateView` calls `ctx.Require<IUiDispatcher>()` throws
"IUiDispatcher is not registered in Context and is not a constructible class" at
`GuiTestHarness.Create` time (the root `Mount()` inside it).

**Why:** every existing harness test that builds such a widget adds
`ctx.AddService<IUiDispatcher>(new QueuedDispatcher())` in its own `configure`; the terminal pane
wiring tests did not, and no implementation change can rescue them without inventing a fallback
dispatcher that would run pty reader callbacks off the UI thread in production.

**How to apply:** when a widget-building test fails on a missing service in `Require`, check the
harness `configure` block before suspecting the widget. Flag it as a test defect rather than
softening the `Require` to a `Get` with a fabricated default.
