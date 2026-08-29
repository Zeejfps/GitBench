---
name: input-seam-facts-gitbench
description: Load-bearing facts about GitBench/ZGF keyboard-input seams — dispatch order, KeyClaim semantics, the hover-built focus queue, and why "decline for the app" is a weak guarantee
metadata:
  type: project
---

Facts about `ZGF.Gui.Desktop.Input.InputSystem` that decide whether a controller seam is sound.
Verified 2026-08-28 while reviewing the terminal pane's keyboard.

**Dispatch order:** the focused component runs FIRST, with `Phase == Bubbling`; only then does the
hover-built `_focusQueue` run (Capturing forward, then Bubbling in reverse). A focused controller
that consumes therefore beats every app-level keybind with no framework change — and sees the
*Bubbling* phase, not Capturing.

**`KeyClaim` is tri-state and both non-None values stop propagation.** `Consume()` = `Command`
(propagation stops AND the OS text event for that key is suppressed, via `_keyClaim` checked in
`SendTextInputEvent`). `ConsumeAsText()` = `Text` (propagation stops, the character still arrives).
Declining leaves `None`. So "let the app have it" and "let the character through" are different
outcomes and a controller that confuses them deletes keystrokes.

**`_focusQueue` is rebuilt by hit-testing from the cursor**, not from registration, and `HitTest`
skips views failing the private `IsViewAndAncestorsVisible` *and* `IsPointInsideClippingAncestors`.

**Consequence — declining is not routing.** A key the focused controller declines reaches the app's
keybind layer only while the pointer is over some controller-bearing view. Move the pointer off the
window and every app keybind is unreachable regardless of focus. This is repo-wide, not specific to
any one pane; do not let a "reserved set" seam claim a guarantee it cannot make.

**Consequence — nothing blurs a focus holder when its view becomes invisible.** `RemoveInteractable`
blurs on *unregister*, but `Switch { KeepAlive = true }` only sets `IsVisible = false`, so a
keep-alive pane keeps the keyboard. Today each controller must walk `View.Parent` itself. The
framework-level fix (blur on visibility loss) would retire that check for every pane at once.

Related: [[seam-conventions-gitbench]], [[recurring-seam-mistakes-gitbench]]
