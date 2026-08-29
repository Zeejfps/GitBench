# Patches to the vendored XtermSharp

`vendor/XtermSharp/` began as a verbatim clone of upstream `XtermSharp/master`, last touched
December 2020. It is now a **fork**: the sources are patched in place, and this file is the record
of every divergence from that clone. Anything not listed here is still byte-identical to it.

Read this before `diff -r`-ing against a fresh clone — the diff is only legible if you know which
hunks were deliberate.

`XtermSharp.Vendored/XtermSharp.Vendored.csproj` is ours and always was; upstream's own
`XtermSharp.csproj` is kept beside the sources for fidelity with the clone and is not built.

---

## Patch 1 — 24-bit colour in the cell (gap 1)

Fixes gap 1 of `docs/xtermsharp-known-gaps.md`: `Terminal.MatchColor` was
`throw new NotImplementedException ()` and every `SGR 38;2;r;g;b` reached it, so the first truecolor
sequence killed the session. A real Claude Code session sends 47 of them.

The cell's appearance no longer fits in an int, so it stopped being one. `CharData.Attribute` was
`(flags << 18) | (fg << 9) | bg` — nine bits per colour, a 256-entry palette plus the sentinels 256
(default) and 257 (inverted default), with no room for 24 bits.

### New file: `CellAttribute.cs`

`CellColorKind` (`Default | InvertedDefault | Indexed | Rgb`), `CellColor` (kind plus three bytes)
and `CellAttribute` (`FLAGS` plus a foreground and a background `CellColor`). Both structs are
`IEquatable` with `==`, `!=` and `GetHashCode`, because attribute equality is load-bearing —
`BufferLine.HasContent` and `Terminal.Scroll` both used int equality on the packed value.

`InvertedDefault` is kept as its own kind rather than collapsed onto `Default`. Palette slot 257 was
distinct from 256 under the old encoding, so `CharData.InvertedAttr != CharData.DefaultAttr`, and
`BufferLine.HasContent` counts a cell filled with it as content. Collapsing the two would have
changed reflow's idea of where a reverse-video line ends.

### Changed files

| File | Change |
| --- | --- |
| `CharData.cs` | `Attribute` is `CellAttribute`, not `int`. `DefaultAttr` / `InvertedAttr` become `public static readonly CellAttribute` (they were `public const int`). Both constructors take a `CellAttribute`. |
| `Terminal.cs` | `CurAttr` is `CellAttribute`. `EraseAttr ()` returns one, built as `DefaultAttr.WithBackground (CurAttr.Background)` — the same thing `(DefaultAttr & ~0x1ff) \| (CurAttr & 0x1ff)` used to say. **`MatchColor` is deleted**, not implemented: resolving RGB to a palette slot at parse time destroys exactly what `GitBench.Terminal.Vt.TerminalColor` exists to preserve, since a palette colour follows the user's theme and a literal RGB must not. |
| `Buffer.cs` | `SavedAttr`, `GetBlankLine`, `SaveCursor`, `RestoreCursor` and `FillViewportRows` all move from `int` to `CellAttribute`. `SavedAttr` is split off its shared `int SavedX, SavedY` declaration. |
| `BufferSet.cs` | `ActivateAltBuffer (CellAttribute? fillAttr)`. |
| `CharacterAttribute.cs` | `ToSGR (CellAttribute)`. The two duplicated fg/bg blocks become one `ColorToSGR` helper that also emits `38;2;r;g;b` for an RGB colour and nothing for either default. The stale `// Temporary, longer term in Attribute we will add a proper encoding` comment is gone — this patch is that encoding. |
| `InputHandlers/InputHandler.cs` | `CharAttributes` carries `CellColor` values instead of nine-bit slots. `38`/`48` route to a new `ExtendedColor` helper that **stores** the RGB instead of calling `MatchColor`. |

### Behaviour that changed beyond the fix

- **Truncated `38`/`48` no longer throws.** Upstream indexed `pars [i + 1]` and `pars [i + 2]`
  unguarded, so `CSI 38m`, `CSI 38;2m` and `CSI 38;2;1m` were `IndexOutOfRangeException`s waiting
  behind the `NotImplementedException`. `ExtendedColor` bounds-checks, leaves the colour unchanged,
  and consumes the rest of the parameter list so that a half-written colour's leftovers are not
  re-read as further SGR parameters.
- **`ToSGR` emits nothing for an inverted-default colour.** Upstream compared the slot against 256
  only, so slot 257 fell through and produced `;38;5;257` — not a legal SGR parameter. Reached only
  from `DECRQSS.cs` when a program asks the terminal to report its current SGR.

### Behaviour deliberately *not* changed

- `ToSGR` still tests `Index > 16` where `>= 16` is correct, so palette index 16 is still reported as
  `;98;` rather than `;38;5;16`. Upstream's bug, left alone: it is on the DECRQSS reply path, no test
  pins it, and fixing it would widen this patch past gap 1.
- `Renderer.DefaultColor` (256) and `Renderer.InvertedDefaultColor` (257) still exist and are now
  unreferenced. They are public members of the vendored assembly, so deleting them would be a wider
  divergence than the gap needs; `CellColorKind` is what carries their meaning now.
- Every other gap in `docs/xtermsharp-known-gaps.md` — the OSC off-by-one, wide characters, kitty
  keyboard, mode 2026, SGR 9 — stays exactly as it was.
## Patch 2 — OSC payloads survive their own bytes (gaps 2 and 3)

Fixes gaps 2 and 3 of `docs/xtermsharp-known-gaps.md`. Both live in `ParserAction.OscPut` in
`EscapeSequenceParser.cs`, which copies a whole run of operating-system-command payload at once
rather than a byte at a time.

Three defects in that one loop:

- **The run started at `data [i+1]`.** The action fires *on* the first payload byte, so every run
  lost its first byte. The first run of an OSC starts on the identifier's leading digit, so `OSC 1`
  and `OSC 2` both reached the dispatcher as `";payload"`, `Int32.TryParse ("")` gave 0, and setting
  a window title also set the icon name. Every later run starts at a feed boundary, so a title split
  across two pseudo-terminal reads lost a byte at the seam as well.
- **`j > len` was tested before `data [j]` was read**, so a run that reached the end of a feed read
  one byte past the buffer. Invisible only because the caller's buffer is normally longer.
- **The run terminated on `data [j] > 0x7f && data [j] < 0x9f`.** Those are UTF-8 continuation
  bytes, not terminators, so `OSC 0 ; naïve — Ω BEL` ended at the em dash's `0x80` and the title
  never arrived.

### Changes

| Site | Change |
| --- | --- |
| `ParserAction.OscPut` | The run is copied from `data [i]`, the bound test is `j >= len`, and the only terminator is a byte below `0x20`. |
| `BuildVt500TransitionTable` | `r (0x80, 0xa0)` is `OscPut` in `OscString`, and `0x9c` is off that state's `OscEnd` list. The scan loop consumes a whole run without consulting the table, so the two have to agree about what ends an OSC or the result depends on where a read boundary fell. |
| `_osc` | `List<byte>`, not `string`. The payload accumulates as bytes and is decoded once, at `OscEnd`, by the new `OscText` helper. |

Decoding per run was the second reason a UTF-8 scalar could not survive a chunk boundary: each run
went to `Encoding.UTF8.GetString` on its own, so a scalar split across two feeds — or across an
ignored C0 byte — became two replacement characters. Accumulating bytes removes the possibility
rather than handling it, and it deletes both the loop's `block` copy and upstream's `TODO` asking
for exactly this.

### Behaviour that changed beyond the fix

- **8-bit ST (`0x9c`) no longer terminates an OSC string.** It cannot: `0x9c` is the second byte of
  U+2733 and of every other scalar in `E2 9C xx`, and this parser reads bytes rather than decoded
  scalars, so the two readings are exclusive. The stream is UTF-8 — the adapter feeds it, and the
  ground state's fast path in `Parse` already prints every byte above `0x1f` rather than treating
  `0x9c` as a control — so the continuation-byte reading is the one that agrees with the rest of the
  engine. A program that ends an OSC with a bare `0x9c` instead of `BEL` or `ESC \` now runs on into
  the payload.
- **The claude corpus paints a different screen.** Its title is `ESC ] 0 ; E2 9C B3 ...` at bytes
  1464 and 4268 — `✳ Claude Code`. The `0x9c` used to end that OSC three bytes in, so the title
  became a replacement character and the remaining `B3 20 43 6c ...` printed onto the alternate
  screen as `³ Claude Code` across rows 32 and 33. Both are gone. `claude.grid` has no golden yet;
  whoever audits one reads these frames.
- **`ParsingState.Osc`** is decoded from the accumulated bytes when the error handler runs, so a
  payload caught mid-scalar is reported without its trailing partial byte.

### Behaviour deliberately *not* changed

- `0x9c` still ends a DCS passthrough (`DcsUnhook`) and an SOS/PM/APC string. The same
  continuation-byte argument applies to both, but neither is an OSC, no corpus reaches them, and
  gap 3 does not name them.
- The payload accumulator is still unbounded: an OSC with no terminator grows until one arrives.
  Upstream's string had the same property, and `0x9c` leaving the terminator list removes one more
  byte value that used to bound it by accident.
## Patch 4 — a DEC private mode keeps its prefix when there is more than one parameter (gap 4)

`InputHandler.SetMode` / `ResetMode` had two paths: one parameter went to `csiDECSET` with the
`collect` the parser had read, more than one went round a loop that passed `""`. The `"?"` of a
private mode was thrown away exactly when a program batched its setup, so
`CSI ?1000;1002;1003;1006;2004;1004h` set none of the six modes it names and was applied as six
ANSI modes instead.

Both methods are now that loop, with `collect` passed through. The single-parameter case is the
one-iteration case of it, so the branch is gone rather than fixed, and with it the
`pars.Length == 0` early return that a zero-iteration loop already covers.

## Patch 5 — `CSI u` means restore cursor only without a private prefix (gap 5)

`parser.SetCsiHandler ('u', ...)` called `terminal.RestoreCursor ()` whatever the prefix, so kitty
keyboard's `CSI > 1 u` (push flags) and `CSI < u` (pop) each teleported the cursor to the last saved
position — four times a session in the acceptance corpus.

The handler now dispatches on `collect` and reports anything else through `terminal.Error`, the way
the `p`, `x` and `z` handlers beside it already do. This patch only stops the wrong movement:
tracking the flags and answering `CSI ? u` is gap 12, which spans files this patch does not touch,
so a private `CSI u` is for now logged and dropped.

## Patch 6 — `CSI > 4 ; 2 m` is not SGR (gap 6)

`parser.SetCsiHandler ('m', ...)` discarded `collect` and handed the parameters to
`CharAttributes`, so xterm's modifyOtherKeys request `CSI > 4 ; 2 m` was applied as SGR 4 and SGR 2
and everything printed afterwards came out underlined and dim.

Same shape as patch 5: `CharAttributes` runs only for an empty `collect`, and a prefixed `m` goes to
`terminal.Error`. Recording the requested level is gap 12 and is not part of this patch.

## Patch 7 — the alternate screen is cleared on entry (gap 7)

`Buffer.FillViewportRows` began with `if (lines.Length != 0) return;` under a
`// TODO: limitation in original` note, so it filled a buffer that had never held a line and did
nothing to one that had. `BufferSet.ActivateAltBuffer` is the only caller on the alt-screen path and
`ActivateNormalBuffer (clearAlt: par == 1047)` does not clear the alt buffer for `?1049l`, so the
second `?1049h` of a session showed the first visit's painting.

The method now replaces every viewport row with a blank line, pushing rows only where the buffer is
short of a full viewport. Its summary changed from "Fills" to "Replaces every row of" to say so.

### Behaviour that changed beyond the fix

- **`?47h` and `?1047h` also clear on entry now**, where xterm clears only for `?1049h` and, for
  1047, on the way out. Distinguishing them means a parameter on `BufferSet.ActivateAltBuffer`,
  which all three modes reach through one call, and upstream's own comment there says the buffer is
  meant to be filled "when switching to it". No corpus and no test uses either mode.
- `FillViewportRows` is public and its contract changed without its signature changing. Both call
  sites are in `BufferSet`; the other one fills the normal buffer at construction, where the buffer
  is empty and the two behaviours coincide.

---

## Patch 8 — real character widths (gap 8)

`InputHandler.Print` hard-coded `var chWidth = 1;` with the real call commented out above it, so a
CJK ideograph, an emoji, a combining mark and a zero-width space each took one cell. Every column
count downstream of a wide character was wrong for as long as it was on screen, and the seam's
`CellWidth.WideLeader` / `WideTrailer` had no producer.

### `CharWidth.cs`

`RuneHelper.ConsoleWidth` — the port of `wcwidth.c` that had been sitting in the tree unreferenced —
is now the width. Two defects had to be fixed before it could be called at all:

- **`bisearch` was called with a count where it wants the last index.** `combining.GetLength (0)` is
  one past the end, and `bisearch` dereferences `table [max,1]` on its first line, so the very first
  non-ASCII rune threw `IndexOutOfRangeException`. The C original passes
  `sizeof (table) / sizeof (struct interval) - 1`; the `- 1` was lost in the port. This is why the
  function had never been wired up.
- **U+00A0 was classified as a C1 control.** The test read `rune >= 0x7f && rune <= 0xa0`, where
  `wcwidth.c` has `ucs >= 0x7f && ucs < 0xa0`. A no-break space is printable and one column wide;
  as a zero-width rune it would have been dropped from the grid. The claude corpus contains five.

The table itself is untouched, and it is the 2007 wcwidth data: **it has no emoji**, so an emoji is
one column here where a modern terminal gives it two. Widening the table is a separate change with
no test behind it and real disagreement between terminals about the boundaries, so it was left
alone rather than guessed at. Everything else the corpora contain — box drawing, block elements,
arrows, the dingbats claude prints — is one column in this table and in xterm.

### `InputHandler.Print`

- The width is taken **after** the charset substitution rather than before it, so it describes the
  rune the cell will actually hold. No charset in the tree substitutes a wide rune, but computing it
  from the pre-substitution codepoint was a defect waiting for one that did.
- **A zero-width rune takes no column and is dropped.** Upstream tried to merge it into the previous
  cell with `chMinusOne.Code += ch` — *arithmetic* addition of two codepoints, where the xterm.js
  line it was translated from concatenates strings. It would have turned `e` + U+0301 into U+0366.
  That block was unreachable while every width was 1; making widths real made it reachable and
  wrong, so it is gone.

  **The cell cannot yet hold a grapheme cluster.** `CharData` has a `Rune` and a `Code` and nowhere
  to put the marks, and `XtermSharpEngine.Translate` builds the seam's `TerminalCell` from `Code`
  alone, leaving `TerminalCell.Combining` null. Dropping the mark keeps the base character intact,
  which is the least wrong of the three available answers; carrying the cluster needs `CharData` and
  the adapter to move together, and until they do
  `UnicodeSpec.CombiningMark_JoinsTheCellItFollowsRatherThanTakingItsOwn` stays red on its second
  assertion. Its first — that the mark costs no column — passes.
- **The trailer cell beside a wide character has `Width` 0.** Upstream wrote `CharData.Null`, which
  is width 1, into the second column while the code around it (`chMinusOne.Width == 0`,
  `lastCell.Width == 2`) already assumed 0. Width 0 is also what the adapter reads as
  `CellWidth.WideTrailer`, and what makes `ITerminalGrid.RowText` skip the column instead of
  printing a second character for it. The trailer carries the leader's attribute, so a background
  colour covers both halves.
- **A character too wide for the line is dropped rather than written past the row.** With every
  width equal to 1 the invariant "`buffer.X + chWidth - 1 <= right` once the wrap block has run"
  held by construction. It does not hold for a two-column character on a one-column screen —
  `TerminalSize` permits one — where the leader lands in the only cell and the trailer is written
  off the end of the row: an `IndexOutOfRangeException` out of `Feed`. The guard after the wrap
  block drops the character instead. `CharacterWidthSpec` pins it.
- The `if (chWidth > 0)` around the trailer loop is gone: zero is handled earlier, and
  `ConsoleWidth` returns nothing else that could reach it.

### Behaviour deliberately *not* changed

- **Overwriting one half of a wide character leaves the other half.** xterm replaces the orphaned
  cell with a space; this engine has no partner-cell logic anywhere, and adding it touches erase,
  insert, delete and reflow. No test pins it and it is not what gap 8 describes.
- `BufferLine.GetTrimmedLength` sums `data [i].Width` inside a loop over `j` — upstream's typo,
  which now returns `Width * (i + 1)` for a line whose last cell is a wide leader. Reflow reads it.
  Left alone: it is not in these two files, and the reflow suite does not move.

### Patch 8b — where a combining mark lives

The half patch 8 left open. A zero-width rune was dropped at `if (chWidth == 0) continue;`, so
`TerminalCell.Combining` had no producer and an "e" followed by U+0301 lost its accent.

`CharData` gains a `string Combining`, set to null by both constructors — the struct had nowhere to
hold a cluster, which is the reason the mark was dropped rather than an oversight. `InputHandler`
gains `AttachCombiningMark`, called where the rune used to be discarded: it appends to the cell
before the cursor, stepping back one further when that cell is a wide character's trailer, since a
trailer carries no rune of its own to combine with. `XtermSharpEngine.Translate` projects the field.

Upstream's own merge block is not restored. It did `chMinusOne.Code += ch` — arithmetic on two
codepoints, where the xterm.js line it was ported from concatenates strings, so "e" + U+0301 became
U+0366. This appends to a string instead.

**Only marks join the cluster.** The zero-width test is `UnicodeCategory`, not width: a zero-width
space and a zero-width joiner are also width 0 but are not part of the preceding grapheme, and
appending them would put them into `RowText` and into anything that copies a selection. They are
still dropped, which is what `ZeroWidthSpace_DoesNotConsumeAColumn` pins.

**A mark with nothing before it is dropped.** At column 0 there is no cluster on this row to join,
and xterm does not carry the mark back to the previous row either.

## Patch 9 — synchronized output, DEC mode 2026 (gap 9)

`case 2026` did not exist in either direction, so the parameter fell off the end of the DECSET
switch unrecorded and no caller could tell that a program was mid-frame.

`Terminal` gained `SynchronizedUpdate` (a frame is open) and `SynchronizedFrames` (how many have
closed since the terminal was created), both read-only from outside the assembly and both written
only by `BeginSynchronizedUpdate` / `EndSynchronizedUpdate`, which is what keeps "the counter moves
only when a frame that was open closes" true by construction. `?2026l` with nothing open counts
nothing. `Setup ()` abandons an open frame on RIS without counting it, and leaves the count alone so
that a caller diffing it across a feed never sees a negative.

The mode is recorded and never honoured: the bytes inside the block are applied to the grid as they
arrive. An engine that buffered them would make the grid lie about what it has received, and would
strand the screen if a program never sent the closing sequence.

---

## Patch 10 — cursor style and DEC mode 12 (gaps 10 and 11)

`Terminal.SetCursorStyle` had an empty body, so DECSCUSR was parsed at `InputHandler.cs` and
discarded, and DEC mode 12 was two commented-out lines in `TerminalModeSetExtensions`. Nothing ever
wrote `TerminalOptions.CursorBlink` either, so a caller reading it got back whatever it passed in.

`Terminal.CursorStyle` now holds the style, `SetCursorStyle` assigns it, and `Setup ()` seeds it
from `Options.CursorStyle` so that RIS returns the cursor to the style the terminal booted with.
Mode 12 set and reset map the current style onto its blinking or steady counterpart through a
private `WithBlink` in `TerminalModeSetExtensions`.

The six-valued `CursorStyle` is kept as the single stored value rather than split into a shape and a
blink flag. It is exactly shape × blink, it is already the vendored public vocabulary that
`InputHandler` speaks, and two fields would be two places for the same fact to be recorded
differently. `GitBench.Terminal.Vt.TerminalCursor` splits it into its two axes in the adapter, where
the seam asks for that shape.

### Behaviour deliberately *not* changed

- `TerminalOptions.CursorBlink` is still a field nothing writes. It is public on the vendored
  assembly and seeding the style from it would give a `TerminalOptions` two ways to disagree with
  itself about blinking; `CursorStyle` is what carries that meaning now.
- `SoftReset` (DECSTR) leaves the cursor style alone, as xterm does. Only RIS resets it.

---

## Patch 12 — kitty keyboard flags and modifyOtherKeys are recorded (gap 12)

Patches 5 and 6 stopped a private `CSI u` moving the cursor and a prefixed `CSI m` being applied as
SGR, but neither recorded what the sequence asked for: both were logged through `terminal.Error` and
dropped. A terminal that cannot answer `CSI ? u` cannot negotiate progressive enhancement, which is
the negotiation the input encoder is built on.

### `Terminal.cs`

`KeyboardProtocolFlags` and `ModifyOtherKeys` join `SynchronizedUpdate` as observable mode state,
both `{ get; private set; }`. Behind the first is a `Stack<int>` and four methods —
`PushKeyboardProtocolFlags`, `PopKeyboardProtocolFlags`, `SetKeyboardProtocolFlags` and
`ReportKeyboardProtocolFlags` — plus `SetKeyModifierOptions` for the xterm resource request.

Flags are masked to the five bits the kitty protocol defines, so a reply never claims an
enhancement the protocol has no way to mean. The stack is bounded at 16, kitty's own limit, and
drops its oldest entry rather than growing without end for a program that pushes and never pops.

`SoftReset` clears all three. A program that soft-resets is asking for the encoding it started with,
and leaving the flags set would keep sending it an encoding it has just said it no longer expects.

### `InputHandlers/InputHandler.cs`

The `u` handler's two-way `if` becomes a switch over `collect`: `""` restores the cursor as before,
`>` pushes, `<` pops, `=` sets in place, `?` answers, and anything else still goes to
`terminal.Error`. A `<` with no parameter pops one — the parser defaults an omitted parameter to 0,
and `CSI < u` is the spelling the acceptance corpus actually contains.

The `m` handler gains a `>` arm routing to `SetKeyModifierOptions`. Only resource 4
(modifyOtherKeys) is observable; a request naming any other resource is accepted and ignored rather
than reported as an error, because it is a legal thing for a program to send.

### Behaviour that changed beyond the fix

- **A private `CSI u` or a prefixed `CSI m` no longer reaches `terminal.Error`** for the prefixes
  above. Anything still unrecognised does.

## Patch 13 — SGR 9 and 29, crossed out (gap 13)

`CharAttributes` had no branch for either parameter, so `CSI 9m` was reported as an unknown SGR
attribute and dropped, even though `FLAGS.CrossedOut` exists and the adapter already maps it to
`CellAttributes.CrossedOut`. Two branches, in the shape of the ones on either side of them.

`CharacterAttribute.ToSGR` still does not emit `;9`, so a DECRQSS reply under-reports a crossed-out
cell. That file is outside this patch.
## Patch 15 — scrollback depth is settable (gap 15)

`TerminalOptions.Scrollback` was `{ get; }` and the constructor fixed it at 1000, so
`TerminalSetup.ScrollbackLines` had nowhere to go. It is now `{ get; set; }`, which is how `Cols`
and `Rows` beside it already work. `Buffer.getCorrectBufferLength` reads it on `Clear` and on
`Resize` as well as at construction, so a later change takes effect at the next one rather than
being silently ignored.

---

## Patch 16 — lines that leave the screen are counted (gap 16)

`Buffer.YBase` was the only record of a line leaving the top of the screen, and it stops moving once
the scrollback is full: `Terminal.Scroll` checks `Lines.IsFull` and, when it is, recycles the oldest
line in place rather than incrementing `YBase`. The count and the depth are the same number only
until the history fills up, and after that they diverge by one per line for the rest of the session.

`Terminal` gained `ScrolledIntoHistory`, `{ get; private set; }` beside `SynchronizedFrames`,
incremented once at the end of the `ScrollTop == 0` branch of `Scroll` — the branch that is exactly
"a line goes to the scrollback", reached on both the trimmed and the untrimmed path. `Setup ()`
leaves it alone on RIS for the same reason `SynchronizedFrames` is left alone: a caller diffing it
across a feed must never see a negative.

`FeedResult.LinesScrolled` is the adapter's diff of this counter, where it was a diff of
`Buffer.YBase`. What reads it is the pane's scroll position, which follows the count to keep a
reader on the line they were reading while the shell writes underneath them; following the depth
instead had the text crawling under them once the history was full.
