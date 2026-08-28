---
name: xtermsharp-gap-estimates-overcount
description: The per-gap "tests that will go green" counts in docs/xtermsharp-known-gaps.md are estimates, and three claude CorpusPropertiesSpec assertions contradict the recorded bytes
metadata:
  type: project
---

The "Failing:" list under each gap in `docs/xtermsharp-known-gaps.md` is a projection, not a
measurement. Gap 1 (truecolor) was projected to turn 16 tests green; fixing it turned exactly 14.

**Why:** three `CorpusPropertiesSpec` cases assert things the claude corpus's own bytes rule out,
and no engine fix can reach them:

- `Corpus_SetsTheWindowTitle(claude)` wants a non-empty title, but the last OSC in the stream is
  `ESC ] 0 ; BEL` at byte 5531 — an empty payload. A correct terminal ends that session untitled.
- `Claude_PaintsColouredTextOntoTheGrid` wants a visible RGB foreground after the whole corpus, but
  byte 5231 leaves the alternate screen and the bytes after it erase all 34 rows of the normal one.
  The RGB cells are real; they only exist in mid-stream frames.
- `Claude_EndsWithMouseTrackingReportedFromTheLastRequest` wants SGR encoding, but the last `?1006`
  is the reset at byte 5244. (This one might still be reachable through gap 4.)

**How to apply:** before chasing a gap's projected test count, replay the relevant corpus tail
(`python -c` over `Corpus/<name>.bin` with a regex for the sequence) and check the assertion against
the bytes. Report a shortfall as a finding about the test rather than implementing around it — and
never widen the fix to hit a number. See [[xtermsharp-vendoring]].
