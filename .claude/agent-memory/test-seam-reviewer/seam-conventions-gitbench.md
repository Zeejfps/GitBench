---
name: seam-conventions-gitbench
description: Load-bearing seam conventions in GitBench — internal-by-default in Features/**, Outcome only for user-facing failures, doc <remarks> that argue against the rejected alternative
metadata:
  type: project
---

Conventions that are settled in this repo and should not be re-litigated in a seam review.

**`GitBench/Features/**` types are `internal` by default** (~537 internal vs ~109 public), and
`GitBench/GitBench.csproj` carries `<InternalsVisibleTo Include="GitBench.Tests" />` (and
`GitBench.Automation`).

**Why:** the app assembly has no external consumers; `public` in `Features/**` is the anomaly, not
the norm.

**How to apply:** a test suite exercising an `internal` type through `InternalsVisibleTo` is NOT the
"tests reach past the public surface" finding — it is the house seam. Only flag it if the tests use
reflection or subclass hooks. Conversely, a `public` type under `Features/**` deserves a question.
Noted 2026-08-28 while reviewing `TerminalRowRuns`: `RunStyle.cs` is `public` and is the outlier.

**Error model is not uniform, and that is deliberate.** `Outcome` is for git/user-facing operation
failures. Contract violations on a hot pure path (a caller-owned buffer too short, a row index off
the grid) throw — `ITerminalGrid.CopyRow` throws `ArgumentOutOfRangeException`. Do not propose
`Outcome` for programmer-error paths.

**Doc `<remarks>` argue against the rejected alternative.** `ITerminalGrid`, `RunStyle`,
`DrawGlyphRunInputs` and `CellWidth` all justify their shape by naming the design they did not pick
("Cells are copied out rather than handed over as a span, because…"). That is house style, not
over-documentation — but it makes an *overclaimed* guarantee in `<remarks>` a real finding, since
the doc is read as the contract. See [[recurring-seam-mistakes-gitbench]].

Related: [[recurring-seam-mistakes-gitbench]]
