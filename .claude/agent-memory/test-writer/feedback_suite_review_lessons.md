---
name: suite-review-lessons
description: Adjudicated seam-review corrections to a tests-first suite — surviving-variant tests, invariants that supplement examples, no naked buffer beside a safe accessor, ParamName as content, and labelling a non-flaky perf test
metadata:
  type: feedback
---

Five rules the user adopted after reviewing the `TerminalRowRuns` RED suite (2026-08-28). All were
things the suite got *nearly* right, so they generalise.

**For any "these two things belong together" rule, write the case where the pair sits inside a
longer sequence.** Every wide-cell row in the first draft was exactly 2 cells long, so
`if (WideLeader) startNewRun()` passed all 17 tests and would have drawn CJK one glyph per run.
**Why:** a test only kills the implementations it can distinguish; a 2-element example cannot
distinguish "merge" from "always split". **How to apply:** enumerate the surviving cheap-and-wrong
implementations before declaring a suite done, and add the test that kills each.

**Invariant helpers supplement the per-test expectation, never replace it.** A shared
`AssertTotalAndMaximal(columns, split)` called from every successful split pins the generic AC;
each test still states the concrete runs it expects. **Why:** a suite of pure invariants tells the
implementer nothing about the shape they are meant to build.

**Do not expose a naked buffer next to the accessor that makes slicing safe.** A `CodePoints`
property beside `CodePointsOf(run)` contradicts the doc claim that mis-slicing is unrepresentable,
and gives a caller a whole-row span that compiles into a per-run draw call. Deleting it also leaves
exactly ONE statement of how far a row extends (`Runs`), which keeps future trimming additive.
**How to apply:** if a doc comment claims a mistake is unrepresentable, delete the member that
represents it — and be honest in the doc about the hole that remains, rather than adding a type to
close it.

**`ParamName` is content, not over-specification, when two parameters share one constraint.** Two
buffers both required to be >= row length: the parameter name is the only thing separating the two
failures. Assert it. (Over-specification would be asserting the message text.)

**A perf-shaped test that genuinely cannot flake needs a comment saying why.** This repo has real
flaky timing tests, and the next reader will otherwise quarantine a sound one by pattern-match.
State the reason: nothing to allocate structurally, an exact per-thread counter, a synchronous body.

Related: [[verifying-red-suites]], [[flaky-timing-tests]], [[no-lookup-escape-hatches]]
