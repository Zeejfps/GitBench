---
name: terminal-engine-seam-spec
description: Findings from designing ITerminalEngine/grid-surface tests and running them against vendored XtermSharp — where XtermSharp fails, and what the seam design pressure was
metadata:
  type: project
---

A behavioural spec suite for `ZGF.Terminal.Vt` (terminal plan Modules 2 and 3) was built in a
scratchpad and run against a vendored XtermSharp adapter: 202 tests, 158 pass, 44 fail.

**Why:** the terminal plan vendors XtermSharp behind an `ITerminalEngine` seam. Before Phase 2
commits to it, the question is whether the seam is right and how far off XtermSharp is.

**How to apply:** if asked to test or design the terminal engine seam, these are the settled
conclusions — do not re-derive them.

Seam shapes that survived contact with real bytes:
- `Feed(ReadOnlySpan<byte>)` **returns** `TerminalUpdate { DamageSpan Damage, ReadOnlyMemory<byte>
  Response }`. Returning DA/DSR replies beats raising them as events: no fake delegate in tests, no
  "did I subscribe in time" question, and the reply is naturally scoped to the feed that caused it.
- `TerminalColor` must be a tagged union of Default | Indexed | Rgb. Any packed-int attribute
  (XtermSharp uses 9 bits per colour) makes truecolor unrepresentable, and truecolor is the single
  most-used styling sequence in the recorded `claude` corpus.
- One immutable `TerminalState` snapshot beats a dozen properties on the engine — same reason repo
  view models subscribe to one tuple slice.
- Grid rows are viewport-relative with **negative indices for scrollback**, and the engine holds no
  scroll position (that is the renderer's).
- A cell holds a grapheme cluster, not a rune: `Rune` plus an optional `Combining` string.

XtermSharp's fatal gap: `Terminal.MatchColor` is `throw new NotImplementedException()`, so
`SGR 38;2;r;g;b` takes the engine down. Also wrong (not merely absent): `CSI u` dispatches on the
final byte alone so kitty flag push/pop moves the cursor; `CSI > 4;2 m` is parsed as SGR; OSC loses
a byte at every `Feed` boundary. Absent: `?2026`, cursor shape, kitty flags, wide characters.
Full table with file/line in the scratchpad `engine-spec/KnownGaps.md`.

Build note: the vendored sources do not compile on net10.0 — `NStack.Core` declares `System.Rune`,
which collides with `System.Text.Rune`. Keep the vendored project on `netstandard2.0`.

Related: [[feedback-engine-agnostic-specs]]
