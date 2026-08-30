---
name: technique-internals-probe
description: Drive GitBench internal types outside xUnit by building a scratchpad console app named GitBench.Tests — the fastest way to prove a threading/race diagnosis
metadata:
  type: reference
---

`GitBench.csproj` declares `<InternalsVisibleTo Include="GitBench.Tests" />` with **no strong name**. So any
assembly you compile with `<AssemblyName>GitBench.Tests</AssemblyName>` gets full access to GitBench internals
(`TerminalInstance`, `TerminalSession`, `AppKeybindController`, …).

**Why this matters:** when a bug is a race or a state-machine ordering problem, a scratchpad console app that
runs the real production types in a tight loop (thousands of trials, timestamped state-transition logs, cold
vs. warm process) gives you a causal proof in minutes. Inside xUnit the same question costs a full-suite run
per data point and you cannot instrument without editing the repo — which matters when the task is
diagnose-only or the working tree has uncommitted work.

Recipe (all inside the scratchpad, nothing written to the repo):
1. `dotnet build GitBench.Tests/GitBench.Tests.csproj --artifacts-path <scratchpad>/art` once, to get a
   populated `<scratchpad>/art/bin/GitBench/debug/` with every dependency.
2. New console csproj with `AssemblyName` = `GitBench.Tests`, and one `<Reference><HintPath>` per `*.dll` in
   that directory (`<Private>false</Private>`).
3. Build to a separate artifacts path, then copy all the dlls from step 1 next to the probe's output and
   **delete the probe's `GitBench.Tests.deps.json`** — otherwise the host refuses to resolve ZGF.Gui etc.
4. `cd <probe out> && dotnet GitBench.Tests.dll <args>`.

Gotchas: no `ImplicitUsings`, so add `using System;` yourself. `IUiDispatcher` / `IReadable<T>` live in
namespace `ZGF.Observable` inside `ZGF.Gui.dll`; `IReadable<T>` exposes `Subscribe(Action<T>)`, not an event.
For pure "what does this static return here?" questions, a plain reflection probe against the same dll dir
(with an `AssemblyResolve` hook) is enough and needs no assembly-name trick.

Related: [[known-failing-tests-macos]]
