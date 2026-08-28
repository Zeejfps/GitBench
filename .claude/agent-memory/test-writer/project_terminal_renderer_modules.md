---
name: terminal-renderer-modules
description: The terminal renderer is being built as parallel modules behind a frozen RunStyle/ICellStyler seam — what each module owns and what a test suite for one must not touch
metadata:
  type: project
---

The terminal *renderer* (the app side of `GitBench.Terminal.Vt`) is decomposed into modules built by
concurrent agents in the same worktree, all meeting at one frozen seam in
`GitBench/Features/Terminal/RunStyle.cs`:

- `RunStyle` (packed ARGB fg/bg + Bold/Italic/Underline/StrikeThrough) and `ICellStyler` are
  **frozen**. Inverse/Dim/Hidden are spent before a cell becomes a `RunStyle`; Blink has no
  representation because nothing drives a blink clock.
- Module B — colour resolution: `TerminalPalette : ICellStyler`, constructed from a `TerminalStyles`
  theme value. Pure, no `ThemeService`, no DI; a theme change makes a new instance.
- Module C — run coalescing (`TerminalRowRuns*`): decides when two neighbouring cells look the same,
  and knows nothing about how either colour was decided.

**Why:** the seam exists so a run-splitting test can state its styles outright instead of standing
up a theme, and so the two modules can be written in parallel without merge conflicts.

**How to apply:** when writing tests for one terminal renderer module, stay inside its own test file
and do not read-modify a sibling module's files — another agent is usually editing them. Test the
styler through `ICellStyler`, not the concrete type, so the seam stays the thing under test.

Related: [[terminal-engine-seam-spec]], [[feedback-engine-agnostic-specs]]
