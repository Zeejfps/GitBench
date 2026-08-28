---
name: verifying-red-suites
description: How to verify a tests-first (RED) suite in GitBench — throwaway reference impl in the scratchpad, isolated test project when another agent breaks GitBench.Tests, and the zero-alloc measurement pattern that actually holds
metadata:
  type: feedback
---

A RED suite is a draft until you have seen it go green against *something*. Three techniques, all
validated on `TerminalRowRunsTests` (2026-08-28).

**Prove the suite is satisfiable with a throwaway reference implementation.** Write the tests and a
`NotImplementedException` stub in the repo, then in the scratchpad create a tiny test project that
compiles the *same test file* (`<Compile Include="G:\...\XTests.cs" />`, `EnableDefaultCompileItems=false`)
against a local implementation and only the leaf project references it needs. All-green there means
the tests are internally consistent and not over-constrained; all-red in the repo with every failure
tracing to the stub means the RED state is honest. Nothing in the repo changes.

**Why:** without it you cannot distinguish "not implemented yet" from "these assertions contradict
each other", and the implementer inherits the contradiction.

**How to apply:** any tests-first task where you hand a suite to another agent.

**Compiling around a broken shared test project.** `GitBench.Tests` is one project; a concurrent
agent's half-landed test file (tests committed, production stub not yet) makes the whole project
fail to compile, so you cannot run yours. Fix: a scratchpad project with
`<AssemblyName>GitBench.Tests</AssemblyName>` — `InternalsVisibleTo("GitBench.Tests")` matches by
simple assembly name, so the isolated project still sees `internal` types. Don't touch their file
and don't wait on them.

**Zero-allocation tests are reliable here if written this way.** Put the measured loop in a
`[MethodImpl(MethodImplOptions.NoInlining)]` static helper, call it once to warm (JIT + OSR), then
call it again between two `GC.GetAllocatedBytesForCurrentThread()` reads and assert the delta is
exactly `0L`. Measured 0 bytes across 5 consecutive runs in Debug. Warming a loop that lives in the
test method itself is *not* enough — the measured loop must be the same compiled code as the warm-up.
Precedent for looser allocation budgets: `framework/JpegSharp.Tests/AllocationTests.cs`.

Related: [[flaky-timing-tests]] — this is the one class of perf-shaped test that has held up.
