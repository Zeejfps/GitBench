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
