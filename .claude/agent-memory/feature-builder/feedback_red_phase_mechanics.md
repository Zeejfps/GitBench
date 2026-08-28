---
name: red-phase-mechanics
description: Two mechanics that made the tests-first pipeline work here — the compiling stub in a shared tree, and demanding a surviving-variant from the seam reviewer
metadata:
  type: feedback
---

Ask the test-writer for a **compiling stub alongside the tests**, and ask the seam reviewer for a **surviving variant**. Both are cheap to request in the brief and expensive to discover you needed.

**Why:** Two failure modes that phase 4 cannot recover from. A red suite that does not compile blocks every *other* agent in the tree, because this repo routinely has parallel agents in one working copy (see [[brief-quality]]). And a suite can be green, thorough-looking, and still admit a badly wrong implementation — reviewing tests by reading them does not surface that; only naming a concrete wrong implementation does.

**How to apply:**

- **Red phase means "throws", not "does not compile".** Have the test-writer commit the full API surface with XML docs and every body `throw new NotImplementedException();`. The tree keeps compiling for the concurrent agent, the tests are red for exactly one reason, and the implementer's job is unambiguous — fill bodies, change nothing else. Ask the writer to confirm red-for-one-reason, not just red.
- **Make the writer prove the suite is satisfiable.** A throwaway reference implementation in the scratchpad, run against the suite, before it hands over. It costs the writer minutes and rules out handing the implementer a self-contradictory contract. Nothing enters the repo.
- **Demand a surviving variant from the seam reviewer, in the brief.** Phrase it as: "name an implementation that passes all N tests and is still wrong." On `TerminalRowRuns` this produced the one genuine hole — every wide-cell test row was exactly 2 cells long, so `if (cell.Width is WideLeader) closeRun();` passed all 17 tests while drawing every CJK line one glyph per run. Reading the tests would not have found it. A finding of this shape outranks everything else in the review.
- **Cardinality-2 examples cannot distinguish "merge" from "always split".** The general form of the above: any test whose row/list contains exactly the two elements under test pins nothing about grouping. Wide pairs, adjacent duplicates, and boundary merges all need a third neighbouring element.
- **Universals stated in an acceptance criterion need an invariant helper, not six examples.** "Runs tile the row exactly once, no two neighbours share a style" was pinned only by hand-written cases until a small `AssertTotalAndMaximal` helper was called from every successful-split test. Have it supplement the concrete per-test assertions, never replace them — a suite of pure invariants tells the implementer nothing about the intended shape.
- **`ParamName` is content, not decoration, when two parameters share one constraint.** The writer filed it as over-specification; overrule that. With two output buffers both required to be `>= row.Length`, the parameter name is the only thing distinguishing the two failures.
