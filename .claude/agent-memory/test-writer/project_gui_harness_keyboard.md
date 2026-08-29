---
name: gui-harness-keyboard
description: How to test keyboard controllers through GuiTestHarness — reading KeyClaim, building the focus queue, and observing "the app would have got this key"
metadata:
  type: project
---

Testing a `KeyboardMouseController` through `GuiTestHarness` needs three things the harness API does not hand you directly.

**Reading the claim.** `harness.PressKey/KeyDown` construct a `KeyboardKeyEvent` and discard it, so the `Command` / `Text` / `None` outcome is invisible. Build the event yourself and call the public `harness.Input.SendKeyboardKeyEvent(ref e)`, then read `e.Claim`. That is still the real dispatch path (focused-component first, then the focus queue) and it sets the internal `_keyClaim`, so a following `harness.SendText(rune)` is still dropped-or-delivered exactly as in the app.

**Building the focus queue.** `InputSystem._focusQueue` is populated by *hover*, not by registration: `harness.MoveTo(x, y)` hit-tests and rebuilds the path (ancestor-first = capture order). Without a mouse move the queue is empty and an unconsumed key reaches nobody. `StealFocus` alone sets the focused component but builds no path.

**Observing fall-through.** Register a recording spy controller on the view *before* the controller under test (registration order is capture order, and the app's real keybind controller sits on an ancestor, so it is genuinely earlier). Then: focused controller consumes → spy sees nothing; focused controller declines → spy records the key. A spy will see an unconsumed event twice (capture + bubble), so assert `Contains`, never a count.

Note a hidden view (`view.IsVisible = false`) is excluded from `HitTest`, so set it *after* the last mouse move or the queue empties out from under the test.

Related: [[verifying-red-suites]], [[escape-literals-in-sources]].
