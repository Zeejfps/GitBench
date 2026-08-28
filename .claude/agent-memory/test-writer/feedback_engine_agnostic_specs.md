---
name: feedback-engine-agnostic-specs
description: When tests exist to design a seam, they bind only to the seam and assert correct behaviour — a red test documenting a real gap beats a green one blessing the implementation
metadata:
  type: feedback
---

When a suite's purpose is to pin a **seam** rather than to cover an existing implementation:

1. Tests bind only to the proposed interface. Exactly one file names a concrete implementation
   (`EngineUnderTest.Create(...)`), so swapping the backing implementation is a one-line edit.
2. Assertions encode correct behaviour from the spec, never observed output from the current
   implementation. Never write an expectation by running the implementation and blessing the result.
3. Where the implementation diverges, the test stays red and the divergence is recorded in a
   separate document — not marked up inside the suite, which would bind the spec to one engine.
4. The implementation's pass rate is a secondary finding. Lead the report with what the exercise
   taught you about the seam.

**Why:** the user gave this as a mid-task correction while I was building the terminal-engine spec
suite. The reasoning: baking an implementation's bugs into assertions makes them permanent, and a
suite that needs editing to accept a different correct implementation is testing the wrong thing.

**How to apply:** whenever the brief is "design a seam and prove it is implementable", or a
candidate library is being evaluated behind an interface. Does not apply to backfilling coverage on
code that is already the intended implementation.

Related: [[terminal-engine-seam-spec]]
