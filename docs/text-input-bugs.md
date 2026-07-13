# Text input & text layout — known bugs

> Found while scoping CJK/IME support (`docs/plans/cjk-ime-support.md`), but **none of these depend
> on that plan** — they are live bugs today, reachable with emoji, astral-plane characters, or any
> multi-line text field. They can be fixed independently and in any order.
>
> Most of them share one root cause: **text is indexed by UTF-16 code unit (`char`) as if that were
> a character.** It isn't. An emoji or a CJK Extension-B ideograph is a surrogate *pair* — two
> `char`s — and slicing between them produces an orphaned surrogate that renders as tofu.
>
> `InvariantGlobalization` is **not** set, so full ICU is available: `System.Globalization.StringInfo`
> and `TextElementEnumerator` may be used for grapheme clusters. `System.Text.Rune` is the minimum
> bar.

---

## BUG-1 — Arrow keys land the caret between surrogate halves

**Severity:** high (data corruption follows from it)
**File:** `framework/ZGF.Gui.Desktop/Components/TextInput/TextInputView.cs:672` (`MoveCaretLeft`),
`:696` (`MoveCaretRight`)

`_caretIndex--` / `_caretIndex++` step exactly one UTF-16 code unit.

**Repro:** type `😀` (or `𠀀`, CJK Ext-B) into any text field, press Left once. The caret is now
*inside* the surrogate pair. Press Backspace and you delete half of it.

**Fix:** step by `Rune` (ideally by grapheme cluster, so combining marks and ZWJ emoji sequences move
as one unit). Add `PrevBoundary(int index)` / `NextBoundary(int index)` helpers on `TextInputView`
and route every caret mutation through them.

**Note:** `MoveCaretVertically`, `MoveCaretTo` and `GetCaretIndexFromPoint` (`:139`) can also land on
a mid-pair index — a click can put the caret inside a surrogate pair. Snap any computed index to the
nearest boundary in one place rather than patching each call site.

---

## BUG-2 — Backspace deletes half a character

**Severity:** high (corrupts the buffer)
**File:** `framework/ZGF.Gui.Desktop/Components/TextInput/TextInputView.cs:843` (`Delete`)

```csharp
DeleteChar(_caretIndex - 1);
_caretIndex--;
```

One code unit removed. Backspacing over an emoji or an astral ideograph leaves a lone surrogate in
`_buffer`, which then flows into `TextValue` → the commit message → git.

**Repro:** type `😀`, press Backspace once. Expect: empty. Actual: an orphaned surrogate (`\uD83D`).

**Fix:** delete a whole cluster — reuse the BUG-1 boundary helper. `DeleteWord` (`:863`) needs the
same treatment via its word-boundary scan.

**Existing test gap:** `framework/ZGF.Gui.Tests/TextInputUnicodeTests.cs` has
`TypesAstralPlaneChars` (insertion) and `BackspaceDeletesOneCyrillicChar` (BMP, 1 code unit) — but
nothing for *navigating or deleting* an astral character. Add those; they fail today.

---

## BUG-3 — The multi-line text field wraps mid-word, and ignores every CJK rule

**Severity:** medium (visible in the commit description box on every long message)
**File:** `framework/ZGF.Gui.Desktop/Components/TextInput/TextInputView.cs:292` (`GetLines`),
`:585` (`ShouldWrap`)

`ShouldWrap` is a bare width test (`lineWidth >= maxWidth`) evaluated at every character index, so
`GetLines` breaks the line **wherever the pixel budget runs out** — with no word boundaries at all.

Three distinct defects fall out of this:

1. **English breaks mid-word**: `implementa\ntion`. This is not a CJK issue; it is wrong for every
   language.
2. **No kinsoku for CJK**: closing punctuation (`。`, `」`) can start a wrapped line.
3. **It can split a surrogate pair across lines**, since `i` advances per `char`.

The kicker: `framework/ZGF.Gui/TextWrapper.cs` **already implements all of this correctly** —
UAX-14-lite `IsWide` classes, kinsoku via `IsNoBreakBefore`/`IsNoBreakAfter`, and surrogate-safe
`ReadCodePoint`. `TextInputView` simply doesn't use it.

**Fix:** make `TextInputView` wrap through `TextWrapper`. The wrinkle is that `TextWrapper` returns
`string`s while `TextInputView` needs `Range`s into `_buffer` (caret math depends on indices), so
`TextWrapper` needs a range-returning overload. Do that rather than duplicating the break rules.

---

## BUG-4 — `TruncateToFit` orphans a surrogate

**Severity:** low (cosmetic tofu, but trivially fixable)
**File:** `GitBench/Controls/TextMeasure.cs:24-34`

The binary search slices `text[..lo]` without checking whether `lo` falls inside a surrogate pair.

**The correct implementation already exists** in `framework/ZGF.Gui/Views/TextView.cs:199`
(`Ellipsize`), which guards it:

```csharp
// Never cut inside a surrogate pair: a low surrogate at mid means the
// prefix would end on an orphaned high surrogate (renders as tofu).
if (mid < text.Length && char.IsLowSurrogate(text[mid]))
    mid--;
if (mid <= lo)
    break;
```

**Fix:** copy the guard — better, extract the shared truncation into one place so the two cannot
drift again.

---

## BUG-5 — Ctrl+Arrow treats an entire CJK sentence as one word

**Severity:** low (UX papercut)
**File:** `framework/ZGF.Gui.Desktop/Components/TextInput/TextInputView.cs:736`

```csharp
private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
```

Every CJK ideograph is a letter and CJK doesn't use spaces, so `FindNextWordBoundary` skips the whole
run: Ctrl+Right jumps from the start of a Chinese sentence to its end. Same for double-click
word-select.

**Fix:** treat a transition into/out of an ideographic range as a word boundary (`TextWrapper.IsWide`
already classifies these), so Ctrl+Arrow moves per character within CJK. Proper morphological
segmentation is out of scope — per-character is what most editors do and is a large improvement over
"the whole paragraph".

---

## BUG-6 — The glyph atlas silently drops glyphs when full

**Severity:** medium (latent; CJK makes it reachable)
**File:** `framework/ZGF.Fonts/GlyphAtlas.cs:47` (`TryReserve`), sized at
`framework/ZGF.Fonts/FreeTypeFontBackend.cs:21` (2048×2048)

One single-channel skyline-packed atlas, **no eviction and no growth**. `TryReserve` returns `false`
when full and the glyph is simply not drawn — no error, no log, no fallback. Latin needs a couple
hundred glyphs, so this has never been hit. CJK needs thousands, multiplied across sizes (UI 16px,
mono 13px) and synthetic-bold variants.

**Fix:** grow the atlas (or add a second page / LRU eviction) and — regardless of which — **log or
signal exhaustion** instead of rendering nothing. Silent tofu is the worst failure mode here because
it looks like a font-coverage bug.

---

## Audit (unconfirmed — worth a look, lower confidence)

These index or `Substring` by `char` and may have the same surrogate hazard. I did not confirm a
concrete repro:

- `GitBench/Infrastructure/PathWrap.cs`
- `GitBench/Features/Diff/DiffRowPainter.cs`

Also worth knowing (not a bug, but an inconsistency): the vendored GLFW natives are **version-skewed**
— `win-x64` is 3.3.7 while `osx-*` is 3.4.0. `docs/plans/cjk-ime-support.md` Phase 1 unifies them.

---

## Suggested order

BUG-1 and BUG-2 together (they share the boundary helper, and BUG-2 is the one that can put broken
text into a real commit message), then BUG-3 (most user-visible), then BUG-4/BUG-5 (cheap), then
BUG-6 (needs a design call on grow vs. evict).

All of BUG-1 through BUG-5 are unit-testable headlessly through `GuiTestHarness` — see
`framework/ZGF.Gui.Tests/TextInputUnicodeTests.cs` for the existing pattern.
