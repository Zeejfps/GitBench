# PRD: Syntax Highlighting in DiffView (TextMateSharp)

## Problem Statement

When I view a file diff in GitBench, every line of code renders in a single
flat color. For anything more than a trivial change this makes the diff hard to
read: I can't quickly distinguish keywords, strings, comments, types, or
identifiers, so I lose the at-a-glance structure I get in my editor or in other
Git GUIs (Fork, Sublime Merge, GitHub Desktop). The current `DiffView` draws the
whole line text with one `DrawText` call in one color, with no notion of the
file's language.

I want the diff to be syntax-highlighted per language, picked automatically from
the file extension, and I want it built so that adding more languages later is
easy-ish rather than a per-language coding effort.

## Solution

Add per-token syntax highlighting to the diff body, driven by **TextMateSharp**
(MIT; the same TextMate-grammar engine behind AvaloniaEdit). Languages are data
(TextMate grammars), so new languages are added by registering a grammar rather
than writing a lexer by hand.

Because a diff only shows fragments of a file (interleaved `+`/`-`/context
lines), highlighting is done in **whole-file mode**: GitBench already has access
to both file blobs, so for each diff we reconstruct/fetch the full old and new
file contents, tokenize each whole file top-to-bottom (threading TextMateSharp's
line state so multi-line comments and strings are correct), and map the
resulting per-line token spans back onto the visible diff rows by line number.
This is the approach GitHub Desktop documents and rejects per-line highlighting
for; it fits GitBench cleanly because `DiffLine` already carries old/new line
numbers.

TextMate scopes (`keyword`, `string`, `comment`, …) are mapped to a small,
curated set of token-color slots resolved against the existing `ThemeStyles`
palette, so highlighting tracks the active light/dark theme. The feature is
toggleable via `DiffOptions` and **on by default**; unsupported languages,
oversized files, or any tokenization failure fall back silently to the current
plain rendering.

The prototype ships with **C#** and **TypeScript** support.

## User Stories

1. As a developer reviewing a diff, I want code tokens (keywords, strings,
   comments, numbers, types, functions) colored, so that I can read changes at a
   glance instead of parsing a wall of one-color text.
2. As a developer, I want the language detected automatically from the file
   extension, so that I don't have to pick a language per file.
3. As a developer viewing a C# (`.cs`) diff, I want correct C# highlighting, so
   that the prototype is useful on this codebase itself.
4. As a developer viewing a TypeScript (`.ts`/`.tsx`) diff, I want correct
   TypeScript highlighting, so that the second prototype language is validated.
5. As a developer, I want a multi-line comment or string that starts above the
   visible hunk to still be colored correctly inside the hunk, so that
   highlighting doesn't visibly break at hunk boundaries.
6. As a developer, I want added, removed, and context lines all highlighted, so
   that the highlighting is consistent across the whole diff and not just one
   side.
7. As a developer, I want removed lines highlighted using the *old* version of
   the file and added/context lines using the *new* version, so that each line's
   colors reflect the code it actually came from.
8. As a developer, I want the diff's add/remove background tints and the
   syntax-highlight foreground colors to coexist legibly, so that I can still
   tell added/removed lines apart while reading colored tokens.
9. As a developer using a light or dark theme, I want token colors that match my
   theme, so that highlighting looks native rather than pasted-in.
10. As a developer opening a file whose language isn't supported yet, I want the
    diff to render exactly as it does today (plain), so that the feature never
    makes any file worse.
11. As a developer, I want a setting to turn syntax highlighting off, so that I
    can revert to plain rendering if I prefer it or hit an issue.
12. As a developer rapidly clicking through many files, I want highlighting to
    compute off the UI thread and never block scrolling or selection, so that the
    app stays responsive.
13. As a developer switching files quickly, I want in-flight highlighting for a
    file I navigated away from to be discarded, so that stale colors never land
    on the wrong diff.
14. As a developer opening a very large file, I want highlighting to be skipped
    above a size/line cap (falling back to plain), so that a huge file can't hang
    or bog down the diff.
15. As a developer, I want a single pathological line to not stall the whole
    diff, so that one weird line can't freeze rendering (per-line tokenize
    timeout, fall back to plain for that file/line).
16. As a developer viewing a binary file, a mode-only change, a rename with no
    content change, or an empty diff, I want highlighting to be a no-op, so that
    those existing states are unaffected.
17. As a developer, I want tabs to stay aligned after highlighting, so that token
    colors line up with the tab-expanded text the diff already renders.
18. As a developer, I want highlighting to work for committed diffs, staged
    diffs, and unstaged/working-tree diffs, so that every diff side benefits, not
    just commits.
19. As a maintainer, I want adding a new language to mean "register a grammar +
    map its scopes," so that growing language coverage is cheap.
20. As a maintainer, I want the highlighting logic (language detection, scope→
    color mapping, tokenization, span-to-line mapping) to live in small testable
    modules separate from rendering, so that I can unit-test correctness without
    a GPU.
21. As a maintainer, I want the TextMateSharp native dependency (Oniguruma) to be
    packaged per supported platform, so that the app still builds and runs on
    each target OS.
22. As a developer, when highlighting fails for any reason, I want the diff to
    degrade to plain rendering rather than show an error or blank, so that
    failures are invisible and safe.

## Implementation Decisions

**Engine & strategy**
- Use **TextMateSharp** + bundled TextMate grammars for C# and TypeScript. MIT
  licensed. Ships a native Oniguruma regex engine that must be packaged per
  platform.
- **Whole-file tokenize + map-back** strategy (not per-hunk, not per-line):
  fetch full old and new file contents, tokenize each whole file once with
  TextMateSharp threading its per-line `IStateStack`, then attach token spans to
  diff rows by line number. Removed lines use old-file spans; added/context lines
  use new-file spans. Fetch only the side(s) actually needed (pure-add and
  pure-delete diffs need one side).

**New modules (deep where possible, isolated from rendering)**
- **`LanguageRegistry`** — pure mapping from file path/extension to a grammar id
  (or "unsupported"). Table-driven; the single place new languages are
  registered. Initial entries: `.cs`, `.ts`, `.tsx`.
- **`SyntaxHighlighter`** — the only module that touches TextMateSharp. Interface
  is roughly: given full file text + grammar id, return a per-line list of token
  spans, where each span is `(startColumn, length, tokenColorSlot)`. Internally
  loads the grammar, tokenizes line-by-line threading `IStateStack`, and resolves
  each token's scope to a color slot via `ScopeColorMap`. Enforces the per-line
  tokenize timeout and the file-size cap; returns "no highlighting" on cap/
  failure.
- **`ScopeColorMap`** — pure mapping from a TextMate scope string to a small enum
  of token-color slots (e.g. Keyword, String, Comment, Number, Type, Function,
  Variable, Operator, Punctuation, Constant, Default). Longest-scope-prefix wins.
- **`DiffHighlightCoordinator`** — orchestrates per diff: use `LanguageRegistry`
  to detect language; if supported and under cap, fetch needed blobs via the
  existing `GitService`, run `SyntaxHighlighter`, and produce per-line span
  lookups keyed by old and new line number. Runs asynchronously and is
  generation-guarded (consistent with the existing `GenerationGuard` pattern) so
  navigating away discards stale results. On any failure, yields "no spans" and
  the view renders plain.

**Edits to existing code**
- `DiffRow.Line` gains an optional per-line spans field (token color runs for
  that line's text). Default null/empty → plain rendering, identical to today.
- `DiffContentView.FlattenRows` looks up each line's spans by `NewLineNumber`
  (added/context) or `OldLineNumber` (removed) and attaches them; span columns
  are mapped through the same tab-expansion (`ExpandTabs`) the text already
  undergoes so colors stay aligned.
- `DiffContentView.DrawLineRow` replaces the single whole-line `DrawMonoText`
  call with one call per span when spans exist, positioning each at
  `textStart + startColumn * _monoAdvance` (safe because the font is monospace
  and batches into one GPU draw call regardless of span count). No spans →
  current single-call path.
- `DiffContentStyles` / `ThemeStyles.Diff.cs` gain the curated token color slots
  per palette (light + dark).
- `DiffOptions` gains a `SyntaxHighlightingEnabled` toggle, default **true**.

**Theming**
- Curated scope→palette mapping (~10–15 slots), resolved at draw time against the
  active theme. No external TextMate theme `.json` colors are used directly.

**Behavior / limits**
- File-size/line-count cap above which highlighting is skipped (target ~256 KB,
  consistent with GitHub Desktop's heuristic; exact number tunable).
- Per-line tokenize timeout to bound Oniguruma backtracking worst case.
- Silent fallback to plain rendering for: unsupported language, over-cap file,
  binary/mode-only/rename-only/empty diffs, blob-fetch failure, tokenize failure.
- No change to diff add/remove background tints; syntax colors are foreground
  only and must remain legible over those tints.

## Testing Decisions

A good test here asserts **external behavior** — given inputs, the produced spans
/ mappings — not TextMateSharp internals or rendering. The renderer itself
(`DiffContentView` drawing) is verified manually, not unit-tested.

Note: the repository currently has **no automated unit-test framework**
(`ZGF.Gui.Tests` is a manual visual sandbox WinExe, not a test suite). This PRD
therefore introduces a small **xUnit** test project for the new logic modules as
a prerequisite. There is no in-repo prior art for unit tests; the test project
follows standard xUnit conventions.

Modules to unit-test:
- **`LanguageRegistry`** — extension/path → grammar id, including unsupported
  paths, case-insensitivity, and the `.cs`/`.ts`/`.tsx` mappings.
- **`ScopeColorMap`** — representative scope strings map to the correct color
  slot (e.g. `keyword.control.cs` → Keyword, `string.quoted.double.ts` → String,
  `comment.block` → Comment), and unknown scopes fall back to Default.
- **`SyntaxHighlighter`** — the high-value correctness tests: a multi-line block
  comment and a multi-line string produce comment/string-colored spans on every
  line they cover (proving `IStateStack` is threaded), single-line constructs are
  colored correctly, the size cap and per-line timeout yield "no highlighting,"
  and an unknown grammar is handled gracefully. Runs the real TextMateSharp
  engine in-test.
- **`DiffHighlightCoordinator`** — span-to-line mapping logic with fake spans
  (blob fetch substituted/excluded): added/context lines resolve to new-side
  spans, removed lines to old-side spans, and tab-expansion column mapping keeps
  span offsets aligned. Generation-guard discards stale results.

End-to-end verification is **manual visual checking** on real C# and TypeScript
diffs in the running app, specifically confirming multi-line comments/strings
that span hunk boundaries are colored correctly, add/remove tints remain
distinguishable, and unsupported files render unchanged.

## Out of Scope

- **Intra-line (word-level) diff highlighting** — emphasizing the specific
  changed characters within a line is a separate feature/PRD.
- **Languages beyond C# and TypeScript** — the architecture makes them cheap to
  add later (register grammar + scopes), but only C#/TS ship in this prototype.
- **Semantic / compiler-accurate highlighting** (e.g. Roslyn for C#) — TextMate
  grammar lexing only; no type-aware coloring.
- **User-configurable token colors / custom theme editor** — colors come from the
  curated palette mapping; no per-user customization UI.
- **Using TextMateSharp's own `.json` themes** — colors come from `ThemeStyles`.
- **An automated rendering/snapshot harness** — rendering is verified manually.
- **Re-highlighting incrementally as the working tree changes** — highlighting is
  recomputed per diff load.

## Further Notes

- Why whole-file over per-hunk/per-line: per-line highlighting visibly breaks on
  multi-line comments and strings (the failure mode GitHub Desktop documents).
  Whole-file is correct and is a natural fit because a Git client already has both
  blobs and `DiffLine` already carries old/new line numbers, making token→line
  mapping straightforward.
- Why TextMateSharp over alternatives: Tree-sitter has the most mature output but
  immature .NET bindings and wants whole valid files; AvalonEdit `.xshd` has few
  ready-made grammars and is WPF-coupled; ColorCode requires hand-writing each
  language in C#. TextMateSharp gives the broadest set of **languages-as-data**,
  is MIT, is production-proven (AvaloniaEdit's engine), and exposes per-line
  tokens with a resumable state stack.
- Rendering cost is a non-issue: the canvas batches all glyphs (across any number
  of `DrawText` calls) into a single instanced GPU draw, and per-glyph color is
  already part of the vertex data — so multiple colored spans per row cost
  effectively nothing extra.
- Packaging: the TextMateSharp native Oniguruma binary must be included for each
  shipped platform.
- Research sources: GitHub Desktop syntax-highlighting architecture doc,
  Sublime Merge `.sublime-syntax` engine, JetBrains restartable-lexer docs,
  TextMateSharp / TextMateSharp.Grammars.
