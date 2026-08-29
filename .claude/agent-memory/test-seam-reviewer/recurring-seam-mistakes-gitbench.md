---
name: recurring-seam-mistakes-gitbench
description: The seam mistakes worth checking first in GitBench test suites — safe-accessor-plus-escape-hatch, AC coverage gaps on the common case, untested translation tables, mode-blind predicates, and which "no allocation" tests are actually deterministic
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

**3. The exhaustively-tested pure table beside the untested translation into it.** When a suite
sweeps `Enum.GetValues<TDomain>()` on a pure encoder, check whether anything sweeps the
`TFramework -> TDomain` map that feeds it. Found 2026-08-28 on `TerminalKeyEncoder` /
`TerminalInputController`: the encoder table was pinned key-by-key (all 26 Ctrl letters proved
distinct), while the private `KeyboardKey -> TerminalKey` switch inside the controller was exercised
on ~20 of 55 keys — DownArrow, End, Insert, PageDown, F2-F4, F6-F11 and Ctrl+D all absent, so an
implementation mapping only the named keys passed all 352 tests.

**Why:** the domain enum is visible to a sweep; the framework enum is not, because the map is a
private switch with no seam.

**How to apply:** ask for the map to be its own named `internal static class`, and for one totality
test (`every TDomain except None has at least one TFramework that produces it`).

**3b. Surjectivity + injectivity do NOT pin a translation table - a permutation satisfies both.**
Found 2026-08-28 (round 2 of the same review): after `TerminalKeyMap` was extracted with an
"every TerminalKey is reachable" sweep and an "only Enter collides" sweep, an implementation with
End/Insert/PageDown three-cycled, F6-F10 rotated and the untested letters deranged still passed all
390 tests - because surjective + injective over a finite set IS bijective, and a bijection is
exactly what a copy-paste slip preserves.

**How to apply:** the only sweep that pins a table is one that asserts each arm's *destination*:
identity-on-names (`TerminalKey.ToString() == KeyboardKey.ToString().Replace("Arrow","").Replace("Numpad","")`)
or the full expected dictionary compared with `Assert.Equal`. Keep the surjectivity sweep beside it
(it catches a missing arm, which the name rule cannot see); the injectivity sweep is then redundant.
Also check which enum members are pinned end-to-end elsewhere before believing a spot-check theory
covers the table - here only 8 of 55 arms were named.

**4. A predicate whose signature cannot express the modes the surrounding design says it is
structured for.** Found 2026-08-28: `TerminalKeyEncoder.Encode` took `TerminalModes` and its sibling
`ProducesText(key, modifiers)` did not, with a `<remarks>` asserting "no terminal mode decides
whether a key types" — false under kitty CSI-u / `modifyOtherKeys`, the exact protocols the class
doc said it was structured for.

**How to apply:** when two functions must agree, check they take the same inputs. If one takes less,
either it is genuinely mode-free forever or the split is a future signature break.

**5. `GC.GetAllocatedBytesForCurrentThread()` delta tests are structurally deterministic here — do
not lump them in with the repo's flaky timing tests.** The counter is exact per thread, xunit runs a
synchronous test body on one thread, and a genuinely allocation-free implementation has a
*structurally* zero delta (no boxing, no closures, no arrays), not a statistically small one. Tiered
compilation does not change managed allocation counts for the same IL.

**Why:** [[flaky-timing-tests]] in the user's auto-memory makes the team rightly suspicious of
"performance tests", but this one has a different failure mode — it fails iff the AC is violated.

**6. "Churn" objections to a signature change are usually counting `[InlineData]` rows, not call
sites.** In this repo the theory rows almost always go through a private `Encoded(...)`/`Length(...)`
helper, so a return-shape change costs the helper count, not the row count. Found 2026-08-28: a
proposed tri-state return was rejected as "~220 test cases" when the real cost was 15 call sites.

**How to apply:** grep `Type.Member(` in the test files before accepting a churn argument.

Related: [[seam-conventions-gitbench]], [[input-seam-facts-gitbench]]
