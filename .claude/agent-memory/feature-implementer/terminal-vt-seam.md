---
name: terminal-vt-seam
description: ZGF.Terminal.Vt engine seam — the settled shape, the XtermSharp adapter's build constraints, and the two places the agreed surface did not survive contact
metadata:
  type: project
---

The terminal engine seam (`ZGF.Terminal.Vt`) is settled: `ITerminalGrid.CopyRow(int, Span<TerminalCell>)`
as the primitive with `Cell()`/`RowText()` as extensions, `FeedResult` carrying `Response` directly
(no `DrainResponse`), `CellWidth` as `Single | WideLeader | WideTrailer`, `TerminalCell.Combining`
for grapheme clusters, `TerminalColor` as `Default | Indexed | Rgb`, `TerminalState` as one atomic
snapshot, `TerminalSetup(Size, ScrollbackLines)`.

**Why:** two agents designed it independently and the disagreements were resolved deliberately;
re-litigating any of it discards that review.

**How to apply:** implement against it as written. Two places where it did not survive contact with
XtermSharp, both reported rather than changed:

- Wrap direction. The seam first said "this row overflowed and continues on the next"; that has a
  permanently unanswerable case (the bottom viewport row is not known to have wrapped until the next
  rune scrolls it), so it is now `bool ContinuesPreviousRow(int row)` — "this row continues the one
  above". Always answerable, and the direction every xterm.js-derived engine already stores
  (`BufferLine.IsWrapped` is set on the continuation row, `InputHandler.cs:1307`), so the adapter is
  a direct read. The golden header key is `continues`, not `wrapped`.
- `TerminalSetup.ScrollbackLines` cannot be honoured: `TerminalOptions.Scrollback` is get-only and
  fixed at 1000 by its constructor. Nothing in the suite pins it, so it is silently ignored.

See [[xtermsharp-vendoring]] for the build constraints and [[golden-files-no-update-switch]] for how
the corpus goldens are maintained.
