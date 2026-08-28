---
name: theme-styles-mix-is-bankers-rounding
description: ThemeStyles.Mix rounds half-to-even (Math.Round), so it is not a drop-in for any colour blend whose tests pin half-up rounding
metadata:
  type: project
---

`ThemeStyles.Mix` (in `GitBench/Theming/ThemeStyles.cs`) interpolates with `Math.Round(double)`, which is banker's rounding: `Math.Round(8.5) == 8`. Any new colour blend whose expected values were computed as half-away-from-zero must do its own integer `(a + b + 1) / 2` and must not be "simplified" onto `Mix`.

**Why:** `TerminalPalette`'s Dim blend has two frozen tests that turn on a `.5` channel (`DimRoundsEachChannelUpAtTheHalfway` expects `0xFF09121B`; `Mix` yields `0xFF08121A`). Rounding down repeatedly is also the wrong direction for a dim: it creeps darker.

**How to apply:** When a theming task asks for a midpoint/blend, check whether the expected constants were derived half-up before reaching for the house helper. A code comment at the local helper is warranted, or the next reader consolidates it onto `Mix`.

Related: [[terminal-theming-flows-through-buildstyles]]
