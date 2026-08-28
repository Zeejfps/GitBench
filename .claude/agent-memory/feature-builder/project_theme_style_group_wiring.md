---
name: theme-style-group-wiring
description: Adding a new ThemeStyles style group always forces an edit to ThemeStyles.Build.cs, even when a brief scopes you to Dark.cs/Light.cs
metadata:
  type: project
---

A new style group on `ThemeStyles` cannot be wired without touching `GitBench/Theming/ThemeStyles.Build.cs`, because `BuildStyles`' object initialiser must set every `required` member.

**Why:** `Dark.cs` and `Light.cs` do not assemble `ThemeStyles`. They only build per-theme *input palettes* (`DiffSyntaxPalette`, `CommitBadgePalette`, `AnsiColors`) and hand them to the shared `BuildStyles(...)`, which is the single place every `Build*(p, ...)` call lives. A `with`-expression in Dark/Light cannot satisfy a `required` member — C# demands it in the object initialiser at construction.

So the pattern for a style group with hand-picked per-theme values is always four files plus Build.cs:
`ThemeStyles.<Group>.cs` (record + `Build<Group>`), the `required` property in `ThemeStyles.cs`, the literals in `Dark.cs`/`Light.cs`, and two lines in `Build.cs` (a parameter + an assignment).

**How to apply:** When a task brief lists the theming files to edit but omits `Build.cs`, that is an oversight in the brief, not a design alternative. Take the two-line Build.cs edit and flag it, rather than weakening the property to non-required to stay inside the letter of the file list. Derived groups that need nothing per-theme (`BuildRowSelection(p)`) still touch Build.cs, just without the extra parameter.
