# XtermSharp against the unified seam

`dotnet test` on 2026-08-28: **254 tests, 196 pass, 58 fail.**

Every failure below is XtermSharp's. The suite is engine-agnostic — `TerminalEngines.cs` is the
only file that names an implementation — and every expectation states what a correct xterm-class
terminal does, worked out from the specifications and from the recorded corpora. Nothing here may
be relaxed to make the suite green, and no golden may be regenerated from engine output.

Line numbers refer to the verbatim copy under `XtermSharp/`, which is byte-identical to the clone
(upstream `XtermSharp/master`, last touched December 2020). `diff -r` against the clone is clean.

---

## Fatal — throws and kills the session

### 1. Truecolor SGR throws

`Terminal.cs:683` — `public int MatchColor (int r1, int g1, int b1) { throw new NotImplementedException (); }`,
reached from `InputHandlers/InputHandler.cs:754` (foreground) and `:770` (background) whenever SGR
carries `38;2;r;g;b` or `48;2;r;g;b`. The first 24-bit colour takes the whole engine down mid-stream.
The acceptance corpus sends 43 of them in five seconds.

Failing: `ColorSpec.Truecolor*` (4), `ColorSpec.Reset_ReturnsBothTruecolorsToTheThemeDefault`,
`AttributeSpec.ColourAndAttribute_InOneSequence_BothReachTheCell`,
`ChunkingAndRobustnessSpec.TruncatedTruecolorSequence_DoesNotThrow`,
`ChunkingAndRobustnessSpec.WholeSessionFedOneByteAtATime_ProducesTheSameGrid`,
all six `CorpusPropertiesSpec` claude cases, all three `ChunkInvarianceTests` claude cases,
`CorpusReplayTests.Replay_ProducesTheGoldenScreens(claude)`,
`CorpusReplayTests.Replay_OfTheAcceptanceCorpus_CountsOneCompletedFramePerSynchronizedBlock`.

**Why `claude.grid` has no golden, and must not get one.** The engine cannot produce a single frame
of that corpus, so there is nothing to audit against the bytes. The replay test fails on the throw,
with `XtermSharp.Terminal.MatchColor` at the top of the stack. When truecolor works, a person reads
the frames against `Corpus/claude.bin` and commits the result — not before.

**Cost of making truecolor real.** `CharData.cs:16` packs a cell's whole appearance into one int as
`(flags << 18) | (fg << 9) | bg`: nine bits per colour, which holds a 256-entry palette index plus
the sentinels 256 (default) and 257 (inverted default), and has no room for 24 bits. The change is
therefore to the cell representation, which is the widest change available in this source:

- `CharData.Attribute` becomes a wider value — the cheapest shape is `int flags` plus two 32-bit
  colour words tagged default / indexed / rgb, i.e. `CharData` grows from 16 to ~24 bytes.
- Every producer and consumer of the packed int moves with it. Direct hits: `Terminal.CurAttr` and
  the `CharData.DefaultAttr` / `InvertedAttr` constants; `InputHandler.CharAttributes`
  (`InputHandler.cs:664`, which builds the pack at `:790`); `CharacterAttribute.ToSGR`;
  `BufferLine` fill and resize paths; `Buffer.GetBlankLine` and `FillViewportRows`;
  `Renderer`/`SelectionService`/`SearchService` where attributes are compared for run-splitting.
  Roughly 25 call sites across 10 files, found by grepping `>> 18`, `>> 9`, `& 0x1ff` and `Attribute`.
- `MatchColor` then disappears rather than being implemented: the point of the seam's
  `TerminalColor` sum type is that nothing resolves RGB to a palette slot at parse time.
- The adapter's `XtermGrid.Translate` and `ToColor` change with it; nothing else in this repository
  moves, because no test binds to an XtermSharp type.

Estimate: one focused day, most of it mechanical, with the risk concentrated in the reflow and
renderer paths that assume attribute equality is int equality. Worth doing before anything else on
this list — it is the only gap that is fatal rather than merely wrong.

---

## Wrong — silently corrupts the screen

### 2. Every operating-system-command payload loses its first byte

`EscapeSequenceParser.cs:624`, `case ParserAction.OscPut`. The run is copied from `data[i+1]`
instead of `data[i]`:

```
var block = new byte [j - (i+1)];
for (int k = i+1; k < j; k++)
    block [k-i-1] = data [k];
```

Two consequences, one root cause:

- **Every OSC is executed as OSC 0.** The byte dropped from the first run is the identifier's first
  digit, so `osc` is `";payload"`, `Int32.TryParse("")` yields 0, and the dispatcher at
  `InputHandler.cs:192` runs `SetTitleAndIcon` for OSC 1 and OSC 2 as well. Confirmed directly:
  feeding `OSC 2;C:\Program Files\...\less.exe BEL` sets both the title and the icon name.
- **A payload split across two feeds loses a byte at the seam.** Each new run drops its own first
  byte. On a pseudo-terminal every read boundary is arbitrary, so this fires constantly.

The loop also evaluates `data[j]` when `j == len` before the `j > len` guard can stop it — an
out-of-bounds read that is invisible only because the caller's buffer is usually longer.

Failing: `TitleAndHyperlinkSpec.Osc2_SetsOnlyTheWindowTitle`,
`TitleAndHyperlinkSpec.Osc_SplitAcrossFeeds_StillSetsTheTitle`,
eight `ChunkInvarianceTests` cases (vim, less, git-log, smoke at 1-byte and 7-byte),
`CorpusReplayTests.Replay_ProducesTheGoldenScreens(smoke)`.

**This one had already been blessed into a golden.** `smoke.grid` recorded the window title as
`C:\WINDOWS\YSTEM32\cmd.exe`. The corpus says `SYSTEM32` (bytes 75–106), and the third frame
boundary falls at byte 90, on that `S`. The golden has been corrected to the bytes and the smoke
replay is now red. The correction is noted in the file's `;` provenance block.

### 3. Non-ASCII operating-system-command payloads are mangled

Same site, `EscapeSequenceParser.cs:626`. The run terminates on any byte in `0x80..0x9E`, which
includes most UTF-8 continuation bytes, and the next run then loses its own first byte to defect 2.
A title containing an em dash does not survive.

Failing: `TitleAndHyperlinkSpec.Title_MayContainNonAsciiText`.

### 4. A multi-parameter DEC private mode is applied as an ANSI mode

`InputHandlers/InputHandler.cs:808` (`SetMode`) and `:793` (`ResetMode`):

```
if (pars.Length > 1) {
    for (var i = 0; i < pars.Length; i++)
        terminal.csiDECSET (pars [i], "");
    return;
}
terminal.csiDECSET (pars [0], collect);
```

The `collect` string — `"?"` for a private mode — is replaced with `""` on the multi-parameter path,
so `CSI ?1000;1002;1003;1006;2004;1004h` sets none of the six modes it names. Programs that batch
their mode setup get silently nothing.

Failing: `ModeSpec.ModesSetInOneSequence_AreAllReported`.

### 5. `CSI u` dispatches on the final byte alone

`InputHandlers/InputHandler.cs:109` — `parser.SetCsiHandler ('u', (pars, collect) => terminal.RestoreCursor ());`.
The private prefix is ignored, so kitty's `CSI > 1 u` (push flags) and `CSI < u` (pop) both execute
*restore cursor*. The acceptance corpus pops flags four times per session; each one teleports the
cursor to the last saved position.

Failing: `KeyboardProtocolSpec.PushingKeyboardFlags_DoesNotMoveTheCursor`,
`KeyboardProtocolSpec.PoppingKeyboardFlags_DoesNotMoveTheCursor`.

### 6. `CSI > 4 ; 2 m` (modifyOtherKeys) is parsed as SGR

`InputHandlers/InputHandler.cs:79` — `parser.SetCsiHandler ('m', (pars, collect) => CharAttributes (pars));`
discards `collect`, so parameters 4 and 2 are applied as *underline* and *dim* to everything printed
afterwards.

Failing: `KeyboardProtocolSpec.ModifyOtherKeys_DoesNotChangeCellAttributes`,
`KeyboardProtocolSpec.ModifyOtherKeys_RecordsTheRequestedLevel`.

### 7. The alternate buffer is not cleared on entry

`Buffer.cs:190`, `FillViewportRows` returns early when the buffer already holds lines
(`if (lines.Length != 0) return;`, with a `// TODO: limitation in original` above it), and
`BufferSet.cs:75` is the only caller on the alt-screen path. A second `?1049h` therefore shows the
previous visit's painting.

Failing: `AltScreenSpec.ReEnteringAltScreen_DoesNotShowThePreviousVisitsContent`.

### 8. Every character is one column wide

`InputHandlers/InputHandler.cs:1244` — `var chWidth = 1;`, with the real call commented out on the
line above (`// var chWidth = Rune.ColumnWidth ((Rune)code);` and a `1 until we get a fixed NStack`
note). CJK, emoji, combining marks and zero-width characters each take a cell of their own, and a
wide character desynchronises the rest of the row for as long as it is on screen. The seam's
`CellWidth.WideLeader` / `WideTrailer` are therefore never produced.

Failing: `UnicodeSpec.WideCharacter_OccupiesTwoColumns`,
`UnicodeSpec.WideCharacter_LeavesATrailerCellBesideIt`,
`UnicodeSpec.TextAfterAWideCharacter_StartsTwoColumnsLater`,
`UnicodeSpec.CombiningMark_JoinsTheCellItFollowsRatherThanTakingItsOwn`,
`UnicodeSpec.ZeroWidthSpace_DoesNotConsumeAColumn`.

`TerminalCell.Combining` has no producer for the same reason: a combining mark takes its own cell
rather than joining the one before it.

---

## Ignored — the feature is absent but nothing breaks

### 9. Synchronized output (`?2026`) is unhandled

No `case 2026` anywhere in `InputHandlers/TerminalModeSetExtensions.cs`; the parameter falls off the
end of the DECSET switch unrecorded. The grid stays correct — the renderer simply cannot know it is
mid-frame, so a streaming TUI tears. Twenty blocks in the acceptance corpus. This is also why the
adapter reports `FramesCompleted: 0` and `FramePending: false` on every feed.

Failing: `ModeSpec.SynchronizedOutput_TracksBeginAndEndOfFrame`,
`ModeSpec.SynchronizedOutput_CountsAFrameOnTheFeedThatClosesIt`,
`CorpusReplayTests.Replay_OfTheAcceptanceCorpus_CountsOneCompletedFramePerSynchronizedBlock`.

### 10. Cursor shape is unobservable

`Terminal.cs:697` — `public void SetCursorStyle (CursorStyle style) { }` has an empty body, is not
virtual, and stores nothing. `InputHandler.cs:570` parses DECSCUSR correctly and hands the style to
that method, where it is discarded. No adapter can recover it without a patch.

### 11. Cursor blink is a dead field

DEC mode 12 is parsed and dropped: `TerminalModeSetExtensions.cs:139` (`// this.cursorBlink = true;`)
and `:337` (`// this.cursorBlink = false;`). Nothing ever writes `TerminalOptions.CursorBlink`
either, so whatever the adapter passes in at construction is what gets reported forever.

The adapter reports `terminal.Options.CursorBlink`, which is XtermSharp's own default of `false`.
That makes `TerminalCursor.Blinking` a constant `false` rather than a constant `true`, and it is why
every golden's cursor line reads `steady`. **Those `steady` readings are engine output, not audited
terminal behaviour** — a fresh xterm cursor blinks. They have been left as they are rather than
churned to `blink`, because the field is equally untrue either way and the goldens' cell content is
what was audited; the divergence is named by the two tests below instead. When mode 12 is
implemented, the goldens' cursor lines have to be re-derived from the corpora.

Failing: `CursorShapeSpec.CursorShape_StartsAsABlinkingBlock`,
`CursorShapeSpec.CursorBlink_TracksTheDecPrivateMode`,
five of the six `CursorShapeSpec.SetCursorStyle_SelectsShapeAndBlinkIndependently` cases (gap 10).

### 12. Kitty keyboard flags are not tracked and `CSI ? u` is not answered

There is no handler at all. A terminal that does not reply to the query cannot negotiate progressive
enhancement, which is the whole reason the plan owns the input encoder.

Failing: `KeyboardProtocolSpec.PushingKeyboardFlags_MakesThemCurrent`,
`KeyboardProtocolSpec.QueryingKeyboardFlags_AnswersWithTheCurrentFlags`,
`KeyboardProtocolSpec.QueryingKeyboardFlags_AfterAPush_ReportsThePushedFlags`.

### 13. SGR 9 / 29 (crossed out) is unhandled

`InputHandlers/InputHandler.cs:664`, `CharAttributes`, has no branch for parameter 9 or 29 even
though `FLAGS.CrossedOut` exists at `CharacterAttribute.cs:13` and the packing has room for it.

Failing: `AttributeSpec.Attribute_ReachesTheCellsPrintedAfterIt(9)`,
`AttributeSpec.CancellingParameter_ClearsTheAttributeItPairsWith(9, 29)`,
`AttributeSpec.Attributes_InOneSequence_AreAllCarriedAsBits`.

### 14. OSC 8 hyperlinks are discarded

`InputHandlers/InputHandler.cs:192-196` registers handlers for 0, 1 and 2 only, and everything else
goes to the fallback at `:41`. Correct as far as the grid goes — the link text prints and the URL
does not leak — so no test fails. Recorded because the URL is gone by the time the renderer sees the
cell, so clickable links need a patch. The acceptance corpus emits four of them.

### 15. Scrollback depth cannot be set

`TerminalOptions.cs:11` — `public int? Scrollback { get; }` is get-only and the constructor fixes it
at 1000. `TerminalSetup.ScrollbackLines` is part of the seam's contract precisely so that two
engines agree about what a recorded session looks like, and this engine silently ignores it. No test
pins it because the whole suite runs at 1000 and every corpus is far shorter than that, but a
caller asking for a different depth gets 1000 without being told.

---

## Configuration, not a defect

`TerminalOptions.ConvertEol` defaults to `true`, which makes a bare LF also perform a carriage
return. That is console-host behaviour: on a pseudo-terminal the line discipline has already
produced CRLF, and a bare LF from a program means "down one row, same column". The adapter sets it
to `false`. Worth knowing because the default silently passes most tests and then loses a column in
exactly the case a TUI relies on.

---

## Build

The vendored sources **do not compile on `net10.0`**. `NStack.Core` declares its own `System.Rune`,
which makes every bare `Rune` in XtermSharp ambiguous with `System.Text.Rune` (CS0104 at
`InputHandlers/InputHandler.cs:1224` and `SelectionService.cs:175`). `XtermSharp.Vendored` therefore
targets `netstandard2.0` and is consumed from `net10.0`; vendoring these files as `net10.0` later
means qualifying those two call sites to `NStack.Rune`, or putting an extern alias on the reference.

Upstream's own `XtermSharp.csproj` also does not build under a current SDK: its `InternalsVisibleTo`
`AssemblyAttribute` item carries `Visible="False"` metadata, which newer SDKs pass through as a
second constructor argument. The copy under `XtermSharp/` includes that project file for fidelity
with the clone and nothing references it; `XtermSharp.Vendored/XtermSharp.Vendored.csproj` compiles
the sources instead.

NStack flows transitively to everything downstream, so every consuming project carries
`<Using Include="System.Text.Rune" Alias="Rune" />`. `PrivateAssets` would hide the type but also
drop the assembly from the consumer's `deps.json`, and the engine then throws `FileNotFoundException`
while building its first buffer.

---

## Adapter translations that are not gaps

- **`ITerminalGrid.ContinuesPreviousRow` is a direct read.** `BufferLine.IsWrapped` is set on the
  *continuation* row (`InputHandler.cs:1307` — `buffer.Lines [++buffer.Y].IsWrapped = true`
  increments first), which is the same direction the seam asks about, so the adapter reads
  `Lines[YBase + row].IsWrapped` with no lookahead and no special case. The goldens list those rows
  under the key `continues`; see their `;` provenance blocks.
- **Palette slots 256 and 257** are XtermSharp's "default" and "inverted default"; both map to
  `TerminalColor.Default`, because the inverse bit already carries that meaning at the seam.
