---
name: three-lens-merge
description: What the three-writer merge actually caught on the terminal keyboard feature, and why running one writer would have shipped two real bugs
metadata:
  type: feedback
---

Run all three lenses. On the terminal-keyboard feature the contract lane produced a clean, thorough, 127-case suite that would have been accepted on its own — and both genuine defects came from the other two lanes.

**Why:** The contract lane tests the specification as written. It cannot find a defect *in* the specification, because it is reading the same words. The edges and seams lanes are pointed at what the spec does not say, which is where both failures lived.

**How to apply:**

- **What the edges lane found that nothing else could:** the encoder's `int Encode(...)` return had `0` meaning both "typeable, let the character through" and "no encoding for this key". The controller's only sane translation of the first (`ConsumeAsText()`) applied to the second *deletes* the keystroke — no character ever follows a Ctrl chord, so `Ctrl+/`, `Ctrl+[`, `Ctrl+0` reach neither the shell nor `AppKeybindController`. The contract lane's tests all pass with that bug present. Fix was a second pure predicate, `ProducesText(key, modifiers)`.
- **What the seams lane found:** my own acceptance criteria contradicted themselves. Criterion A7 required the encoder to emit `CSI 15~` for F5; criterion B7 reserved F5 for the app. Net effect: F5 permanently unreachable from the pane, so htop/mc/vim F5 bindings dead with no escape hatch. **Generalise this check: any key named in both the "encode this" list and the "reserve this" list is a contradiction, and it is worth grepping for before the briefs go out.**
- **Resolve contradictions against the user's own words, not against the more detailed criterion.** The user's rule was "the terminal wins everything except a small reserved set (mode switching and repo hotkeys stay with the app)". F5 is a forced refresh — neither category — so it goes to the terminal. My B7 had added it without noticing it was outside the rule the user stated.
- **Ask each lane to prove satisfiability with a throwaway reference implementation, and to state what it pinned where the criteria were silent.** All three did; the edges lane's list of 18 silent-and-pinned decisions was the single most useful artifact of the phase, because each one is a ratify-or-overrule I could decide in seconds. Ask for the format explicitly: "criteria say nothing about X; I pinned Y; the alternative is Z."
- **Mutation evidence beats argument.** The edges lane proved modifier masking was load-bearing (not taste) by deleting it and showing 5 tests go red; the seams lane proved ancestor-visibility mattered by flipping the check to `_view.IsVisible` and showing exactly one test go red. Both findings were accepted on the strength of the mutation, not the prose. Ask for it.
- **Merge cost is real but bounded.** Three suites totalling ~2500 lines merged into 462 tests. Assembling by file surgery (copy the two verified-compiling scratchpad suites in as new files, then apply adjudications as targeted edits) was far cheaper and safer than transcribing, and kept each lane's verified-red state intact. The judgment work is the adjudications and the duplicate collapse, not the typing.

**The three lenses were still not enough, and the seam reviewer's surviving variant was.**

Ask the reviewer to "name an implementation that passes all N tests and is still wrong" in *both* rounds, not just the first. On this feature it earned its place twice:

- **Round 1** found that all three lanes together pressed only ~20 distinct `KeyboardKey` values. `DownArrow`, `End`, `Insert`, `PageDown`, `F2/F3/F6/F11`, `Space` and 18 of 26 letters were never pressed by any test, so a `KeyboardKey → TerminalKey` switch with 20 arms was green — shipping a shell with no history recall, no `Ctrl+D`, no `Ctrl+R`. The cause is worth generalising: introducing a domain enum (`TerminalKey`) to decouple a pure table from a framework API **moves the riskiest table out of the tested surface**, because the new table between framework and domain has no name and no home. Give it one.
- **Round 2 found the hole in my own fix.** I closed round 1 with surjectivity + injectivity sweeps over the map. Those two together characterise a *permutation*, which is exactly what a copy-paste slip preserves — a three-cycle over End/Insert/PageDown passes both. Coverage sweeps that count arms do not pin arms; assert each arm's destination (a name-derived rule over both enums does it in one test).

**Scrutinise the criterion you fill yourself as hard as the lanes' work.** Both of the above were in tests I wrote during the merge, not in any lane's output. My `TerminalPaneWiringTests` also shipped broken (no `IUiDispatcher` in the harness) and was only caught in phase 4 — the parent's own tests get no lane review and no second reading unless you ask for one.

**When a phase-4 finding splits across roles, split the fix.** The implementer's one questionable line (a `!session.Exited.IsCompleted` guard) existed only because two of *my* fixtures made a finished replay indistinguishable from an exited shell. I fixed the fixtures, then sent the guard back rather than editing production myself — which keeps phase 4 an independent check. It also confirmed the implementer's report: it had flagged the guard as the one thing it wanted a second opinion on.
