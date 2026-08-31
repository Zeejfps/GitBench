---
name: test-global-state-hazards
description: Process-global state that GitBench.Tests mutates while xUnit runs other collections in parallel — known latent cross-collection races, not yet fixed
metadata:
  type: project
---

xUnit runs one collection per core, so anything process-global that one test writes is visible to every
other test running at that instant. Three such writes exist in `GitBench.Tests` and none is contained by
the type system — each is held together by a convention.

**Environment variables.** `AssistantProviderTests.ClearingASavedKeyFallsBackToTheProvidersEnvironmentVariable`
sets a real provider variable (e.g. `OPENAI_API_KEY`) process-wide for the duration of the test;
`AssistantProviderSwitchTimingTests`' constructor clears *all* provider variables and restores them in
`Dispose`. Both collide with each other and with any concurrent test that constructs
`AssistantCredentials`, which reads `Environment.GetEnvironmentVariable` ambiently
(`GitBench/Features/Assistant/AssistantCredentials.cs`, `FromEnvironment`).
`Terminal/ShellCommandTests.ShellVariable` does the same to `SHELL`, which `ShellCommand.UnixShell` and
`GitProcessRunner` both read — a live hazard on the macOS/Linux CI legs, where it can misdirect an
unrelated test's real `git` invocation.

The repo already has the right shape for this one file over: `AppPaths.ResolveRoot` takes a
`Func<string, string?> readEnvVar` instead of reading the environment itself. Applying that to
`AssistantCredentials` is the contained fix; it changes a production constructor, so it was reported
rather than done.

**`DiffOptions`.** Three `public static bool` fields
(`SyntaxHighlightingEnabled`, `IntraLineHighlightingEnabled`, `StructureEnabled`).
`DiffHunkHeaderTests.StructureDisabledProducesNoOutlines` flips `StructureEnabled` to false and back in a
`finally`. This has already been patched once by hand: `AssistantReadToolsTests` and
`AssistantReviewToolsTests` carry comments saying they were put in `CodeIntelCollection` *specifically*
so they cannot run while that flip is in effect. That is an invariant enforced by collection membership
and a comment — the next test that reaches `DiffAnnotationCoordinator` or `FileContentLoader` from
outside the collection reopens it.

**How to apply:** when a test fails with a *wrong value* rather than a timeout, suspect these before
suspecting the test. Timeouts on round 5s/10s boundaries are a different problem — see
[[threadpool-starvation-flakes]].
