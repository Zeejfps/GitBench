# Markdown renderer — rich text for the assistant transcript

> Plan for a markdown renderer that displays LLM chat responses in the assistant overlay
> (`docs/plans/assistant.md`, Phase 3's `TranscriptRow`). **Framing:** LLMs answer in markdown, so
> the transcript needs paragraphs with inline styling, code blocks, lists, and tables — not a
> `TextView` dump. The renderer is built as a reusable widget, independent of the assistant, so it
> can later serve release notes, commit descriptions, or anything else markdown-shaped. No v1/v2
> split: real italics, proper tables, and a parser seam land from the start.

## Decisions

Every row below is a settled call, not a default.

| Area | Decision |
|---|---|
| Location | Everything in this repo: `GitBench/Features/Markdown/`. Nothing lands in the `framework` submodule. |
| Parser | Hand-rolled `BasicMarkdownParser` behind an `IMarkdownParser` seam. The AST is owned by GitBench and parser-agnostic — a future Markdig (or other) backend is an adapter producing the same AST, renderer untouched. |
| Dialect | GFM-flavored subset (see Scope). Unsupported syntax degrades to literal text, never an error. |
| Inline model | The AST's inline layer is **flat styled runs** (`InlineRun`), not a nested emphasis tree. The parser resolves nesting; the renderer only ever sees runs. This is the seam's contract. |
| Italics | Real font: `Inter-Italic.ttf` embedded in `GitBench/Assets/Fonts/Inter/`, registered as its own family (`"inter-italic"`) in `AppHostSetup.UseAppFonts`. Italic = family swap; bold-italic = italic family + the existing synthetic embolden (`FontWeight.Bold`). No framework change. |
| Tables | Proper GFM pipe tables with auto column sizing (min/max-content, CSS-table-style distribution), per-column alignment, wrapped cells, `HorizontalScrollArea` fallback when min widths overflow. |
| Code blocks | Themed box, JetBrains Mono, syntax highlighting via the existing `SyntaxHighlighter` + `LanguageRegistry` from the fenced info string, copy-to-clipboard button. |
| Links | Styled + underlined, hover feedback, click opens via `IPlatformShell.OpenUrl`. |
| Selection | Not in scope. Copy buttons on code blocks (here) and per-message copy (assistant plan) cover the chat use case. The run/segment model keeps a future selection layer possible. |
| Streaming | Full re-parse per delta, throttled to the frame tick; unchanged blocks keep their views via structural equality; unterminated constructs render as in-progress, not as literal backticks/pipes. |
| Theming | New `MarkdownStyles` slot in `ThemeStyles`, defined for both palettes in `ThemeStyles.Dark.cs` / `ThemeStyles.Light.cs`. |

## Scope

**Blocks:** ATX headings (`#`–`######`), paragraphs, fenced code blocks (info string → language),
unordered/ordered lists (nested, `start` honored), GFM task-list items, blockquotes (nestable),
thematic breaks, GFM pipe tables.

**Inlines:** bold, italic, bold-italic, inline code, strikethrough, links `[text](url)`, bare-URL
autolinks, backslash escapes, hard breaks.

**Deliberately out:** setext headings, indented code blocks (LLMs use fences), raw HTML (rendered
as literal text), images (rendered as their link), footnotes, reference-style links. Each degrades
readably; none should ever throw.

## Architecture

```
GitBench/Features/Markdown/
  Parsing/
    IMarkdownParser.cs        seam: Parse(string) -> MarkdownDocument
    MarkdownAst.cs            block + inline records, parser-agnostic, structural equality
    BasicMarkdownParser.cs    line-based block scanner
    InlineParser.cs           emphasis/code/link resolution -> flat InlineRun lists
  Rendering/
    RichTextLayout.cs         run-aware wrap engine (shared by paragraphs and table cells)
    RichTextView.cs           custom view: draws positioned segments, link hit-testing
    RichText.cs               widget wrapper (the app-code API)
    MarkdownWidget.cs         Markdown prop -> Column of block widgets
    CodeBlockWidget.cs        mono box + SyntaxHighlighter runs + copy button
    MarkdownTableView.cs      auto table layout over RichTextLayout cells
    MarkdownTable.cs          widget wrapper
    LinkController.cs         hover cursor + click -> IPlatformShell.OpenUrl
  MarkdownBlockList.cs        stable-identity block list for streaming (ObservableList diff)
GitBench/Theming/ThemeStyles.Markdown.cs
GitBench/Assets/Fonts/Inter/Inter-Italic.ttf (+ LICENSE-Inter.txt)
GitBench.Tests/Markdown/…
```

Placement follows existing precedent:

- **Custom views are an accepted app-level pattern** — `DiffContentView` and `DiffRowPainter`
  already draw styled runs directly on `ICanvas` in GitBench. `RichTextView` and
  `MarkdownTableView` are the same species, wrapped in widgets per `WIDGETS.md` (app code composes
  widgets; only widgets construct views).
- **The wrap engine mirrors `TextWrapper`.** `RichTextLayout` reimplements the greedy
  UAX-14-lite wrap over *styled runs* (per-run `ICanvas` measurement, breaks allowed at run
  boundaries), producing positioned segments. `TextWrapper.WrapRanges` is the reference for break
  opportunities, kinsoku, and CJK handling — behavior must match for single-style input.
- **Caching mirrors `TextView`** — layout is recomputed only when width or content changes
  (`_wrappedForWidth` pattern), because measure/draw run every frame.
- **Syntax highlighting is reused, not rebuilt.** `SyntaxHighlighter.Highlight(text, languageId)`
  already returns per-line `TokenSpan(Start, Length, TokenColorSlot)` and `ScopeColorMap` resolves
  slots per theme; `LanguageRegistry` maps the fence info string to a grammar.
- **Bidi:** paragraph direction defers to the canvas default like all other text; code blocks and
  tables pin `BaseDirection.Ltr` the way `DiffRowPainter.MonoMetricsStyle` does.

## The AST (the seam's contract)

Records with structural equality (needed by streaming):

- `MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks)`
- Blocks: `HeadingBlock(int Level, Runs)`, `ParagraphBlock(Runs)`,
  `CodeBlock(string? Language, string Text, bool IsClosed)`,
  `ListBlock(bool Ordered, int Start, IReadOnlyList<ListItem> Items)`,
  `ListItem(IReadOnlyList<MarkdownBlock> Blocks, bool? TaskChecked)`,
  `QuoteBlock(IReadOnlyList<MarkdownBlock> Blocks)`, `ThematicBreakBlock`,
  `TableBlock(IReadOnlyList<ColumnAlignment> Columns, HeaderCells, IReadOnlyList<RowCells> Rows)`
- Inline: `InlineRun(string Text, bool Bold, bool Italic, bool Code, bool Strikethrough,
  string? LinkUrl)` — flat, pre-resolved. A table cell is `IReadOnlyList<InlineRun>`.

Swapping parsers later means writing one adapter that flattens the other parser's inline tree into
`InlineRun`s. The renderer, theming, and tests all sit on this contract.

## Implementation steps

Each step compiles, has tests, and is independently reviewable.

### Step 1 — AST + seam + block parser (pure, no UI)

`MarkdownAst.cs`, `IMarkdownParser`, and `BasicMarkdownParser` covering block structure only
(inline text kept as a single unstyled run): headings, paragraphs, fences (unterminated →
`IsClosed = false`), lists with nesting and task states, blockquotes, thematic breaks, and pipe
tables (header + delimiter + rows, alignment parsing). Table detection requires header and
delimiter rows; anything less stays a paragraph.

Tests in `GitBench.Tests/Markdown/` — table-driven markdown→AST cases, including the degradation
paths (raw HTML, setext-looking input, malformed tables) and a fuzz-ish "never throws on any
prefix of a valid document" test, which streaming depends on.

### Step 2 — inline parser

`InlineParser`: emphasis (`*`/`**`/`***`/`_`), inline code (backtick runs, code wins over
emphasis), strikethrough, links, autolinks, escapes, hard breaks — resolved into flat `InlineRun`
lists. Runs merge when adjacent with identical style. Pure, table-driven tests; CommonMark's
emphasis corner cases trimmed to the subset we claim.

### Step 3 — Inter Italic

Add `Inter-Italic.ttf` (same Inter release as the framework's `Inter-Regular`, OFL license file
alongside, embedded like the JetBrains Mono resource) and register it in
`AppHostSetup.UseAppFonts` as family `"inter-italic"` at the same base size as the default face.
A constant (`MarkdownFonts.ItalicFamily`) next to the renderer names it. Verify via `/verify`:
italic and bold-italic sample text renders with true italic outlines, and synthetic embolden on
the italic face looks acceptable.

### Step 4 — `RichTextLayout` + `RichTextView` + links

The core new primitive.

- `RichTextLayout`: input `(runs, maxWidth, canvas)` → lines of positioned segments
  `(run index, text slice, x, width)`. Greedy wrap with per-run styles; break behavior identical
  to `TextWrapper` for single-style input (shared test corpus proves it). Exposes measured height
  for the view's intrinsic-measure overrides.
- `RichTextView`: mirrors `TextView`'s measure/cache/draw shape; draws inline-code chip
  backgrounds (rounded rect behind code segments), underlines for links, then per-segment
  `DrawText`. Keeps segment rects for hit-testing.
- `LinkController`: hover → pointer cursor + hover color; click → `IPlatformShell.OpenUrl`.
- `RichText` widget wrapper; style inputs come from theme, not hardcoded.

Tests via `GuiTestHarness`/`ZGF.Gui.Testing`: wrap parity with `TextWrapper`, mixed-style wrap
snapshots, link hit-test geometry, cache invalidation on width/content change.

### Step 5 — block rendering + theming

`MarkdownWidget`: AST → `Column` of block widgets.

- Headings: `FontSize` ladder off the existing scale (`Title`/`Heading`/`Default`…), bold.
- Lists: bullet/number/checkbox gutter + nested `Column`; task states render Lucide glyphs,
  display-only.
- Blockquotes: accent bar + inset children, nestable.
- Thematic break: 1px themed rule.
- `CodeBlockWidget`: themed box, mono runs colored via `SyntaxHighlighter` +
  `ScopeColorMap` (skip highlighting while `IsClosed` is false and for unknown languages),
  horizontal scroll for long lines, copy button (existing clipboard service).
- `ThemeStyles.Markdown.cs`: link, code chip fg/bg, code block bg/border, quote bar/text, rule,
  table border/header colors — defined in both `Dark` and `Light`.
- Localization: copy-button tooltip/label added to all six `Strings/*.json` (LOC004 enforces).

Widget tests with the harness; visual pass over both themes via `/verify`.

### Step 6 — tables

`MarkdownTableView` + `MarkdownTable`:

- Column sizing: per column, min-content (widest unbreakable segment) and max-content (unwrapped)
  widths measured through `RichTextLayout`. Fit: all-max if it fits; otherwise min + proportional
  distribution of the remainder by (max − min); if Σmin still overflows, the table nests in the
  existing `HorizontalScrollArea` at min widths.
- Cells wrap via `RichTextLayout`; per-column alignment from the delimiter row; header row bold
  with a heavier rule; row separators from theme. Layout caches against (width, content) like
  `TextView`.

Tests: sizing math as pure unit tests (fake measurer), geometry tests through the harness,
degenerate cases (ragged rows, empty cells, one-column, 15-column).

### Step 7 — streaming

`MarkdownBlockList`: holds an `ObservableList` of block rows keyed by structural equality.
On each text update (throttled to the frame tick / ~30 Hz): re-parse, diff against current blocks,
mutate only the changed tail — in the common streaming case exactly the last block, so completed
blocks' views (and their layout caches) survive untouched. The transcript binds it with `Each`,
per the assistant plan's reactive-list rule.

In-progress constructs render as their eventual shape: open fence → live code block, header+
delimiter-only table → table. Tests: replay a recorded response as growing prefixes; assert no
exceptions, monotone block identity for the completed prefix, and final AST equal to one-shot
parse.

### Step 8 — integration hooks + AOT gate

A minimal preview entry (debug-only window or `GitBench.Automation` scene) that renders a fixture
document exercising every construct — the surface `/verify` drives and future changes regress
against. Publish a Release/NativeAOT build and run the preview: the parser and renderer are plain
code with no reflection, but this repo's history (`libgit2sharp-callback-apis-crash-aot`) says AOT
proof happens early, not at the end. Hand-off: `MarkdownWidget` is what the assistant's
`TranscriptRow` embeds; `MarkdownBlockList` is what its streamed-turn VM feeds.

## Estimates

| Step | Effort |
|---|---|
| 1. AST + block parser | 1–1.5 d |
| 2. Inline parser | 1 d |
| 3. Inter Italic | 0.5 d |
| 4. RichTextLayout / RichTextView / links | 2–3 d |
| 5. Block rendering + theming | 1.5 d |
| 6. Tables | 2 d |
| 7. Streaming | 1 d |
| 8. Preview + AOT gate | 0.5 d |
| **Total** | **≈ 9–11 d** |

## Cross-cutting

- **Localization:** every user-visible string in all six `Strings/*.json` or the build fails
  (`LOC004`).
- **Build checks:** `dotnet build GitBench/GitBench.csproj --artifacts-path <scratchpad>` —
  isolated outputs, never default `obj/bin`, never touch a running app.
- **No reflection, no new dependencies:** parser and layout are plain C#; AOT-safe by
  construction, proven in Step 8.
- **Performance budget:** re-parse of a 10 KB message under ~1 ms; layout only on width/content
  change; draw allocates nothing per frame (segment lists reused, `DiffRowPainter`'s shared-style
  mutation pattern where applicable).

## Risks

1. **Run-aware wrapping regressions.** `TextWrapper` encodes non-obvious behavior (CJK breaks,
   kinsoku, segment-boundary breaks for paths/URLs). Mitigated by sharing its test corpus and
   asserting single-style parity in Step 4 — parity is a gate, not an aspiration.
2. **Emphasis edge cases.** CommonMark emphasis is famously fiddly (`**a *b** c*`…). The subset is
   scoped and table-driven-tested; anything outside it must degrade to literal text, and the seam
   means a full CommonMark parser can replace `BasicMarkdownParser` without touching rendering.
3. **Table sizing pathologies.** Long unbreakable tokens (URLs, hashes) blow up min-content
   widths. The `HorizontalScrollArea` fallback bounds the damage; the sizing tests include these
   shapes explicitly.
4. **Streaming churn.** If block identity is too unstable (e.g. a growing paragraph never compares
   equal), every delta rebuilds the tail anyway — correct but wasteful; the throttle keeps it
   invisible. Measured in Step 7 with the replay test.
5. **Synthetic bold-italic.** Bold-italic uses embolden on the italic face; if it reads poorly at
   13 px, the escape hatch is embedding `Inter-BoldItalic.ttf` as a fourth face — an asset
   addition, no code change.
