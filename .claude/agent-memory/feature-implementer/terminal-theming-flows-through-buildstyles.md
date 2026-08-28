---
name: terminal-theming-flows-through-buildstyles
description: Adding a theme sub-palette means editing five files at once — the required prop, both builders, BuildStyles' parameter list and its initialiser
metadata:
  type: project
---

A new theme sub-palette (`AnsiColors`/`TerminalStyles` was the latest) is not one file. `BuildStyles` in `ThemeStyles.Build.cs` takes every hand-written palette as a parameter and sets every `required` member in one object initialiser, so adding a sub-palette forces, in the same batch: the `required` property in `ThemeStyles.cs`, a `Build<X>` in the new partial file, a `var` in both `BuildDark` and `BuildLight`, and both a new parameter and a new initialiser line in `ThemeStyles.Build.cs`. Miss any one and the build fails on the `required` member — it will not compile half-done.

**Why:** A task brief that scopes "one new file plus one property" is understating it; `BuildStyles` edits are forced by the type system, not optional scope creep.

**How to apply:** Batch all five edits before the first compile. Say so up front if a brief appears to forbid touching `ThemeStyles.Build.cs`.

The consumer-side lookup convention held up: no indexer on the palette record, and a single total `switch` over `byte` at the consumer (`TerminalPalette.Indexed`, mirroring `DiffRowPainter.SlotColor`) — zero rule-1 escape hatches, and the agreed surface survived contact unchanged.

Related: [[theme-styles-mix-is-bankers-rounding]]
