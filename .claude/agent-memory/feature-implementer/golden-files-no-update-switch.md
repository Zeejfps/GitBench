---
name: golden-files-no-update-switch
description: Terminal corpus goldens are audited against raw bytes and never regenerated from engine output — no --update flag, and blessed output has already slipped in once
metadata:
  type: feedback
---

A golden `.grid` file states what a correct terminal shows, worked out from the recorded bytes.
Never add an `--update`/`--accept` switch, and never copy an engine's `.actual` output across.

**Why:** a flag that rewrites goldens from engine output turns every bug into the specification the
first time someone runs the suite with it on. This is not hypothetical here — an audited
`smoke.grid` had already recorded the window title as `C:\WINDOWS\YSTEM32\cmd.exe` when the corpus
plainly says `SYSTEM32`; XtermSharp's `OscPut` drops the first byte of the payload run after a feed
boundary, and the third frame boundary lands on that `S`.

**How to apply:**
- Frame offsets are scanned from the corpus (`FramePoints`), never taken from
  `FeedResult.FramesCompleted` — one missed frame would otherwise shift every later frame and drown
  one real divergence in twenty false ones.
- When auditing a golden, check every value that a frame boundary could have cut: OSC payloads are
  the ones that bite, because CSI and UTF-8 resumption are fine in XtermSharp and OSC is not.
- `GoldenFile` writes `.actual` beside the golden on failure and strips `;`-prefixed provenance
  lines from the comparison. Record any correction or re-derivation in that block.
- Fields no engine can track (cursor blink, cursor shape) are engine output wherever they appear in
  a golden. Say so rather than churning the files.

See [[terminal-vt-seam]] and [[xtermsharp-vendoring]].
