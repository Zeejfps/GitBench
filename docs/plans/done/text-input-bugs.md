# Text input & text layout — known bugs

> Found while scoping CJK/IME support (`docs/plans/cjk-ime-support.md`), but **none of these depend
> on that plan** — they are live bugs today, reachable with emoji, astral-plane characters, or any
> multi-line text field.
>
> **BUG-1 through BUG-6 are fixed** (see *Status* under each). What remains is the audit section at
> the bottom.
>
> Most of them shared one root cause: **text is indexed by UTF-16 code unit (`char`) as if that were
> a character.** It isn't. An emoji or a CJK Extension-B ideograph is a surrogate *pair* — two
> `char`s — and slicing between them produces an orphaned surrogate that renders as tofu.

---

## BUG-1 — Arrow keys land the caret between surrogate halves — FIXED

**Was:** `_caretIndex--` / `_caretIndex++` stepped exactly one UTF-16 code unit, so Left over `😀`
put the caret *inside* the surrogate pair.

**Status:** fixed. `framework/ZGF.Gui/TextBoundaries.cs` is the single home for cluster boundaries
(`Next` / `Prev` / `Snap`, on grapheme clusters via `StringInfo`, so a skin-tone emoji moves as one
unit). `TextInputView` routes every caret mutation through it, and every *computed* index (clicks via
`GetCaretIndexFromPoint`, Up/Down via `FindIndexClosestToX`) is snapped in one place — `SetCaret`.

**Tests:** `framework/ZGF.Gui.Tests/TextInputBoundaryTests.cs`.

---

## BUG-2 — Backspace deletes half a character — FIXED

**Was:** `Delete` removed one code unit, leaving a lone surrogate in the buffer — which flowed into
`TextValue` → the commit message → git.

**Status:** fixed. `Delete` removes the whole cluster before the caret (`TextBoundaries.Prev`), and
`DeleteWord` goes through the word scan below. `DeleteChar` is gone; deletion has one path.

---

## BUG-3 — The multi-line text field wraps mid-word, and ignores every CJK rule — FIXED

**Was:** `ShouldWrap` was a bare width test evaluated per code unit, so `GetLines` broke the line
wherever the pixel budget ran out: `implementa\ntion`, closing punctuation starting a line, and
surrogate pairs split across lines.

**Status:** fixed. `TextWrapper` gained `WrapRanges`, which applies the break rules it already had
(word boundaries, kinsoku, surrogate-safe `ReadCodePoint`) but returns `Range`s into the caller's
buffer instead of new strings — a text field's caret is an index, so it can't wrap through the
string API. The ranges tile the buffer: a soft-wrapped line ends where the next begins (spaces at the
break stay on the line they follow, so every index stays reachable by the caret), while a newline
leaves a one-character gap.

`TextInputView.GetLines` is now the one line model — drawing, the caret rect, selection rects, clicks
and Up/Down all read it, so they cannot disagree about where a line ends. It is cached until the text,
the width, or a metrics-affecting style changes. `ShouldWrap` and the three hand-rolled break loops
(in `ComputeCaretRect`, `DrawSelectionBox`, `GetLines`) are gone.

**Tests:** `framework/ZGF.Gui.Tests/TextInputWrapTests.cs`.

---

## BUG-4 — `TruncateToFit` orphans a surrogate — FIXED

**Was:** two copies of ellipsis truncation — `TextView.Ellipsize` guarded the surrogate cut,
`GitBench/Controls/TextMeasure.cs` didn't.

**Status:** fixed by removing the fork rather than copying the guard. `framework/ZGF.Gui/TextEllipsis.cs`
is the one implementation, and it snaps the cut to a cluster boundary (so it also won't split an emoji
from its skin-tone modifier). `TextView` and GitBench's three call sites use it; `TextMeasure` is deleted.

**Tests:** `framework/ZGF.Gui.Tests/TextEllipsisTests.cs`.

---

## BUG-5 — Ctrl+Arrow treats an entire CJK sentence as one word — FIXED

**Was:** `IsWordChar` = `char.IsLetterOrDigit(c) || c == '_'`. Every CJK ideograph is a letter and CJK
doesn't use spaces, so Ctrl+Right jumped from the start of a Chinese sentence to its end.

**Status:** fixed. Word scanning moved to `TextBoundaries.NextWord` / `PrevWord`, which classify by
`Rune` (not `char`) into word / ideographic / other, using `TextWrapper.IsWide` for the ideographic
range. An ideograph is its own word, so Ctrl+Arrow and Ctrl+Backspace move one character within CJK;
Latin word-jumping is unchanged.

---

## BUG-6 — The glyph atlas silently drops glyphs when full — FIXED

**Was:** one 2048×2048 skyline atlas with no growth and no eviction. `TryReserve` returned `false`
when full and the glyph simply wasn't drawn — no error, no log, no fallback.

**Status:** fixed by growth plus a signal, not by eviction (eviction needs a live-glyph refcount the
backend doesn't have; growth is cheap here). The atlas doubles in *height* up to `atlasMaxHeight`
(default 8192) — the width is fixed, so a row's offset into the pixel buffer is unchanged and both the
existing pixels and the skyline stay valid across the resize. `Version` bumps on growth; the GL and
Metal uploaders compare it against the version they uploaded and reallocate the texture instead of
poking a dirty sub-rect into storage of the wrong shape.

When it genuinely can't fit a glyph even at max size, `GlyphAtlas.Exhausted` (surfaced as
`FreeTypeFontBackend.AtlasExhausted`) fires once. The framework has no logger, so this is a signal for
the host rather than a log line — but it is no longer silent.

**Tests:** `framework/ZGF.Gui.Tests/GlyphAtlasTests.cs`.

---

## Audit (unconfirmed — still open)

These index or `Substring` by `char` and may have the same surrogate hazard. Not yet confirmed with a
concrete repro:

- `GitBench/Infrastructure/PathWrap.cs`
- `GitBench/Features/Diff/DiffRowPainter.cs`

Also worth knowing (not a bug, but an inconsistency): the vendored GLFW natives are **version-skewed**
— `win-x64` is 3.3.7 while `osx-*` is 3.4.0. `docs/plans/cjk-ime-support.md` Phase 1 unifies them.
