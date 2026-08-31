---
name: threadpool-starvation-flakes
description: The dominant cause of nondeterministic GitBench.Tests failures — thread-pool starvation in the test host, recognisable by failures landing exactly on a 5s/10s deadline
metadata:
  type: project
---

Suite-wide flakiness in `GitBench.Tests` that shows as "different tests fail each run, all pass in
isolation" is almost always **thread-pool starvation in the test host**, not a bug in the tests that failed.

**Mechanism:** production hands every background job to the ambient pool (`Task.Run` in
`AsyncCommand.Execute` and `ViewModelBase.RunBackground/RunMutation/TryRunBackground`, ~38 sites). xUnit
runs one collection per core, and many tests deliberately park a pool thread to hold a gate open
(`GitReadGateStoreTests.ConcurrencyProbeGate` parks 3 for up to 5s; `MutationRunnerTests` blocks the work
delegate on a `ManualResetEventSlim`; `ScriptedRemoteGitService` waits 30s). The pool's floor is
`ProcessorCount` and it grows past that by hill-climbing at roughly a thread per second, so a job issued
while those threads are parked sits queued for seconds — and whichever unrelated test is waiting on it
with a wall-clock deadline loses.

**The tell:** the failure duration equals the deadline exactly — 5 s or 10 s, never 3 s or 7 s. If the
durations are round like that across unrelated classes, stop reading the tests and look at the pool.

**Measured on 24 cores, HEAD 5b7d298:** peak 139 queued work items, pool queue non-empty on 193 of the
first 200 samples (25 ms apart). Baseline 4/2/9/5/9 failures over five consecutive runs; with the floor
raised, 0 failures over six, and the queue backlog fell from ~28% of samples to ~3%.

**Hypotheses this killed, so they need not be re-chased:** `SyntaxHighlighter`'s 750 ms whole-file budget
never fires — instrumented over a full run both with the floor raised and with it back at ProcessorCount
(which did still flake), zero trips. `GuiTestHarness.Advance` is a virtual *frame* clock and cannot help:
the flaky waits are on real background work (pty reader threads, `Task.Run` continuations), not on timers.

**Fix in place:** `<ThreadPoolMinThreads>256</ThreadPoolMinThreads>` in `GitBench.Tests.csproj`. The SDK
maps it to `System.Threading.ThreadPool.MinThreads` in `runtimeconfig.json` and the vstest host honours it
(verified). Prefer this over calling `ThreadPool.SetMinThreads` — declarative host configuration rather
than mutating a global at runtime, and it applies on CI unchanged.

**What it does not fix:** the waits are still wall-clock. The structural fix — injecting a background-work
seam so tests run background jobs deterministically — was written up for the user, not done.

**Diagnosis recipe that worked:** a `[ModuleInitializer]` in the test assembly, gated on an env var,
spawning a sampler thread that records `ThreadPool.PendingWorkItemCount` / `ThreadCount` to a file
(`Console.WriteLine` is swallowed by `dotnet test`). Then re-run with the floor raised and compare. Two
data points — backlog before/after and failures before/after — settle it in about ten minutes.

Related: [[known-failing-tests-macos]], [[technique-internals-probe]], [[test-global-state-hazards]]
