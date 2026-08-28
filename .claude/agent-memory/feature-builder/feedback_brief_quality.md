---
name: brief-quality
description: What made subagent briefs work in this repo — verbatim criteria, handed-over traps, and pre-decided open questions
metadata:
  type: feedback
---

Briefs that produced usable work first time shared three things beyond quoting the acceptance criteria verbatim.

**Why:** Subagents share none of the parent's context, and the expensive failures are not misunderstandings of *what* to build — they are re-deriving a decision the parent already made, or walking into a trap the parent already knew about. Both are invisible from inside the subagent.

**How to apply:**

- **Specify discriminating test data, not just the rule.** Saying "Dim mixes toward the background the cell is drawn on, not the theme's default" is weak; saying it and noting that one definition yields `0xFF6EA055` where the other yields `0xFF6C4231` makes the test verifiable by the parent in a way that does not depend on trusting the writer. Every ordering constraint should come with "here is what a transposition would produce".
- **Hand over known traps explicitly.** Telling the implementer that `ThemeStyles.Mix` uses `Math.Round` (banker's rounding, `Math.Round(8.5) == 8`) and would fail the round-half-up test saved a debug cycle it could not have shortcut on its own.
- **Check whether a stated ordering is actually observable before pinning it.** "A test must fail if any two of Inverse/Dim/Hidden are reordered" is not automatically satisfiable — if Dim blends toward the *cell's* resolved background, then Dim-then-Hidden and Hidden-then-Dim produce identical output and that pair is unpinnable. Defining Dim against the *theme's* background instead makes all three pairs observable. Work this out in Phase 0; discovering it in review costs a whole round.
- **Pre-decide the open questions the writer will otherwise return.** Rounding mode, visibility, and where a value lives are all parent decisions. Left unstated they come back as open questions and cost a round trip.
- **Name the concurrent agent's files in every brief.** Multiple agents in one working tree is normal here; each brief should say which paths belong to someone else and that build errors originating there are to be reported, not fixed.
