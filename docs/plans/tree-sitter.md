# Tree-sitter in GitBench

Adding real parsing to a git client, and the six things it buys that a regex
cannot.

---

## 1. Why

GitBench reads code all day and understands none of it. Everything structural it
currently shows is a guess:

- **Hunk headers** are git's `xfuncname` — a per-language regex, configured in
  `.gitattributes`, blank or wrong most of the time. `GitService.TryParseHunkHeader`
  takes whatever text trails the closing `@@` and shows it verbatim.
- **Syntax colors** come from TextMate grammars — regex state machines. Good
  enough for color, structurally blind.
- **Context expansion** reveals a fixed number of lines, because lines are the
  only unit available.

A parser changes what the app can say. Tree-sitter is the right one: error
tolerant (it parses a file mid-edit and a file with a syntax error), fast enough
to run per-file on a background lane, and already wrapped for us in
`cs_tree_sitter`.

**The scope discipline that makes this cheap:** every capability below is
answered by parsing **one file**. No cross-file resolution, no repository index,
no background sweep, no cache invalidation, no persistence. That is not a
limitation we are working around — it is the line that separates the parts of
this that are a week of work from the parts that are a quarter.

---

## 2. What it buys, in the order we should build it

| # | Capability | Why it matters here |
| --- | --- | --- |
| A | **Real hunk headers** | Every hunk says `AuthService.Login(string)` instead of nothing. Highest daily impact, smallest change — `DiffHunk.Header` is already rendered. |
| B | **File outline and jump** | Know where you are in a long file; jump to a declaration. |
| C | **Symbol-level change summary** | "This commit touched `Login()`, added `TokenCache`, removed `LegacyAuth`" — the review question, answered directly. |
| D | **Expand to the enclosing declaration** | Replaces "reveal 20 more lines" with "reveal the method". |
| E | **Assistant context** | Hand the model the enclosing signature and the file's shape, not raw line ranges. |
| F | **Folding** | Collapse a body you are not reading. The only capability that touches the row stream. |
| G | **Moved-code detection** | A method moved 200 lines reads as *moved*, not as a delete plus an add. |

A through F are the plan. G is sketched and explicitly deferred — see §10.

### What this deliberately does not do

- **No "N usages" counts.** Counting uses across a repository needs
  name-to-declaration binding, which tree-sitter cannot do. A syntactic count is
  wrong in both directions at once — it over-counts every same-named member and
  silently under-counts every bare-identifier read. That is a feature that lies
  quietly, which is worse than not having it. If we ever want it properly for
  C#, the honest route is Roslyn, and that is its own decision.
- **No tree-sitter highlighting yet.** Highlighting works, and changing the
  tokenizer under every colored surface in the app is unrelated work with its
  own risk. It becomes worth revisiting the moment Capability A lands, because
  from then on we parse the file anyway — see §10's Capability H, which is a
  measurement first and a decision second.
- **No back/forward navigation history.** Worth doing, unrelated to parsing,
  belongs in its own plan.

---

## 3. The submodule, and why it is one

`https://github.com/Zeejfps/cs_tree_sitter` comes in as a git submodule — **not**
a package, **not** a vendored copy, because we expect to change it. If the
bindings turn out to be missing something we need, we add it there rather than
working around the gap on this side. Same author, one commit away.

That makes the boundary a live design question, so it is stated up front:

**Belongs upstream, in `cs_tree_sitter`:**
- Anything true for any tree-sitter consumer: a missing `ts_*` declaration in
  `TS.cs`, an unimplemented query predicate, a `Node` convenience the wrapper
  should obviously have, a `SafeHandle` or ABI bug.
- A new bundled grammar (one entry in the `grammars` array in `native/build.cs`).
- Anything needing `unsafe`. The submodule's stated contract is that nothing
  above `TreeSitter` needs it; this work must not be what breaks that.
- Build support for a RID we ship, including the `NativeArtifactsRid` fix in
  Phase 0 (§11), which cannot live on our side.

**Stays here, in GitBench:**
- The capture protocol and the `.scm` files. What counts as a declaration is a
  GitBench decision, not a fact about tree-sitter — repodex has its own set,
  deliberately different.
- `SymbolKind`, `FileOutline`, every diff and UI concern.
- Anything that would make the submodule know what a "hunk" or a "repository" is.

**The cost, so it is not reached for casually:** two commits (one in the
submodule, one bumping the pointer), and CI only sees it when the pointer moves.
Build it here first; move it up when a second consumer wants it. Budget **two
upstream PRs in Phase 0 alone** — the `Native.props` RID fix and a CI workflow —
plus one or two during Phase 1 for node accessors the wrapper may not expose yet.

**Where it goes.** `vendor/` currently means *vendored and patched sources* —
`vendor/XtermSharp.Vendored` with a `PATCHES.md` — and `.gitmodules` holds only
`framework`, which sits at the repo root. Putting a submodule in `vendor/` mixes
two conventions in one folder. `external/cs_tree_sitter` is the cleaner home;
`vendor/` is acceptable if we would rather have one place to look. Decide before
Phase 0, because the path is baked into every `.csproj` import.

---

## 4. The extraction layer

### 4.1 What comes out

```csharp
sealed record OutlineNode(
    string Name,
    SymbolKind Kind,
    string? ParameterTypes,  // "string, int" for callables; null otherwise
    int StartLine,           // 1-based inclusive
    int EndLine,             // 1-based inclusive
    int SignatureEndLine,    // where the signature stops
    IReadOnlyList<OutlineNode> Children);

sealed record FileOutline(IReadOnlyList<OutlineNode> Roots);
```

**Spans are the declaration, and nesting is not derived from them.** A
`file_scoped_namespace_declaration` ends at its semicolon but scopes the rest of
the file. If `StartLine`/`EndLine` became that extent, `namespace Foo;` would
report as spanning the whole file and every jump target, breadcrumb and fold
range built from it would be wrong. So:

- `StartLine`/`EndLine` are always **the declaration itself**, which is what a
  reader is shown and scrolled to.
- The **extent** — a wider span used only to decide what contains what — is
  consumed *inside* the extractor while building the tree and never appears on
  `OutlineNode`. This is repodex's arrangement, where extents live on an
  internal record and never reach the public symbol.
- Therefore `EnclosingAt(line)` **walks children**; it is not a span test
  against a node's own range. Every consumer that wants "the innermost
  declaration containing line N" goes through that one method.

**`StartLine` excludes attributes.** In tree-sitter's C# grammar `attribute_list`
is a child of the declaration node, so the node's own start row is the
`[Obsolete]` line, not the signature. Left alone, folding would put its chevron
several rows above the thing it folds and the breadcrumb would name a
declaration a line before it visibly starts. The extractor skips leading
`attribute_list` children when computing `StartLine`. Tested.

**`SignatureEndLine` is `EndLine` when there is no body.** An abstract method,
an interface member, a positional record and a delegate all declare without one,
and `@body` is optional. Setting it to `EndLine` makes them correctly
not-foldable (§9.3's predicate is `SignatureEndLine < EndLine`). The obvious
alternative, `StartLine`, would make a multi-line abstract signature spuriously
foldable and collapse part of its own signature.

**`ParameterTypes` exists so overloads are distinguishable.** Two `Login`
overloads are two declarations, and `(Kind, Name)` alone conflates them: §7
would report an added overload as a modification of the existing one, §5 would
give two different hunks the same header, and §9.4's fold signature would not
notice a reordering. It also lets §2's promised `AuthService.Login(string)` be
rendered rather than quietly downgraded. Normalized types only — no parameter
names, no modifiers — because it is an identity key, not a signature renderer.

Lines are 1-based and inclusive; tree-sitter's 0-based rows are converted
exactly once, here.

**There is no `Column` field.** A `TSPoint` column is a UTF-8 *byte* column,
which does not line up with the tab-expanded character columns `DiffText`,
`TokenSpan` and `DiffRowPainter` work in. Nothing in §2 needs one, and shipping
it would be a field whose name lies about its unit.

**Why a tree rather than repodex's flat list.** repodex stores symbols in
SQLite, and a flat list ordered outermost-first with a backward-only
`ParentIndex` turns into row ids in one forward pass. We have no persistence, so
that constraint buys nothing here, and a tree makes a forward or self reference
unrepresentable rather than merely conventionally absent.

`SymbolKind`: `Namespace, Class, Struct, Interface, Record, Enum, Method,
Constructor, Property, Event, Field, EnumMember, Function, Type`.

### 4.2 The seam

```csharp
internal interface ISymbolExtractor
{
    CodeIntelAvailability Availability { get; }
    FileOutline? Extract(string text, CodeLanguage language);
}
```

**Availability is represented once.** `Availability` answers "is parsing
possible at all" — a UI asks it once to decide whether to show a fold chevron or
an outline button. `Extract` returns null only for *this file has nothing to
say*: unsupported language, over cap, parse produced no declarations. Never
throws. Two mechanisms meaning the same thing would leave callers guessing which
to switch on.

`string` in, not `ReadOnlySpan<byte>`: the output is line numbers, not byte
offsets, so there is no offset arithmetic to keep in the source encoding, and
`IGitDiffReader.GetFileText` already hands over a string. The extractor encodes
to UTF-8 once internally and reads `StartPoint.Row` / `EndPoint.Row`.

Two callers need a note. `FileContentLoader` produces
`IReadOnlyList<string> Lines`, not text — so the Files-view path either joins
(a full-file allocation) or `FilePreview.Text` grows a raw-text field. *Neither,
as it turned out: Phase 3 extracts inside `FileContentLoader.Load`, beside the
highlight and the markdown render, where the decoded text is already in hand and
`FilePreview.Text` only has to carry the resulting `FileOutline?`.* And line endings:
tree-sitter counts rows by `\n` in the bytes while `SplitLines` normalizes
`\r\n` **and lone `\r`**. CRLF agrees; a lone-CR file does not. Normalize to
`\n` before encoding.

Defined at its consumers, one level deep, no hierarchy behind it. Implemented by
`TreeSitterSymbolExtractor`, constructed once in `AppServices` and **injected** —
there are only three `new DiffViewModel(` sites, so the wiring is cheap. No
`Shared` static; `SyntaxHighlighter.Shared` is an existing module-level
singleton and this does not copy it.

### 4.3 The capture protocol

Four captures. That is the whole vocabulary:

| Capture | Meaning |
| --- | --- |
| `@def.<kind>` | The whole declaration. `<kind>` is a `SymbolKind` name. One per pattern; this is what makes it a node. |
| `@name` | The declared name. |
| `@body` | Optional. Marks only where the signature stops. |
| `@extent` | Optional. The containment span, consumed inside the extractor (§4.1). |

Adapted from repodex's `c_sharp.scm`, dropping the `@ref` and `@import` halves —
they answer "who uses this", which §2 explicitly does not ask. That is roughly
40% of the file. The definition patterns, which are what we keep, are also where
all of Capability A's accuracy lives.

Three things the adaptation must keep, each learned the hard way upstream and
each documented in repodex's own query file:

1. **`@extent`.** Without it every type in a file-scoped-namespace file reports
   at file scope.
2. **Optional `@body`.** Abstract methods, interface members, partial methods
   and positional records all declare without one.
3. **First `@body` wins.** Several patterns bind it twice (a real body and the
   `;` standing in for one). Captures arrive in source order, so the earliest is
   where the signature stops.

**Deduplication is required.** Patterns overlap by design; two patterns matching
the same node must produce one `OutlineNode`. Keep one per node id, first match
wins. Tested.

**Capture names are validated at extractor construction.** The query compiler
will not do it for us: tree-sitter validates node types, fields and predicates,
but not what a capture name *means*, so `@def.Frobnicate` compiles clean. At
construction we walk `Query.CaptureCount` / `Query.CaptureName(i)`, map each to a
`SymbolKind`, and fail into `CodeIntelAvailability.Unavailable` naming the bad
capture. The "every bundled `.scm` compiles" test asserts capture names too,
since compilation alone proves nothing here.

### 4.4 Boundaries

Three, each parsed at the edge (Rule 1):

1. **`Language.Load` and `Query.Compile`**, plus the capture-name validation
   above, at extractor construction. Missing grammar library, ABI mismatch,
   malformed query, unknown capture — all become
   `CodeIntelAvailability.Unavailable(reason)`, logged once to the crash log.
   Every capability degrades to exactly today's behavior. The app never fails to
   start because a dylib did not ship.
2. **The embedded `.scm` resource.** Same path.
3. **File text.** Already crosses a boundary in the callers.

### 4.5 Threading, pooling and cost

Every capability runs on a **lane that already exists** — `DiffViewModel`'s
generation-guarded background task, `FileBrowserViewModel`'s preview `Task.Run`.
No new lane, no hosted service, no index.

`Language` and the compiled `Query` are immutable and shared. `Parser` and
`QueryCursor` are the mutable halves and need one per concurrent call.

**The pool must be bounded.** `ReviewDiffList.EnsureVisibleLoaded` starts a
`DiffViewModel` per visible file, each with its own highlight lane, and
`ViewModelBase.RunBackground` is a bare `Task.Run` whose generation guard
*discards results* rather than cancelling work. Scrolling fast through a
200-file review therefore fires N concurrent parses that all run to completion.
An unbounded pool would grow to N parsers, each holding native memory, and never
shrink. Use a fixed-size pool (sized to `Environment.ProcessorCount`) with a
wait, or a semaphore around extraction. Saturation blocks a background lane,
which is the correct back-pressure.

Two hazards:

- A pair whose parse **threw must not go back in the pool**.
- It must be a pool, **not `[ThreadStatic]`**. Async continuations resume on
  arbitrary thread-pool threads, so a thread-local would hand the same parser to
  two concurrent callers.

A cursor's retained byte range is *not* a third hazard here: `ForEachMatch`
calls `ts_query_cursor_set_byte_range` on every execution and the wrapper
documents doing so deliberately. That is a raw-C-API problem this binding
already solved.

**Caps.** `GitService.ShowBlob` / `ReadWorkingFile` apply **no cap** — the
existing cap lives downstream in `SyntaxHighlighter.Highlight` at 256 KB of
chars. The extractor takes its own cap, and the two are measured in different
units, so some files will get colors and no outline or vice versa. That is
acceptable; it is written down so nobody treats a mismatch as a bug.

Three truncation interactions that need handling, not just noting:

- `DiffOptions.TruncationLineCap` drops lines past 5000 while keeping the `@@`
  counts, so **a late hunk can carry zero `Lines`**. "Take the first changed
  line" then has nothing to take. Handle the empty hunk explicitly (fall back to
  git's header).
- `FilePreview.Text` is truncated at its own cap. An outline built from
  truncated text is missing everything below the cut, so the jump list must not
  present itself as complete.
- A file over the extractor cap returns null, which is the no-outline path
  everything already handles.

**Cost gets a budget, like highlighting has.** `SyntaxHighlighter` carries a
750 ms whole-file budget and a 100 ms per-line timeout. Extraction gets an
equivalent whole-file budget; exceeding it returns null and is logged once.
Asserting "parsing is the cheap half" without a number is how the diff lane gets
slow later and nobody knows which half did it.

**Cancellation.** Extraction is synchronous and short. The caller's existing
generation guard discards a stale result; the pool outlives every call and the
`SyntaxTree` is disposed in the same `using` that made it.

### 4.6 Layout

```
GitBench/Features/CodeIntel/
  CodeLanguage.cs             enum { CSharp, TypeScript, Tsx } + Detect(path)
  SymbolKind.cs
  OutlineNode.cs              OutlineNode, FileOutline, EnclosingAt, Flatten
  ISymbolExtractor.cs
  TreeSitterSymbolExtractor.cs
  ExtractorPool.cs
  CodeIntelAvailability.cs    Ready | Unavailable(string reason)
GitBench/Assets/Queries/
  c_sharp.scm, typescript.scm, tsx.scm     (embedded resources)
```

A folder, not a project. A separate project would buy isolation we do not need
(`GitBench.Tests` already has `InternalsVisibleTo`) and would not change the
native build gate.

**`CodeLanguage` is deliberately not `LanguageRegistry`'s id.** That names a
TextMate grammar and has ~90 members; this names a tree-sitter grammar and has
three. One type per vocabulary.

**A kill switch.** Every other risky diff feature has one
(`DiffOptions.SyntaxHighlightingEnabled`, `IntraLineHighlightingEnabled`).
Extraction gets `DiffOptions.StructureEnabled`, checked in the coordinator and
in the file-browser path, so "the parser is wrong about this file" has an answer
that is not a rebuild.

### 4.7 Which grammars we bundle, and how they get to a user

Phase 1 bundles **C# only**, and TypeScript/TSX follow immediately after because
they are already compiled into the submodule's native library. Beyond that,
adding a language is a *build* decision, not a code one — one entry in the
`grammars` array in `native/build.cs`, one `.scm`, one `CodeLanguage` member.
This subsection exists so that decision gets made once instead of re-litigated
the first time someone opens a Kotlin repo.

**The coverage gap is structural, not chromatic.** A language with no
tree-sitter grammar still gets syntax colors, because TextMateSharp covers all
62 ids in `LanguageRegistry` and remains the highlighter (§2). What it does not
get is hunk headers, the outline, the jump list, folding or the change summary —
those have **no fallback at all**. So coverage matters for Capabilities A–F and
barely matters for H.

That is also the whole reason the "prompt the user to install a language pack"
pattern does not fit here. In an editor that pattern exists because no parser
means no highlighting; for us, no parser means *exactly what ships today*.

**Target set, when we expand:** roughly fifteen to twenty grammars covering the
overwhelming majority of real repositories — C#, TypeScript, TSX, JavaScript,
JSX, Python, Go, Rust, Java, C, C++, Ruby, PHP, JSON, YAML, HTML, CSS, Bash,
Markdown.

**Measure before committing to that list.** Each grammar is generated C compiled
once per shipped RID into `libtree-sitter-grammars`, and some `parser.c` files
run to megabytes — C++ and TypeScript are the notorious ones. The costs to
measure are installer size per RID, cold build time, and cached build time.
Guessing here is how a 40 MB installer happens by accident. Note also that a few
grammars ship C++ external scanners, which the native build must tolerate.

**We do not compile grammars on the user's machine.** This is the model nvim,
Helix and Emacs use — `:TSInstall` clones a grammar repo and builds it with the
user's C compiler, and nvim-treesitter's `auto_install` does it on first
encounter with a filetype. It works there because those tools are used from a
terminal on a machine that already has a toolchain. It is wrong here for three
reasons:

1. **Windows.** The shared libraries need a *GNU* toolchain — MSVC links a DLL
   with an empty export table (§12.5). "Install MSYS2 to get syntax-aware diffs"
   is not a first-run experience for a GUI git client.
2. **Supply chain.** Downloading arbitrary C, compiling it, and loading it into
   a process that handles git credentials is a large surface to open for a
   feature whose absence costs a hunk header.
3. **Signing.** Once notarization exists (§12.2), `dlopen`-ing an unsigned
   library from a cache directory requires the `disable-library-validation`
   entitlement — a real weakening of the app's posture, permanently, for this.

**If coverage ever becomes a genuine complaint**, the escalation is a
**prebuilt** grammar pack: compiled in our own CI, which already targets four
RIDs, published as a separate signed artifact, hash-pinned, downloaded and
cached on demand. Same user-facing behavior as an editor's language pack, none
of the three problems above, because we compiled it and we signed it. Velopack
may be able to carry it as a separate package; that needs checking.

WASM grammars — Zed's approach, and the only model that sidesteps native code
entirely — are the other honest option and are not currently reachable:
`cs_tree_sitter` has no WASM support, and hosting a runtime like wasmtime inside
a NativeAOT app is its own project.

---

## 5. Capability A — real hunk headers

**The highest-value change in this document, and the smallest.**

`DiffHunk.Header` is `string?`, parsed from the trailing text after the closing
`@@` (`GitService.TryParseHunkHeader`) and already drawn by `DiffRowPainter`.
The separator row, the layout and the theming all exist.

### Do not overwrite `DiffHunk.Header`

`HunkPatchBuilder` writes `hunk.Header` back into the patch text handed to
`git apply` for stage and discard. Git ignores trailing text, but that is a
patch-correctness path and not the place to find out. `DiffResult` stays exactly
what git said.

### Carry outlines, not derived strings

The tempting shape is a `hunk index -> string` map on the render state. It is
wrong, and the way it fails is quiet.

`DiffViewModel.ApplyOptimisticHunkRemoval` drops a just-staged hunk **out of the
middle of the list** and carries the previous render's payload forward, with the
comment *"whole-file line numbering is unchanged by dropping a hunk, so the
existing spans stay valid"*. That is true for `DiffHighlight`, which is keyed by
**file line number**. It is false for anything keyed by hunk index: after
staging hunk 2, every hunk from 2 on would be labelled with its predecessor's
declaration — silently, on the screen the user is looking at.
`CarryHighlightForward` and the rollback path do the same thing. A bare `int`
standing for a domain identifier, invalidated by a routine operation, is Rule 1's
headline failure mode.

So the annotations carry the outlines themselves:

```csharp
sealed record DiffAnnotations(
    DiffHighlight? Highlight,
    FileOutline? NewSide,
    FileOutline? OldSide);
```

`DiffRowSet.EmitGapSeparator` already has the `DiffHunk` in hand and resolves
the enclosing declaration at flatten time from the hunk's first changed line —
a line-number lookup, immune to re-indexing, identical in character to
`DiffHighlight.ForLine` one function below it.

This is also strictly cheaper downstream: §7 needs both outlines and §8 needs an
enclosing node's start line, and with strings on the render state both would
have to re-derive or refetch. With outlines, both are free.

### The render-state change is two records

`DiffRenderState.Loaded` **and** `DiffRenderState.FullFile` both carry a
`DiffHighlight?`, and `StartHighlight` / `CarryHighlightForward` write to both.
Either both move to `DiffAnnotations?`, or the coordinator's result is
destructured at two sites. Prefer moving both — hunk contexts are meaningless on
`FullFile`, but a nullable field that is always null there is cheaper than two
divergent shapes.

### One coordinator, one blob fetch

`DiffHighlightCoordinator` already detects the language, computes which sides
the diff needs, and fetches each side's whole text through `IGitDiffReader` —
reads that go through `IGitReadGate` and are the expensive part. It becomes
`DiffAnnotationCoordinator` and produces both outputs from text it already has.
`NeededSides` already computes exactly the union of sides the header rule needs.
A second coordinator refetching the same blobs would double the git reads for
one screen.

**Its two early returns must split first.** Today `Compute` returns null if
`!DiffOptions.SyntaxHighlightingEnabled`, and again if
`LanguageRegistry.DetectLanguageId` returns null. Left as-is, turning off syntax
highlighting would silently disable hunk headers, and a file TextMate does not
recognise would get no outline even where tree-sitter knows the language. Both
gates move to their own output's branch; extraction is gated by
`DiffOptions.StructureEnabled` and `CodeLanguage.Detect` instead. A file then
gets colors, or contexts, or both, and none of the three is a special case.

### Deriving the header

For each hunk, take the first changed line, `EnclosingAt` it in the appropriate
side's outline, and render the containment path from the outermost meaningful
ancestor: `AuthService.Login(string)` — namespaces elided, `.` separator,
truncated from the left when it does not fit.

**Sides: the first changed line picks both the line number and the outline.** An
added line ⇒ new side at its `NewLineNumber`; a removed line ⇒ old side at its
`OldLineNumber`. Context lines never decide, because a deletion hunk carries
three of them and reading the new side there would name whatever declaration now
sits at that position — the exact case the acceptance below rules out. This is
also the split `NeededSides` already fetches by, so the outline the rule asks for
is always the one that was built.

The consequence, which is intended: a *modification* hunk names the **old**
declaration, because git emits removals before additions. Identical text in every
case except an edit to the signature itself.

A hunk whose first changed line is inside no declaration falls back to
`h.Header`. A hunk with zero lines (§4.5's truncation case) falls back too.

Two rendering details that are not free:

- **`MaxRowCells`.** `EmitGapSeparator` sizes the horizontal scroll extent from
  `DiffText.VisualCells(header)`. A longer derived string that does not update
  this is clipped out of reach.
- **Left truncation does not exist today.** `DrawMonoText` is called
  start-aligned for the header. Truncating from the left is painter work, small
  but real, and not covered by "a data substitution".

### Hand the same string to the assistant

`ReadTools.cs` emits `hunk.Header` to the model. Left alone, the assistant sees
git's `xfuncname` while the UI shows the real declaration. Fixing it here is the
cheapest part of Capability E and belongs in this phase.

### Acceptance

Every hunk separator names the enclosing declaration, including in a
file-scoped-namespace file (the `@extent` case — check by eye). Overloads get
distinct headers. A pure deletion names the declaration it was removed from. A
mode-only change and a whole-file deletion behave as today (`Hunks.Count == 0`
short-circuits the first; `NeededSides` handles the second). An unsupported
language, and `StructureEnabled = false`, render byte-identically to today.
Stage and discard still work, with a test asserting the patch text is unchanged.

Known gap, pre-existing: a **fully expanded** gap emits no separator row at all,
so "every hunk separator" is only true until the reader expands context. Do not
write an acceptance test that trips on it.

---

## 6. Capability B — outline and jump

**B1 — declarations in the tree.** A file is a node that opens, the way a
directory is. Expanding it lists its declarations inline, indented by nesting,
each with a kind icon and the parameter list dimmed after the name; clicking one
previews the file and scrolls to it. Files are collapsed by default; opening one
reveals its whole outline at once. A declaration that declares something itself
opens too, but the other way round from a directory: what is tracked is the set
the reader has explicitly *closed*. Default-closed declarations would make
reading a file's shape a drill-down — three clicks to reach a method — and would
need state for every node to say the ordinary thing. The closed set is keyed by
containment chain (`App.AuthService.Login(string)`), so it survives a re-parse
and an edit further up the file, and it is not persisted.

A file gets a chevron when `CodeLanguages.Detect` recognises its name, which is
known before anything is read; whether it has any declarations is only learned by
opening it, so a file with none opens onto nothing. The alternative — parsing
every listed file to decide whether to draw a chevron — is a file read per row on
the listing lane, which is the wrong price for a chevron.

*This replaced a jump dropdown in the preview header
(`RepoBarContextMenu.ShowSearchable`, type-to-filter). The tree is where you are
already navigating, and it gives the outline a place that persists instead of one
that closes when you look away. Search comes back when the whole file panel gets
it, rather than living on one control.*

**B2 — sticky breadcrumb.** The enclosing declaration of the top visible line,
in the preview header, updating on scroll — dimmed and *after* the path rather
than replacing it, because the path says which file and the breadcrumb only ever
says where in it. Namespaces are elided for the same reason they are in a hunk
header: within one file they are the same everywhere, so they cost width without
saying anything.

An always-visible outline panel is deliberately not here: `ResizableRightPanel`
does not exist (only `ResizableLeftSidebar` and `ResizableSidebar`), so it is a
new control plus a persisted width plus a collapse rule. Add it when B1 proves
the outline is something you reach for.

### Where the outline comes from

Two places, because the tree and the preview want it at different times.

`FilePreview.Text` gains a `FileOutline?`, alongside the `DiffHighlight?` it
already carries — same shape, same lifetime, computed on the same background
load. That is the previewed file's outline, and it is what B2 reads.

The tree's is its own: `FileBrowserTree` takes an outline delegate and caches one
per **expanded** file beside its directory listings, so both are dropped by the
same `Refresh` and a working-tree change re-parses exactly what is open.
`FileContentLoader.OutlineOf` is the cheap read behind it — decode and extract,
no lines, no highlight, no markdown.

**Row identity moves from a path to a `RowKey`.** A file and the declarations
inside it share a `FullPath`, so a cursor keyed by path could never leave the
file row: arrowing down would resolve back to the same index. `RowKey` is the
path for anything on disk and `path + '\n' + containment chain` for a declaration — a
newline being illegal in a path on either platform, which is also what lets
`Persist` recognise a declaration cursor and decline to write it to the state
file. The chain rather than the line is what makes both the cursor and a closed
declaration survive an edit above them. Expanded *files* do ride the persisted `Expanded` set: they are paths, and
`ReconcileExpanded` now counts files as present so a flatten stops closing them.

### The channels are the real work

Jumping to a line does not currently work. Five gaps, of which **1–4 are closed
as of B1** and 5 is what B2 still needs:

1. `FileBrowserViewModel.SyncPreview` early-returns when the target path is
   unchanged — so a jump *within the open file* fires no preview event at all. A
   jump needs its own path, separate from selection.
2. `FileBrowserTextBody` holds its `DiffContentView` as a local with only a
   `content.Bind(browser.Preview, …)`. There is no view-model → view channel for
   "scroll to line N".
3. `SetRenderState` resets `_metricsResolved`, and `ScrollToNewLine` bails on
   `_lineHeight <= 0` before recording anything. `_lineHeight` itself is never
   zeroed — only `EnsureMetrics` writes it, and `ApplyScrollForTransition`
   already calls `ScrollToNewLine` from inside `SetRenderState` and depends on
   that persistence. So this failure is confined to the **first draw of a fresh
   view**, not every render. A pending-line target is still wanted, and §9.6
   makes it mandatory rather than merely tidy.
4. `SetRenderState` sets `_scrollX = 0` **unconditionally**. Make it conditional
   on the path actually changing.
5. B2 needs a channel in the *other* direction. `TryGetTopVisibleNewLine` lives
   on the `DiffContentView` that `FileBrowserTextBody` holds as a local;
   `FileBrowserPreviewHeader` is a sibling widget with no path to it and needs
   the value on every scroll frame. **Closed** by
   `DiffContentView.TopVisibleLineChanged`, raised from the draw rather than from
   `VerticalScrollPositionChanged`: row geometry needs metrics, metrics resolve on
   the first draw, and the scroll event fires before that — so a scroll-driven
   breadcrumb would stay empty until the reader touched the wheel. Deduped on the
   line, and reset when the path changes so a new file republishes line 1 as a
   different declaration.

All five are closed. Gaps 1 and 2 are answered by the same pair: `NavigateToLine` raises a
`LineRevealRequested` event — an occurrence, not a state, so jumping twice to the
same line scrolls twice — and `FileBrowserTextBody` holds the subscription for
its mounted period. Gap 3 is `DiffContentView.RequestScrollToNewLine`, which
keeps the target until metrics resolve and drops it if a different file arrives
first. Gap 4 was already fixed, ahead of this plan, by `3153a7e`.

**Phase 3 delivers one entry point, `NavigateToLine(int line)`**, on the file
browser's view model — the single method that jumps, whether the caller is a
declaration row, a future navigation feature, or §9.6's unfold-and-jump. Naming
it here is what makes "one place to get it right" true rather than aspirational.

It holds a pending line rather than dropping one. A declaration row's file is
often *not* the file on screen, so the jump has to outlive the read that brings
it there; the target is released when a text preview for that exact path lands,
and dropped when the reader moves elsewhere first.

### Acceptance

Open a `.cs` file's chevron in the tree: its declarations appear indented beneath
it, parameter lists dimmed, and a second file can be open at the same time.
Clicking one lands on the declaration a few rows below the top edge — including
when that file was not the one previewed, which is the read the reveal has to
wait for. A file with no grammar has no chevron; one with a grammar and no
declarations opens onto nothing. The breadcrumb tracks scrolling. Horizontal
scroll survives a jump.

---

## 7. Capability C — symbol-level change summary

For a commit or a review stack: **what declarations did this change touch?**

Build both outlines (already on `DiffAnnotations` from §5), match hierarchically
per level by `(Kind, Name, ParameterTypes)` — qualified-name equality without
building qualified names, with overloads distinguished — and classify:

- new-side only → **Added**
- old-side only → **Removed** (nested under its nearest surviving ancestor)
- both → **Modified** if a changed line falls in the node's own range *excluding
  its children's ranges*, else **Unchanged**

That exclusion is what makes a changed method light up while its containing
class stays neutral — the difference between a useful summary and a list saying
"everything changed".

Including `ParameterTypes` in the key is what stops an added overload being
reported as a modification of an existing one, and a removed overload as a
modification of its sibling.

Prune to changed nodes, keeping Unchanged ancestors on the path to a change.

**One outline present, not both.** This section assumed both sides are always
parsed, and they are not: `NeededSides` skips the old blob for a diff that only
adds lines, which is a large share of real commits. With one outline, *added* and
*removed* are not distinctions that can be drawn — the first build of this
reported every declaration in such a file as Added, which reads as "this commit
wrote the whole file". So a one-sided summary reports only what the change
reached inside of, and claims nothing about what appeared or vanished. The
alternative, fetching the old blob whenever structure is on, doubles the git
reads §5 went out of its way to avoid.

**Namespaces contribute nothing.** Every declaration in a file shares one, so
naming it separates nothing — and eliding it from the path while still emitting a
node for it produced a summary entry with an empty name. They are transparent
containers: their children are reported at the level above them.

*Both outlines absent → fall back to the distinct hunk headers from Capability A.
Not built: with the one-sided rule above, the only files reaching that fallback
are ones the parser has no grammar for, where the hunk separators in the body
already carry git's own headers a few pixels below the header that would repeat
them.*

**Where it renders.** The diff pane header for a single file; the review window
for a stack. Start with the single file — it reuses annotations the coordinator
already computes and needs no new git reads.

**It arrives asynchronously**, so it re-emits a render state for a file already
on screen. Keep it **out of the row stream** — it is header chrome — so the row
count stays stable and `SetRenderState`'s row-count guard never fires.

**What it looks like.** A row of declaration names where `DiffPaneHeaderWidget`
used to say "Diff View" — a title that tells a reader nothing they did not
already know. Names only, not containment chains: the tab above already names the
file, and in a narrow pane four full chains spend their width repeating the
prefix they share and truncate to nothing. Colour carries the verb, borrowed from
the diff's own add/remove palette, so there is no legend and nothing to
translate. Four names, then a localized "+N more"; each yields width before the
count does and later ones yield first, so a long list loses its tail rather than
its head.

---

## 8. Capabilities D and E

**D — expand to the enclosing declaration.** Find the enclosing node's start
line, compute how many lines that is from the gap's edge, and expand by that
count. `ContextExpansion`'s model is untouched.

Two constraints. It needs a **new method**: `ExpandGap(int, GapExpandDirection)`
takes a direction, not a count. And it must **not** add a `GapExpandDirection`
member — `ExpanderGlyph` has a `_ =>` catch-all and `ExpanderIconsFor` is a
boolean if-chain, so a new member would be silently mishandled at two sites
rather than caught.

*Built as `ExpandGapToDeclaration`, reached by **Alt-clicking** a stepping
chevron. Both constraints hold: a separate entry point, no third enum member, no
third glyph. A plain click still steps by twenty, so nothing a reader already
does behaves differently — the cost is discoverability, and flipping which of the
two the bare click gets is one line if that trade turns out wrong.*

*The rule, symmetric about the gap: expanding **down** finishes the declaration
the hunk **above** sits in; expanding **up** reaches back to the start of the one
the hunk **below** sits in. It falls back to the fixed step wherever the outline
cannot answer — no grammar, an adjacent hunk inside no declaration, a boundary
already revealed — and is clamped to what the gap still hides. Unfold-all is left
alone: it already means all of it. The rule lives in `DiffGaps.ExpandStep` rather
than inside the view model, because it is gap geometry and wanted testing without
standing up a `DiffViewModel`.*

**E — assistant context.** `ReadFileTool` and the selection-quote path hand the
model line ranges; with an outline they can name the enclosing declaration and
the file's shape. The `hunk.Header` half lands in Phase 2 (§5); the rest is
additive and can land any time after Phase 1.

*There is no `ReadFileTool` — the read surface is status, changes, diff, history,
commit details and branches. So E landed on the two places that do hand the model
code:*

- *`DiffSelectionQuote` names the declaration the selection sits in, chosen from
  the side the selection came off — a removal is named by the before-side
  outline, the same rule a hunk header follows. The prompt reads "lines 42-43, in
  `AuthService.Login(string)` (added lines)".*
- *`get_diff` carries a `declarations_changed` array, which is §7's summary
  flattened. A model asked what a commit does otherwise has to infer it from line
  offsets — and the hunk list is capped at 1500 lines, so the lines it would have
  to infer from may not even be in the response.*

---

## 9. Capability F — folding

The only capability that changes the row stream. Two structural decisions make
it far cheaper than it looks.

### 9.1 Which surfaces fold

**The Files-view preview only.**

`DiffViewMode { Diff, FullFile }` is a sticky per-pane mode with a toolbar
toggle, reachable in the main diff pane and the pop-out window, where
`DiffViewModel.BuildFullFile` constructs the render state. The Files-view
preview is a *different* path: `FileBrowserTextBody.ToRenderState` converts
`FilePreview.Text` into a `DiffRenderState.FullFile` inside the widget.

Folding covers the second only. That gives **one owner** for fold state
(`FileBrowserViewModel`) and one set of survival rules. Extending it to the diff
pane's full-file mode is a follow-on with its own re-emit chain — that surface
re-emits from `StartHighlight`'s async attach and `CarryHighlightForward`,
nowhere near `SyncPreview` — and should not be smuggled in.

Two consequences worth stating:

- `DiffSelectionQuote` is **unreachable** from the folded surface.
  `AssistantActions` is true only in the commit diff tabs; `FileBrowserPreview`
  documents leaving it false. So §9.5's assistant half does not exist in Phase 4.
  It becomes live the day folding reaches the diff pane, and §9.5 says what to
  do then.
- `ReviewDiffList` never sees a fold — but that is an **affordance, not an
  invariant**. `SyncBodyView`'s switch routes `FullFile` to the rows path and
  `ReviewDiffList` does read `RowSet.SingleGutter`; nothing at the type level
  prevents it. It holds because no review-window UI exposes the mode toggle.

### 9.2 No new `DiffRow` case

The obvious move is `DiffRow.Fold`. Don't. A collapsed region is not a new kind
of row — it is an ordinary line with a marker that swallows what follows. So
`DiffRow.Line` gains one optional field:

```csharp
sealed record Line(
    DiffLineKind Kind, string OldNumber, string NewNumber, string Text, int Chars,
    IReadOnlyList<TokenSpan>? Spans = null,
    IReadOnlyList<CharRange>? Emphasis = null,
    FoldMark? Fold = null) : DiffRow;

readonly record struct FoldMark(string Id, bool Collapsed, bool Chevron, bool Chip);
```

*Built with four fields, not two. A fold touches the row stream twice — the
signature row carries the chevron, the brace row carries the chip — and the two
are different rows whenever the signature is one line and the brace is the next,
which is this codebase's style everywhere. The flags say which job a row is doing;
both are set on the one row when a signature and its brace share a line. And
`Id` is the declaration's containment chain, not a `FoldId` index — see §9.4.*

`DiffRow.Line` has exactly **four construction sites**, none passing eight
arguments, so the field appends cleanly. `Deconstruct` gains arity, so any
positional deconstruction becomes a compile error rather than a silent
misbinding. The one silent change is record value equality now including
`Fold` — and nothing in the codebase compares `DiffRow` by equality, so it is
inert. Every `is DiffRow.Line` site keeps matching and keeps behaving.

What actually changes:

| Site | What changes |
| --- | --- |
| `DiffRowPainter` line drawing | Chevron in the fold column; `{...}` pill when collapsed; the rule between gutter and code |
| `DiffRowSet.FlattenFullFile` | Skip lines inside a collapsed range |
| `DiffRowSet.MaxRowCells` | Count the chip, or the row clips and the chip is unreachable |
| `DiffContentView` gutter math | One more fixed-width column |
| `ReviewDiffList` | Shares `DiffRowPainter.LineTextOriginX` and keeps its **own** parallel width math — both move together |
| `DiffContentView` hit-testing | A chevron cell, mirroring `HitTestExpander`'s existing geometry |
| `DiffSelectionModel.BuildCopyText` | §9.5 |

**Reserve the fold column always**, not "only when the state has folds". The
outline arrives asynchronously; a column that appeared when it landed would jog
every line of text sideways a beat after open. Full-file mode always has the
column, empty when nothing folds — the same way `SingleGutter` decides the
gutter layout by mode rather than by content.

`DiffRowPainter.DrawRow` has no `default` arm, so an unmatched row silently
draws nothing. Worth adding one (throw in Debug, visible error row in Release)
while in the area — but this is opportunistic cleanup, **not** part of folding's
cost, since §9.2's design adds no case that could fall through.

### 9.3 What folds, and to what

Foldable when `SignatureEndLine < EndLine`. §4.1 defines the no-body case as
`SignatureEndLine = EndLine`, so the predicate is total: expression-bodied
members, single-line bodies, abstract and interface members, positional records,
delegates and enum members all correctly report not-foldable.

Collapsing hides `SignatureEndLine .. EndLine` — *the opening brace included* —
and the chip renders at the end of the last signature row as `{...}`, so a folded
declaration reads as the single line it declares on: `internal enum GitReadKind{...}`.

*This section originally kept the brace row visible and put the chip there, as
`void Foo()` over `{ … }`. Two rows per collapsed declaration is forty rows for a
twenty-method class, and a bare `{` under a collapsed signature reads as an
unclosed brace. Rider, VS Code and every editor worth copying collapse onto the
signature. The `Math.Max(StartLine + 1, SignatureEndLine)` in the fold plan is
what keeps that honest when a signature and its brace share a line: the chevron's
own row is never hidden.*

The chip is chrome, not characters — it is drawn past the row's text, so nothing
selects it, no caret measures against it, and copying re-inflates the real body
rather than the placeholder.

Nested folds flatten to the **union of collapsed ranges**, outermost wins.

### 9.4 Fold state, and the two events that disturb it

**The per-click event is the primary one.**

`SetRenderState` unconditionally sets `_scrollX = 0`, clears the text selection
when the row count changes, and hands `ApplyScrollForTransition` a **pixel
offset** to restore. A fold toggle always changes the row count — that is what
folding is. So routed through `SetRenderState`, every click would reset
horizontal scroll, drop the selection, and — because the pixel offset is
preserved while the rows above shift — silently move the reader's line.

So folding gets its **own update path**: `DiffContentView.SetFoldState(...)`
re-flattens the rows and re-anchors on the **top visible line**, not the pixel
offset, without running the render-state transition.
`TryGetTopVisibleNewLine` / `ScrollToNewLine` already exist for exactly this and
are already used that way for the Diff↔FullFile mode toggle.

**Selection is cleared on a fold toggle, explicitly.** `DiffTextPos` is
`(Row, Char)` — a row index into the current stream — so a selection anchored at
row 40 means a different line after a collapse. `SetRenderState`'s row-count
guard would have cleared it; the custom path above removes that guard, so the
fold path must clear it itself. Remapping anchors through the fold model is more
work than folding, and is not worth it.

**The secondary event: the 30-second heartbeat.** `RepoReconcileService`
broadcasts a `WorkingTreeChangedMessage` every 30 s per active repo (gated on
foreground and no git activity), `FileBrowserStore` forwards it to
`Invalidate()`, which calls `SyncPreview(force: true)`. The code's own comment on
`Invalidate` says *"this runs twice a minute at idle on every platform."* Left
alone, folds pop open twice a minute.

Two fixes, both worth having independently:

1. **Suppress no-op re-emits.** `SyncPreview(force: true)` has exactly one
   caller — `Invalidate()` — so suppression changes exactly one behavior: "the
   working tree may have changed on disk", which a content comparison answers
   directly. Compare a **hash of the decoded text**, not size+mtime: a same-size
   write inside mtime granularity is a real editor pattern and the failure mode
   there is a preview that never updates, which is worse than the churn.
   The `_preview.Value = new FilePreview.Loading(target)` assignment must move
   behind the check too — it happens synchronously before the background read,
   so the body kind flips Text → Placeholder → Text twice a minute *today*,
   folding or not.
2. **Key fold state to structure.** *Built as the declaration's containment
   chain — `App.AuthService.Login(string)`, from `FileOutline.PathOf` — rather
   than the pre-order index plus a structural signature this section first
   proposed. The chain is the same key Phase 3's tree uses for its own open set,
   it is computed while walking the outline anyway, and it needs no signature to
   compare against: a fold survives any edit that leaves its declaration where it
   is in the containment tree, and quietly stops applying when the declaration is
   renamed or moved.*

   *That also closes the hole the index-based scheme had to accept. Reordering
   two same-named declarations moved a fold onto the wrong method, because the
   index shifted while the signature did not. A chain names the declaration
   rather than its position, so a reorder changes nothing, and a true reorder of
   two identical chains is not representable.*

`FoldState` lives on `FileBrowserViewModel`, per open file, touched **only on
the UI thread**. Not persisted across sessions.

### 9.5 Copying across a fold

Folded lines are absent from the row stream, and `DiffSelectionModel.BuildCopyText`
walks rows with `if (rows[row] is not DiffRow.Line line) continue;`. Selecting
across a collapsed method and copying would **silently drop its body** — text
the user did not select and cannot see is missing. That is data-shaped, not
cosmetic. `BuildCopyText` consults the fold model and re-inflates hidden lines.

**Select All is the most likely path to it.** `WholeSpan` returns `(0,0)` to the
last row, so Select All → Copy goes straight through the bug. `WordSpan` and
`LineSpan` genuinely are within one visible row and need no change.

`DiffSelectionQuote.Build` calls `BuildCopyText` and then runs its **own** loop
over rows with the same filter, computing the cited first/last line numbers. If
only `BuildCopyText` is fixed, the model gets re-inflated text with a line range
that does not describe it. Per §9.1 that path is unreachable in Phase 4 — fix it
when folding reaches the diff pane, and leave a comment saying so.

### 9.6 Jumping into a fold

`FindRowForNewLine` falls back to the closest preceding numbered row, so a jump
into a collapsed body lands on the fold header. The right behavior is **unfold
the containing region, then scroll**, routed through Phase 3's `NavigateToLine`.

This is a sequenced cross-boundary operation: set fold state → re-flatten → new
row stream → resolve the scroll. `ScrollToNewLine` computes against the
*current* row set, so scrolling before the unfold lands targets the folded
layout. This is what makes §6's pending-line target mandatory rather than
first-draw-only.

*In the event it stayed one statement order rather than a sequencing problem:
publishing the fold state re-flattens synchronously through the view's binding,
so `NavigateToLine` unfolds first and raises the reveal second, and the pending
target catches the case where the row is not measurable yet anyway.*

### 9.7 Acceptance

Chevrons beside every declaration with a body, in a column that was there before
the outline arrived. Collapse a method: it reads `void Foo(){...}` on one line,
and the line numbers below are unchanged and correct. Collapse the containing class: the
methods go with it. Collapse something above the viewport: the line you were
reading stays where it was. The selection clears on toggle rather than silently
re-pointing. Wait two minutes: nothing pops open, horizontal scroll has not
moved. Select All → Copy: every folded body is in the clipboard. Jump to a
declaration inside a collapsed class: it unfolds and scrolls there. An
attribute-decorated method puts its chevron on the signature, not the attribute.
A language with no grammar shows no chevrons and renders byte-identically to
today.

---

## 10. Deferred

**G — moved-code detection.** Needs a body fingerprint (repodex has one) plus a
matching pass across the two sides. Genuinely valuable, genuinely a project.
Revisit after Capability C, which builds the old/new outline matching G extends.

**H — tree-sitter syntax highlighting.** Not a replacement for TextMateSharp; a
second implementation behind the `ISyntaxHighlighter` seam that already exists,
routed per language. Tree-sitter where we bundle a grammar, TextMate for
everything else, so coverage never regresses and adding a language is a build
decision rather than a code one.

**Start with a measurement, not an implementation.** Once Phase 1 lands both
engines are in-process, so a benchmark over a few hundred real files comparing
`SyntaxHighlighter.Highlight` against a tree-sitter highlights query on the same
input is roughly an hour of work and settles the question with a number instead
of an argument. Report throughput and worst case, not just the mean — the worst
case is what the current guardrails exist for.

Three things make it *likely* faster, and the third is the real argument:

1. `SyntaxHighlighter` carries a 100 ms per-line timeout explicitly to cap
   Oniguruma backtracking, a 750 ms whole-file budget, and a 256 KB cap. Those
   guardrails are evidence about the current engine's worst case. Tree-sitter
   does not backtrack.
2. `SyntaxHighlighter` holds a **global lock** — TextMateSharp grammars are not
   safe for concurrent tokenization, so every highlighting surface in the app
   serializes through one instance. Harmless for a single diff pane; the review
   window starts a lane per visible file and they all queue. Tree-sitter parsers
   are per-worker by construction.
3. After Capability A we parse the file anyway. Highlights from that tree are
   nearly free, where today we make two passes over the same text with two
   engines.

What it costs, accurately:

- **Not coverage, and not availability.** Maintained tree-sitter grammars exist
  for the large majority of `LanguageRegistry`'s 62 ids. What bundling them
  costs, and why we never compile one on the user's machine, is §4.7 — the same
  decision serves Capabilities A–F and is more urgent there, since those have no
  TextMate fallback.
- **One version pin per grammar.** A pinned tag fixes the node type names the
  queries match on, so N grammars is N pins and a bump breaks a query *silently*
  rather than failing the build. This is the real maintenance surface, and the
  fixture-outline test from Phase 1 is the pattern that catches it.
- **A theme mapping.** `TokenColorSlot` maps TextMate scopes; tree-sitter
  highlight captures are a coarser, different vocabulary, and a naive mapping
  looks *less* granular than what ships today. The mapping is written once
  rather than per language, which is what makes this affordable.
- **Highlighting keeps its TextMate fallback regardless.** Routing per language
  behind `ISyntaxHighlighter` means an unbundled language is never worse than
  today, which is what makes H a safe experiment rather than a migration.

And one thing that is worth nothing here: incremental reparse, tree-sitter's
headline feature and the reason editors adopt it. GitBench does not edit files.
We would get the fast parse, never the fast reparse.

---

## 11. Phases

**Status: Phases 0 through 6 are done — every capability A–F this plan set out to
build. What remains is §11's "Later": TypeScript and TSX, the wider grammar set
per §4.7, then G and H.**

**Phase 0 — build foundation. ✅ Done.** *No app behavior changes. Budget a week.*

- Decide `vendor/` versus `external/` (§3), then
  `git submodule add https://github.com/Zeejfps/cs_tree_sitter.git <path>`.
  HTTPS because it is simpler, not because CI requires it — `release.yml`
  already applies a global `url.…insteadOf` rewrite before a recursive
  `submodule update`, so SSH would also have worked.
- Add both projects to `GitBench.sln`.
- `GitBench.csproj`: `ProjectReference` to `TreeSitter`, import
  `native/Native.props`, and the AOT link items. Note those items are **not** in
  `Native.props` — that file defines four properties and no item groups; the
  `DirectPInvoke`/`NativeLibrary` pair is a paste-in snippet from the
  submodule's README, and as published it is unconditional.
- **Upstream PR #1 — pin the artifact RID to the target, not the host.**
  `Native.props` defaults `NativeArtifactsRid` to
  `$(NETCoreSdkPortableRuntimeIdentifier)`, the *SDK's* RID. This **cannot** be
  fixed from `GitBench.csproj`: `TreeSitter.Bindings` imports `Native.props`
  itself and owns the `Content Include="$(NativeArtifactsDir)*"` copy items and
  the `CheckNativeArtifacts` target, and a property set in a referencing project
  does not reach a `ProjectReference`'s own evaluation — GitBench would link the
  right archive and copy the wrong grammar dylib. The fix belongs in
  `Native.props`, preferring `$(RuntimeIdentifier)` when set.
- **Upstream PR #2 — CI for the submodule.** `cs_tree_sitter` has **no CI at
  all**: no workflow, no script. Its `TreeSitter.Bindings.Tests` pin exact
  struct byte offsets and are what §15.1 leans on as the guard against a
  silently shifted field — and nothing runs them. Add a workflow there running
  the bindings and grammar tests on all four RIDs.
- `scripts/build-native.sh` / `.ps1` wrapping `dotnet run <path>/native/build.cs`.
- **Add a `build.yml` workflow here** — build and test on all four RIDs, on push
  and PR. `.github/workflows/` contains only `release.yml`, on `push: tags`. So
  today the only way to exercise the Windows runner is to publish a real
  release, and none of this plan's "verified in CI" claims have anywhere to run.
- Cache `native/vendor` and `native/artifacts` on the hash of `build.cs`.
  Otherwise every job re-clones three grammar repos and recompiles tens of
  megabytes of generated C.
- `README.md` (there is no `CLAUDE.md`): a fresh clone runs the script once
  before `dotnet build` — and before `dotnet test`, since `GitBench.Tests`
  references `GitBench`. Plus: the submodule is ours to edit, per §3.

*Acceptance:* fresh clone → script → `dotnet build` and `dotnet test` green on
all four platforms in `build.yml`; the submodule's own tests green on all four;
`publish -c Release -r osx-arm64` produces a launching app, and
`publish -r osx-x64` on an arm64 host links the x64 archive.

**Phase 1 — the extractor. ✅ Done.** §4, plus `c_sharp.scm`.

Tests: containment via extents, including file-scoped namespaces; `StartLine`
skips attributes; `SignatureEndLine == EndLine` for every bodyless form;
`ParameterTypes` distinguishes overloads; deduplication of overlapping patterns;
operators and conversion operators, which have no ordinary name; a syntax-error
file extracts what it can and does not throw; unsupported extension returns
null; every bundled `.scm` compiles **and every capture name validates**; a
checked-in expected outline for a fixture file, which is what detects a grammar
pin bump — the pins live in the submodule's `build.cs` and nothing else would
notice them moving.

*Acceptance:* correct outlines for real files from this repo. No UI.

**Phase 2 — Capability A. ✅ Done.** §5, including the `ReadTools.cs` header fix.
Shippable on its own.

Landed as `DiffAnnotations` + `DiffAnnotationCoordinator` in `Features/Diff`,
with `FileOutline.EnclosingPathAt` doing the containment chain and
`DiffRowPainter.FitHeader` / `DiffText.SuffixWithin` doing the left truncation.
Two things the phase learned, both now written into §5: the side rule is decided
by the first changed line rather than by "added or context", and the coordinator
bails before fetching only when *both* outputs are switched off.

**Phase 3 — Capability B.** §6: the outline on `FilePreview.Text`, both
channels, `NavigateToLine`, the `_scrollX` fix, and §9.4's re-emit suppression.
Folding depends on all of it.

- **B1 ✅ Done.** Declarations are rows in the tree under an expandable file
  (`FileBrowserRow.Symbol`, `FileBrowserRow.File.IsExpandable`, `RowKey`);
  `FileBrowserTree` takes an outline delegate and caches one per expanded file;
  `FileBrowserViewModel` takes an `ISymbolExtractor` and owns `ToggleFile`,
  `ToggleSymbol`, `SelectSymbol`, `NavigateToLine` / `LineRevealRequested` and
  the pending reveal; `DiffContentView` grew `RequestScrollToNewLine` with a held target for
  the first-draw case. The outline also rides on `FilePreview.Text`, extracted
  inside `FileContentLoader.Load`, which is what B2 will read. Rows render
  `Name(paramTypes)` with the parameters dimmed — no *kind* label, because naming
  fourteen `SymbolKind` values costs 98 catalog entries and the indent plus the
  parens already separate a type from a callable. The icon carries the kind
  instead, in four categories rather than fourteen: `braces` for a namespace,
  `function-square` for anything callable, `variable` for anything holding a
  value, `box` for anything containing either.
- **B2 ✅ Done.** `DiffContentView.TopVisibleLineChanged` → `SetTopVisibleLine`
  → a `Breadcrumb` projection the header binds to. The dotted-path rendering
  moved out of `DiffAnnotations` onto `FileOutline.RenderPath` /
  `DeclarationPathAt`, so a hunk header and a breadcrumb cannot drift apart —
  they were the same three lines of `StringBuilder` twice.

The `_scrollX` fix and §9.4's re-emit suppression landed early, in `3153a7e`.

**Phase 4 — Capability F, folding.** §9. Settle §9.4's fold-toggle update path
**before** starting — it decides where fold state lives and whether selection
survives, and finding that out mid-phase means redoing the flattening work.

- **4a ✅ Done.** `FoldMark` + `FoldState` in `Features/Diff/Folding.cs`; the
  fold plan inside `DiffRowSet.FlattenFullFile`; the reserved column through
  `DiffRowPainter.LineTextOriginX` / `FoldColumnWidthOf` (and `ReviewDiffList`'s
  parallel width math with it); chevron and chip painting; `DiffContentView`'s
  own `SetFoldState` path, hit test and hover; `BuildCopyText` re-inflation via
  `DiffRowSet.HiddenAfter`; `FileBrowserViewModel.Folds` / `ToggleFold`.
  `DiffRow.Line.Chars` was deleted while the record was open, per §14.
  Two theme slots came with it, per §14's rule about new surfaces:
  `DiffContentStyles.GutterRule` (the vertical rule full-file mode draws between
  the numbering and the code, the way an editor rules its left margin) and
  `FoldChipBackground` (the pill). The chevron *and* the pill are both click
  targets and both report as interactive, so the cursor turns to a hand over
  either and neither starts a text selection.
- **4b ✅ Done.** `FileBrowserViewModel.Unfold` runs at the top of
  `NavigateToLine`, walking the target line's containment chain and opening every
  collapsed declaration that hides it. Ancestors included: an outer fold hides an
  inner one's body whatever the inner one says. A jump to a folded declaration's
  own signature changes nothing, because that row is still on screen.

Copy re-inflation belongs in 4a, not 4b: it is ~20 lines against the fold model
4a already builds, and splitting it out would mean 4a ships a silent hole in
copied text.

**Phase 5 — Capability C.** §7.

**Phase 6 — Capabilities D and E.** §8. Small, independent, either order.

**Later:** TypeScript and TSX (one `.scm` and one enum member each — the
grammars are already in the submodule's native library, so no native work), then
the wider grammar set per §4.7, then G and H.

Phases 4 and 5 are independent and can swap. Folding is placed first because it
was asked for; `ParameterTypes` landing in Phase 1 means C's overload identity
is already available to F either way.

---

## 12. Build, packaging and CI — what is already settled

Verified rather than assumed:

1. **Loose native libraries already ship and already load.** The app ships
   `libglfw.3.dylib` and TextMateSharp's `libonigwrap` beside the AOT
   executable, and `release.yml` copies the whole publish directory flat into
   `Contents/MacOS/`. `Language.Load` probes beside the assembly, so the grammar
   dylib lands where it is looked for.

   But these are **three different mechanisms**, not one, and the plan should
   not reason as if they were interchangeable: `libonigwrap` arrives via
   TextMateSharp's NuGet `runtimes/<rid>/native` assets with zero csproj work;
   `libglfw` uses hand-written per-RID `Content` + `TargetPath` items in
   `Glfw.NET.csproj`; the grammar library flows out of a `ProjectReference`'s
   `Content` glob over files produced **out of band** by a script. Only the
   third has a build-order dependency, which is precisely why §13's first two
   risks exist.
2. **No `codesign` or notarization step exists today**, so an extra dylib costs
   nothing now. When signing arrives, nested dylibs belong in
   `Contents/Frameworks` — `docs/plans/glfw-static-linking.md` already argues
   this for the ones we ship.
3. **Static core plus dynamic grammars does not duplicate the runtime.**
   `build.cs` links `tree-sitter-grammars` from grammar objects only, with no
   reference to the core, so `DirectPInvoke`-ing `tree-sitter` while
   `dlopen`-ing the grammars is correct.
4. **`DirectPInvoke`/`NativeLibrary` are ILC-only** and inert without AOT, so a
   `PublishAot` condition on them is belt-and-braces, not load-bearing.
5. **Windows needs two toolchains**: a GNU one (MSYS2/mingw) for the shared
   libraries, because tree-sitter's core annotates nothing `dllexport` and an
   MSVC-linked DLL exports nothing; and `cl.exe`/`lib.exe` for the static
   archive, located through `vswhere`. The archive **cannot be cross-built** —
   `build.cs` gates it on `OperatingSystem.IsWindows()` — so `windows-latest`
   must run both before every release publish. `windows-latest` is believed to
   ship both. **Prove it in `build.yml` before anything depends on it.** This is
   the single largest unknown in Phase 0.

---

## 13. Risks

| Risk | Mitigation |
| --- | --- |
| Windows native toolchain in CI | Phase 0's `build.yml`, proven before Phase 1 starts |
| A fresh clone stops building until `build.cs` runs | Deliberate submodule design (fails at build naming the command, not at `dlopen`). Script + README. Auto-running it from MSBuild was rejected: it clones from the network during a build |
| CI time and a new network dependency in the release path | Cache `vendor/` and `artifacts/` on the hash of `build.cs` |
| Grammar pin drifts from the queries | Pins live in the submodule's `build.cs`; a checked-in expected outline for a fixture file is what notices (Phase 1) |
| The parser is wrong about a file | `DiffOptions.StructureEnabled`, and every path falls back to today's behavior |
| Header derived from the wrong side, or from an empty hunk | Pure deletions use the old-side outline; zero-line hunks fall back. Both tested |
| Extraction slows the diff lane | A whole-file budget mirroring `SyntaxHighlighter`'s 750 ms; a bounded pool |
| Folded text missing from a copy | `BuildCopyText` re-inflates from the fold model (§9.5), inside Phase 4a |
| Fold toggle disturbs the reader | Folding's own update path, re-anchored on the top visible line (§9.4) |
| Folds popping open on the 30-second heartbeat | Suppress no-op re-emits by content hash; key fold state to structure (§9.4) |

---

## 14. Things this plan must not forget

- **A theme slot for anything new.** The palette went through a consolidation
  pass to 36 slots. The breadcrumb, the change-summary chips, the fold chevron
  and the `{ … }` chip need named slots, not ad-hoc colors.
- **Driving the new surfaces from automation.** `GitBench.Automation` is a
  one-script runner today and there is no automation-id concept in
  `Features/Diff` or `Features/FileBrowser`, so this is not "add an id" — it is
  deciding whether the GUI MCP driver can reach a hit-tested chevron inside a
  virtualized row at all. Worth answering before Phase 4, not after.
- **Worktrees and submodules.** Paths outside the browser's root exist.
  `NavigateToLine` must handle a path it cannot display rather than assume it can.
- **Localization.** New strings go in all seven catalogs, and `ar.json` uses
  `one/two/few/many/other` rather than the `one/other` pair most keys use.
- **`DiffRow.Line.Chars` is dead.** It is `text.Length`, read nowhere. Whoever
  adds the fold chip will be tempted to grow it; delete it instead while the
  record is open.
- **The stale memory note.** *Resolved — the note is gone, and as of Phase 1 a
  `SymbolExtractor` genuinely does exist.*

---

## 15. Rule 3 — the checker's blind spots

1. **Bypasses.** None in GitBench. All `unsafe` and interop stays inside the
   submodule, below the `TreeSitter` wrapper — though note that is a **policy,
   not a boundary**: `TSPoint` lives in `TreeSitter.Bindings`, so GitBench takes
   a `using` on the low-level assembly, and `AllowUnsafeBlocks` is already true
   in `GitBench.csproj`. Nothing enforces the line.

   An ABI or lifetime bug is fixed *there* (§3), which moves the review effort to
   that repo rather than removing it. Its `TreeSitter.Bindings.Tests` pin exact
   struct byte offsets and are the guard against a silently shifted field —
   **and until Phase 0's upstream PR #2, nothing runs them.** That is the honest
   answer to this level, and it is why that PR is a Phase 0 item and not a
   nice-to-have.
2. **Changed signatures.** `DiffRenderState.Loaded` **and** `.FullFile` move
   from `DiffHighlight?` to `DiffAnnotations?`; `DiffHighlightCoordinator` is
   renamed and returns a different type; `StartHighlight` and
   `CarryHighlightForward` write both. All internal, all in `Features/Diff`.
   `DiffResult` and `DiffHunk` are deliberately untouched (§5).

   Phase 4 appends `FoldMark?` to `DiffRow.Line`. Checked rather than worried
   about: four construction sites, none affected; `Deconstruct` gains arity so
   positional deconstruction fails to compile rather than misbinding; the only
   silent change is record equality now including `Fold`, and nothing compares
   `DiffRow` by equality.
3. **New boundary crossings.** Grammar load, query compile, capture-name
   validation, embedded resource. All four resolve into `CodeIntelAvailability`
   at construction; none asserts.
4. **Shared mutable state and ordering.**

   The **extractor pool** is shared across threads. Its hazards are a poisoned
   instance returned after a throw, and using a thread-local instead of a pool
   (async continuations resume anywhere). It must also be **bounded**, because
   the review window starts one background lane per visible file and the
   generation guard discards results rather than cancelling work.

   **Fold state** is mutable, lives on `FileBrowserViewModel`, and is touched
   only on the UI thread. It must stay that way. Its ordering hazard is real: a
   `FoldId` resolved against a re-extracted outline is a silently wrong row
   range, not an exception — which is what the structural signature exists to
   catch, and why §9.4 states the one case it does not catch.

   Elsewhere the assumption is the existing generation guard on the diff lane:
   an annotation result is dropped if the view moved on, exactly as highlighting
   is today.
