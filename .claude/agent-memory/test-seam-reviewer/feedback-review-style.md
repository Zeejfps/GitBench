---
name: feedback-review-style
description: How this user runs seam reviews — applies findings then asks for a verification round, wants "closed" stated plainly, and wants decisive facts flagged even when they wear a taste label
metadata:
  type: feedback
---

Report findings only; the user applies them. Never edit test files unless asked explicitly.

**Say "this is closed" when it is closed, rather than manufacturing a finding.** The user asks for
verification rounds ("round 2") on seams they already fixed, and states outright that they would
rather hear a finding was closed than read a padded one.

**Why:** they check applications themselves and want the review's signal-to-noise preserved. A
manufactured finding costs them the trust to skim the real ones.

**How to apply:** in a verification round, answer each prior finding as applied / half-applied /
not-applied with the evidence, and lead with whichever is genuinely open. When a prior finding
survives in a changed form, say which class of bug the fix DID close before naming the residue.

**Flag decisive technical facts that are wearing a taste label.** The user asked for this
explicitly after a round-1 finding they had discounted as style turned out to be load-bearing.
Label real taste findings as taste, and never soften a fact into one.

**They will go further than a proposal when the reasoning holds, and will keep a stated requirement
that is currently behaviourally redundant** (repo hotkeys reserved by the terminal pane, even though
digits are declined anyway) rather than silently dropping it. Do not re-raise redundancy as a
finding once they have said this.

**Churn arguments get counted, not asserted** — see [[recurring-seam-mistakes-gitbench]] item 6.

Related: [[seam-conventions-gitbench]], [[input-seam-facts-gitbench]]
