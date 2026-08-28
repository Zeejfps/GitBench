---
name: recurring-seam-mistakes-gitbench
description: The seam mistakes worth checking first in GitBench test suites — safe-accessor-plus-escape-hatch, AC coverage gaps on the common case, and which "no allocation" tests are actually deterministic
metadata:
  type: project
---

Check these first on any GitBench seam review; each was a real finding, not a hypothetical.

**1. The safe accessor shipped alongside the escape hatch it was meant to replace.** A type whose
`<remarks>` claims it makes a slicing/pairing bug unrepresentable, while also exposing the raw
buffer as a property, has re-opened the exact bug — usually because the tests found the raw property
convenient to assert on. Ask: does any *non-test* caller need it? Found 2026-08-28 on
`TerminalRowRuns` (`CodePoints` alongside `CodePointsOf(run)`).

**Why:** the property exists to serve assertions, and the caller's own output buffer is almost
always the better thing for the test to read.

**2. Example-based suites pin the exotic case and skip the common one.** Check AC coverage in both
directions, and specifically look for the *ordinary* path being covered only by a test that asserts
a different output field. Found 2026-08-28: three wide-cell tests, none asserting that a
same-styled wide leader+trailer produces ONE run — so a defensive "start a new run at every
WideLeader" implementation passed all 17 tests.

**How to apply:** for any AC phrased as a universal ("runs are total and maximal"), ask for one
invariant helper asserted in every test, not more examples.

**3. `GC.GetAllocatedBytesForCurrentThread()` delta tests are structurally deterministic here — do
not lump them in with the repo's flaky timing tests.** The counter is exact per thread, xunit runs a
synchronous test body on one thread, and a genuinely allocation-free implementation has a
*structurally* zero delta (no boxing, no closures, no arrays), not a statistically small one. Tiered
compilation does not change managed allocation counts for the same IL.

**Why:** [[flaky-timing-tests]] in the user's auto-memory makes the team rightly suspicious of
"performance tests", but this one has a different failure mode — it fails iff the AC is violated.

Related: [[seam-conventions-gitbench]]
