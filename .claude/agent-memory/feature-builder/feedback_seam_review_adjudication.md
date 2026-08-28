---
name: seam-review-adjudication
description: How to adjudicate test-seam-reviewer findings in this repo — verify precedent claims, and never rename or delete something the user named
metadata:
  type: feedback
---

Adjudicate seam-review findings on two separate axes: **is the argument sound**, and **is the change mine to make**. They come apart often.

**Why:** The reviewer argues from repo precedent, and its precedent claims are checkable in one grep — but it also proposes changes that are good engineering and still outside a delegated agent's authority, because the user named the thing being changed. Relaying either kind unexamined is a failure: the first because "never relay an unverified claim", the second because renaming a deliverable out from under the user is not a technical decision.

**How to apply:**

- **Verify every precedent claim before accepting it.** They have held up so far (e.g. "zero indexers in `Theming/` or `Features/`", `DiffRowPainter.SlotColor` as the house total-switch pattern, `SyntaxHighlighter` being `internal sealed`) — each was one grep, and each was accurate. Cheap, so do it every time.
- **Accept findings that dissolve a premise rather than trade against it.** The strongest finding in the terminal-palette review showed the "indexer-with-throw vs duplicated-switch-with-throw" trade was false — a `switch` over `byte` with arms `0..15`, `< 232`, `_` is exhaustive with *no* throwing arm. That takes rule-1 escape hatches to zero instead of relocating one. Findings shaped like "your trade-off has a third option" outrank findings shaped like "I'd prefer X".
- **Reject renames and field deletions the user specified**, however good the case. Carry them to the user as follow-ups with the reviewer's evidence attached. `TerminalPalette` (the user's name) is genuinely inconsistent with the repo's six-for-six `*Palette` = bag-of-`uint`s convention — that is a real finding and still not mine to act on unilaterally.
- **Watch for findings that are decisive technical facts wearing a "taste" label.** The reviewer filed "keep tolerate-and-force for opacity" as taste, but its reasoning was load-bearing: `with` uses the compiler-generated copy constructor and does *not* re-run primary-constructor validation, so constructor validation would be silently bypassed by the suite's own `X with { ... }` fixtures. Read the taste section for these.
