# Incremental parsing — keeping highlighting honest while the reader types

*Design, revision 2. **Both halves are now built** — the submodule bindings and the app-side producer.
Revision 1 was reviewed and is superseded; what each revision got wrong, including this one, is
recorded at the end. Corrections made while building are marked in place rather than silently edited
in, because the reasoning that was wrong is more useful than a doc that reads as if it never was.*

## The bug

Type into a file in the Files pane and the colouring goes wrong immediately. Inserting `var q=1; ` at
the head of `using System;` paints **`var q=1;`** in keyword blue and leaves the real `using` plain —
the old `using` span still covers columns 0–5. Verified in the running app. It does not recover on
save. Fold chevrons freeze the same way: a method you add gets none.

Two causes, and **both** must be fixed:

1. **Nothing re-parses an edited buffer.** `TreeSitterSyntaxHighlighter.Highlight` and
   `TreeSitterSymbolExtractor.Extract` are called only from `FileContentLoader.Load`, which parses
   bytes read from disk.
2. **`DiffContentView.Opened` stamps every annotation with `document.Opened`** — the revision the
   buffer was *constructed* at — regardless of which revision the annotations actually describe.
   Revision 1 failed to name this line at all.

   **Corrected while building: the mechanism stated here was wrong, though the fix was still needed.**
   That line does not gate a producer, because a producer calls `EditorBuffer.Apply` with its own
   stamp and never goes through the view. What it actually breaks is the **re-read after a save**:
   `RepoDocuments.Open`'s rebind path re-reads the file, finds it saying what the document already
   says, and keeps the buffer — so those annotations are genuinely current, and get stamped with the
   opening revision and thrown away. That is precisely the "does not recover on save" symptom above,
   so this was a real bug in the right place for the wrong reason.

`Revised<T>` refusing stale annotations is not the bug. It is the half that works, and it stays.

## Scope: tree-sitter is the primary path

`RoutedSyntaxHighlighter` tries tree-sitter and falls back to TextMate. Tree-sitter covers 16
languages; TextMate covers 287 extensions. **The 16 are the ones being edited** — C#, TypeScript,
JavaScript, Python, Rust, Go, JSON, YAML, C. This design makes the primary path track the buffer.
TextMate-only languages keep today's behaviour: coloured at open, frozen after.

That is a deliberate scope line, not an oversight. TextMate is 10.9× slower than tree-sitter
(`tree-sitter-highlighting-benchmark.md`: 0.7 MB/s vs 7.1 MB/s, p95 up to 500 ms on large CSS), so a
debounced whole-file TextMate re-highlight is a different proposition with a different cost, and it
can be added later against the same producer seam.

## Why incremental rather than a debounced whole-file re-parse

Whole-file tree-sitter parsing is genuinely cheap — 0.80 ms median for C#. But the median is not where
editing happens. The files in this repository that anyone would open and type in are 1600–4300 lines,
which is the p95/max band: **3.8 ms p95 and 20 ms max for C#, 11–12 ms p95 for Rust, Go and JSON, 115
ms max for C.**

Incremental re-parse is O(edit), not O(file). That is the difference between colouring that lags a
debounce and colouring that tracks typing, and it is the reason tree-sitter has an incremental API at
all. `ts_parser_parse_string`'s existing binding already takes `oldTree` and passes `nint.Zero`
(`TreeSitter.Bindings/TS.cs:94`), documented there as "the incremental-editing path, which nothing here
binds the rest of". This is that.

**And because whole-file is cheap, it is also the safety net.** See Decision 6 — this is what makes the
design's one dangerous failure mode recoverable rather than permanent.

## Decisions

**1. One tree per open document, shared by highlighting and the outline.** Today each of
`TreeSitterSyntaxHighlighter.Highlight` and `TreeSitterSymbolExtractor.Extract` does its own
`NormalizeNewlines`, its own UTF-8 encode and its own `Parser.Parse`, then disposes the tree — every
file is parsed twice. One tree feeding both consumers deletes a parse and is the precondition for
incrementality: you cannot incrementally maintain two independently derived trees.

**This requires a new seam that does not exist today.** Both types only expose
`Highlight(text, languageId)` / `Extract(text, language)`; nothing accepts an already-parsed tree, and
`RoutedSyntaxHighlighter.Shared` is a singleton owning the pools and compiled queries. The new surface
is "run your query over this root node and give me spans / an outline". If that seam turns out not to
be cleanly carvable, Decision 1 is the part to drop — the rest of the design stands without it, at the
cost of keeping two trees.

**2. Incrementalise the root parse; re-derive injections from the new root.** This is what `Collect`
and `Scan` already do — injections are re-discovered per parse and each region sub-parsed over a byte
sub-span — so it is the status quo rather than a new decision, and it is correct by construction: an
incremental parse is *defined* to produce the tree a from-scratch parse would.

Be honest about what it does not buy: only `html`, `markdown` and `markdown_inline` injection queries
ship, and every injected region still gets a fresh full parse with its own array copy. **For `.md` and
`.html` the incremental win is approximately zero.** Those two get whatever Decision 6's fallback path
gives them; optimise later if measured.

**3. The parse buffer is normalized UTF-8, and the mapping is derived from the inverse edit.** See the
next section — this is the crux and revision 1 got it wrong four ways.

**4. All tree and buffer ownership lives on one serialized worker.** The UI thread posts two immutable
values — `(DocumentRevision, TextEdit inverse)` — and never touches a tree, a `Node`, or the parse
buffer. This is not a convention: with the Decision 3 formulation the mapping needs nothing from the
document, so there is nothing for the UI thread to contribute beyond the message.

Constraints this satisfies, all verified: `ParseSessionPool.Use` is synchronous and reclaims the
session at the end of its callback, so a session may not be held across an await — and need not be,
because a tree is independent of its parser once `ts_parser_parse_string` returns. `QueryMatch` is a
`ref struct` valid only inside `ForEachMatch`, so queries must complete within one worker turn.

**5. No parse timeout.** Revision 1 specified `ts_parser_set_timeout_micros`. **It does not exist** —
upstream removed it in 0.25 and the pinned v0.26.9 header has only `ts_parser_parse_with_options` with
a native progress callback, which costs `[UnmanagedCallersOnly]` marshalling and the loss of the
contiguous-buffer path. That is an independent project. Here, delete the `WholeFileBudget` stopwatch
and its log line in both files rather than replacing them: it measures after the parse has already
completed, so it bounds nothing and never did. `MaxFileBytes` (1 MiB, identical in both) remains the
real bound.

**6. Verify and fall back — this is what makes the design safe.** A wrong `TSInputEdit` corrupts the
tree silently: no exception, just plausible-looking wrong colours forever. That was revision 1's
scariest property. The fix exploits the very fact that argued against this design: **a from-scratch
parse costs ~1 ms, so it is an affordable fallback.**

After each incremental re-parse, assert the cheap invariant that the new root node's byte extent equals
the parse buffer's length. On mismatch — or on any exception from the mapping — throw the tree away and
re-parse from scratch. The buffer is authoritative; the tree is a cache. Corruption becomes a
recoverable hiccup instead of a permanent wrong answer, and in tests the fallback is an assertion
failure rather than a silent pass.

**That invariant was doubted in review and then checked, because a root node that stopped at the last
token would make it fire on every file ending in a blank line.** It does not: the root's extent covers
the whole buffer for a trailing newline, several trailing blank lines, trailing tabs and spaces, a
whitespace-only file, an empty file, and a file ending in an unterminated block comment. Measured on
the C# grammar. The invariant is safe to assert.

**And it has teeth — which was doubted next, and is the more important question.** If the extent
always matched, the check would be theatre: it would cost nothing and detect nothing. It is not. A
well-formed edit that merely lies about *where* the change was — every bound satisfied, so nothing
refuses it — produces both a corrupt tree and a root that stops short of the buffer, and the check
catches it. Pinned by `AnEditThatIsMerelyWrongIsCaughtByTheExtentCheckAndTheBufferParsedWhole`.

**The direction is the whole of it, and it is worth knowing before debugging one of these.**
Claiming the edit began *earlier* than it did is harmless: more of the tree is thrown away than
needed and the re-parse is still correct. Claiming it began *later* under-invalidates, so subtrees
that should have been discarded are reused at offsets that no longer hold — and that is the case
that corrupts. **Over-invalidation is safe; under-invalidation is the bug.** Every corrupting edit
that could be constructed also left the root short, so the check caught all of them; whether some
corruption can evade it is unproven, which is why the equivalence suite stays the real net.

**The submodule now catches a smaller class of this for free**, ahead of the fallback: `Reparse`
refuses an edit whose bytes describe no pair of buffers at all — a start past either end, or an end
past the buffer it indexes. Those are the transpositions and off-by-ones a mapping produces while it
is being written, and they arrive as an `ArgumentOutOfRangeException` naming the offset and the buffer
length instead of as a corrupt tree. Decision 6 still carries everything else, because an edit can be
well formed and still describe the wrong change.

## The offset mapping — derived from the inverse edit

**Revision 1 specified this from the *applied* edit and was wrong**: `applied` is only available after
`TextDocument.Apply` has already spliced the piece table, and its range is in pre-edit coordinates — so
slicing `_document.Line(start.Line)` by the old column reads the new line. Every edit with a non-zero
start column on a line with non-ASCII before the caret got an arbitrary byte offset.

Use the **inverse** edit instead. It is already plumbed to exactly the right place —
`EditJournal.ApplyOne` raises `_applied?.Invoke(inverse)` and `EditorRowSet.Reproject(TextEdit undo)`
consumes it — and it works on both the typing path and the undo/redo path, which discards `applied`
entirely (`ApplyReversal` calls the one-argument `Apply`).

The inverse is `TextEdit(range = newStart..newEnd, replacement = the removed old text)`. From it, with
the worker's **pre-edit** buffer and line index:

```
startPoint   = inverse.Range.Start                       // same in old and new coordinates
newEndPoint  = inverse.Range.End
oldEndPoint  = startPoint advanced by inverse.Replacement's breaks and trailing length
startByte    = lineStart[startPoint.Line] + Utf8Len(preEditLine(startPoint.Line)[..startPoint.Column])
oldEndByte   = lineStart[oldEndPoint.Line] + Utf8Len(preEditLine(oldEndPoint.Line)[..oldEndPoint.Column])
newEndByte   = startByte + Utf8Len(NormalizeNewlines(newText between the points))
```

Everything comes from the worker's own state. **Nothing is read from the document**, so the trap that
sank revision 1 is structurally impossible rather than remembered.

**Except one thing, and this formulation cannot work without it.** The inverse edit does not carry the
inserted text — it names *where* the new text sits and *what the old text was*, never what the new
text says. Inserting `abc` and inserting `xyz` produce byte-identical inverse edits. So
`newEndByte`, which the block above computes from "the new text between the points", has nothing to
compute from. The inserted text is read out of the document **on the UI thread, where the document
lives**, and travels with the message as a third value:
`DocumentEdit(DocumentRevision At, TextEdit Inverse, string Inserted)`.

That is a genuine amendment to Decision 4's "two immutable values", not a violation of its point. The
worker still touches no document, no tree and no node, and every *offset* still comes from the
worker's own pre-edit state — so the trap stays structural. What changed is only that the message
carries three values instead of two.

### Only the bytes decide anything — measured, not assumed

**The three point fields are inert on this parse path, and no test of the resulting tree can ever say
otherwise.** Measured while building the submodule half: feeding `Reparse` deliberately absurd points
(row 900 on a four-line file) produces a tree byte-identical to a from-scratch parse. So does zeroing
all three. Across C#, Python and Bash, on edits that re-indent a line, add lines, and move a heredoc
terminator.

The mechanism, so this is a fact about tree-sitter rather than about the fixtures that happened to be
tried:

- The points reach exactly one thing — the column-invalidation heuristic in `ts_subtree_edit`
  (`subtree.c:654,743-746`), which decides *how much* of the old tree to re-lex.
- That heuristic only engages for subtrees flagged `depends_on_column`, and that flag is set only when
  a grammar's external scanner calls `lexer->get_column()` (`parser.c:585`, `lexer.c:296`).
- Of the sixteen vendored grammars, **exactly one scanner calls it** — Bash's. Not Python's: its
  indent tracking is its own state, not the lexer's column.
- And even on Bash the tree comes out right, because `ts_parser_parse_string` is handed the whole new
  buffer and re-derives every row and column from the text.

Three consequences, and they are the reason this is worth a section:

1. **The byte arithmetic is the risky part, and it is the only part worth test effort.** It is what the
   equivalence test actually pins. The submodule's own suite demonstrates this: a one-field byte error
   fails nine tests, while mangling every point fails none.
2. **A point bug is permanently silent.** Nothing downstream of the parse reads these points — node
   positions come from the re-parse, not from the edit — so there is no assertion, anywhere, that will
   catch wrong point arithmetic. Do not write a test that claims to.
3. **Supply them correctly anyway.** The header asks for them, a future reader is entitled to trust
   them, and a custom `TSInput` reader (not planned) would change the calculus. But budget them as
   bookkeeping, not as risk.

So the two point corrections below are still the right arithmetic — they are simply not where the bugs
that hurt will be. Weight the line-index correction accordingly higher than the other two.

Three corrections to revision 1's arithmetic, all found in review:

- **`newEndPoint` has two cases and revision 1 specified neither.** With no line break in the inserted
  text it is `(startPoint.Row, startPoint.Column + Utf8Len(inserted))` — the start column is added.
  With one or more breaks it is `(startPoint.Row + breaks, Utf8Len(tail after the last break))` with no
  start column. `TextEdit.EndOfReplacement` already handles exactly this shape for UTF-16 columns;
  copy its structure.
- **The line index is spliced, not shifted.** `lineStart[start.Line]` does not move; the change begins
  at `start.Line + 1`. An edit changes the *number* of lines, so remove `end.Line − start.Line`
  entries, insert one per break in the normalized new text, and only then shift the tail by
  `newEndByte − oldEndByte`. Revision 1 said "shift from `start.Line` onward", which gets a whole-line
  deletion wrong on the first try.
- **Pin the indexing base.** `FileLine` is 1-based, `TSPoint.Row` is 0-based, and revision 1 mixed
  `lineStart[start.Line]` with `start.Line - 1` in the same six lines. The line index is 1-based to
  match `FileLine`; the point row is `Line - 1`.

Verified and not to be re-litigated: `TSPoint.Column` is **bytes**, not characters
(`TSPoint.cs:7-8`), so `startByte − lineStart[line]` is the right column. Document lines and
normalized-buffer lines are 1:1, because `TextDocument` counts CRLF and lone CR as one terminator each
and `NormalizeNewlines` maps each to one `\n` — so a lone-CR file works. `Utf8Len` must *walk*;
`Utf8ToUtf16Offsets.Build` already does this in the other direction and is the thing to mirror.

**Why normalizing the replacement in isolation is safe** — state this, it is load-bearing. A
replacement ending in `\r` immediately before an existing `\n` would normalize to two breaks where the
document has one. `KeepTerminatorsWhole` makes that unrepresentable: it widens the edit when
`before == '\r' && opensWithLf` or `after == '\n' && closesWithCr`, which are the only two straddles
possible. Without that invariant this is a correctness hole, not a length adjustment.

**Cost, stated honestly.** Revision 1 claimed "O(edit size)". It is not: the byte splice is O(file) and
allocates, the line-index splice is O(lines after the edit), and `Parser.Parse` copies the span again
because `SyntaxTree` owns its source. A keystroke at the top of a 1 MiB file is two full-buffer
memcpys plus an index splice before any parsing happens. That is still far below a full re-parse of a
large file, but it is not free, and a gap-buffer for the parse bytes is the obvious later optimisation
if it measures.

## Submodule work — `external/cs_tree_sitter` — BUILT

On branch `incremental-parse`, uncommitted. 134 tests green (29 bindings, 105 wrapper), zero warnings
under `TreatWarningsAsErrors`.

Smaller than revision 1 claimed. **`TSPoint` already exists** (`TreeSitter.Bindings/TSPoint.cs`), and
**`ts_parser_parse_string` already takes `oldTree`** (`TS.cs:94`) — no new parse binding.

`TreeSitter.Bindings/TS.cs`:
- `TSInputEdit { uint StartByte, OldEndByte, NewEndByte; TSPoint StartPoint, OldEndPoint, NewEndPoint; }`
- `ts_tree_edit(nint tree, in TSInputEdit edit)`

Not `ts_tree_copy`: Decision 4 gives one serialized owner, so there is no second reader, and the
bindings' own policy is that an unused binding is an untested binding.

`TreeSitter/` — and this is the part revision 1 missed entirely. **`SyntaxTree` owns a private copy of
its source bytes** (`SyntaxTree.cs:36`) and `Node.Text` reads out of it. After `ts_tree_edit` the
tree's offsets describe the new text while `_source` holds the old, so `Node.Text` either throws
"lies outside the N-byte source" or silently returns the wrong substring — and **both consumers read
node text** (`TreeSitterSymbolExtractor` for names and types, `DynamicLanguageOf` for fence
languages).

So the wrapper is not `Edit` plus a `Parse` overload. It is one operation that cannot be misused:

```
SyntaxTree.Reparse(Parser parser, in TSInputEdit edit, ReadOnlySpan<byte> newUtf8) -> SyntaxTree
```

which applies the edit, re-parses with itself as the old tree, swaps in the new source, and consumes
the old tree. A caller cannot pair a tree with a buffer it did not come from, and there is no window
in which an edited-but-unreparsed tree is reachable. A `Parse(span, oldTree)` overload would be the
optional-field bag: any tree with any buffer, silently corrupt.

It takes `TSInputEdit` directly rather than a wrapper `InputEdit` as sketched above: the wrapper layer
already surfaces `TSPoint` from `Node.StartPoint`, so a parallel copy of the struct would have been
duplication for nothing.

Also: `Parser.Parse` currently throws on NULL with a comment asserting NULL cannot happen. That stays
true — without a timeout, NULL remains unreachable.

**What the build added beyond the sketch**, all of it consumed by the app half:

- **`Parse` keeps its signature.** The old-tree parameter lives on an `internal ParseWith`, so nothing
  public can pair a tree with a foreign buffer. `Reparse` reaches it as a same-assembly caller.
- **Refusals, before the tree is touched:** a disposed tree or parser, a parser on another grammar
  (compared by grammar handle, not by `Language` identity), and the impossible-edit checks above. Each
  leaves the tree untouched and still usable, which is asserted rather than assumed — a guard placed
  after `ts_tree_edit` would hand back an exception plus a tree describing text nobody has.
- **The old tree is released on every path, including a throwing re-parse**, and the release happens
  after the new tree exists, so the subtrees the two share are never the last reference dropped.

**A tree is not bound to the parser that made it, and this is now tested** — it matters because
`ParseSessionPool.Use` hands out whichever session is idle, so being re-parsed by a *different* parser
is the normal case here rather than an edge one. It holds even if the original parser has since been
disposed: `ts_tree_edit` and `ts_tree_delete` each build their own local `SubtreePool` (`tree.c`)
rather than borrowing the parser's. A pool that retires an idle session cannot take the open files
with it.

**Equivalence is tested on Python and Bash as well as C#**, because external scanners are where
subtree reuse actually breaks and C# has none — Python for indentation, Bash for heredoc state
carried across the lines an edit sits between.

## Where it lives — BUILT

Built as described, with the shape confirmed and two things the sketch did not have. 4247 app tests
green; Decision 1 was deliberately dropped, so the highlighter and the outline keep a tree each.

**Discovery is `Opened`/`Closed`, not `Edited`.** `IDocumentStore.Edited` is an `Action<string>` — a
path, no payload — and nothing maps a path back to an `EditorBuffer`, so the producer could not have
found the document it was told about. `IDocumentStore` now raises `Opened`/`Closed` carrying the
buffer, and the producer subscribes to the buffer's own edits from there. **This gives the reload case
for free**, which the lifecycle list below asks for as special handling: a reload runs
`RepoDocuments.Open`'s `Forget`-then-create path, so it arrives as `Closed` + `Opened` and the tree is
discarded and rebuilt with no reload-specific code at all.

**The inverse edit had one consumer's worth of room and now has two.** `EditJournal` takes a single
`Action<TextEdit> applied`, and `EditorBuffer` had already spent it on `EditorRowSet.Reproject`
(`EditorBuffer.cs:67`). `EditorBuffer` now owns that callback and fans out: `Reproject` first, once
per edit, mid-edit — its ordering guarantee untouched, and a merely-interested listener cannot get
between the document and its projection — then an `Edited` event carrying the `DocumentEdit`.
Widening the journal's callback into a list was rejected: it would put a multi-consumer concern in the
undo type, which has no document identity to mint a revision stamp from.

**Not in `RepoDocuments.Entry`** — revision 1 proposed that and the review is right to refuse it.
`Entry` has no `Dispose`; `Forget` and `Dispose` only call `Unfollow()`, so a native tree hung off it
leaks per closed tab and per closed repo until disposal is threaded through four call sites. And
`RepoDocuments.AssertThread` exists to enforce "UI thread only", so putting a worker inside is the type
enforcing a rule it breaks.

Instead: a `DocumentAnnotations` producer that **subscribes to `IDocumentStore.Edited`** and lives
outside the store — the same shape as `DocumentBackedText`, which already does exactly this for the
language server. One new type, no change to the store's contract, no lifetime surgery.

Flow: `Edited(path)` → debounce → worker applies queued inverse edits and re-parses → runs both
queries → posts `Revised<EditorAnnotations>` stamped with the revision captured at queue time →
`EditorBuffer.Apply` refuses it if the document has moved on again.

`DocumentRevision.Of` takes a `TextDocument` and the worker has none, so **the stamp is captured on the
UI thread at queue time and travels with the edit.** Do not add a `DocumentRevision(int)` constructor;
that is the whole safety net.

Debounce: start at ~50 ms. Incremental re-parse of a keystroke is microseconds, so this can be far
tighter than the language server's 500 ms; the residual cost is the queries and the injection re-scan.

## Lifecycle cases revision 1 omitted

- **Undo/redo** produce edits like any other, but in bulk: `ApplyReversal` applies a whole list
  back-to-front, each bumping `Revision`. One Ctrl+Z is N queued edits and N revisions; the stamp must
  be the **last** one.
- **Reload from disk** (`DocumentReconciliation.Reloaded`) replaces the document wholesale and produces
  **no edits at all**. The tree and buffer must be **discarded and rebuilt**, not edited. "Edit the
  tree with whatever arrives" is the wrong default here.
- **Save rebinding the session** is a no-op for the parse: `RepoDocuments.Open`'s rebind path keeps the
  same `EditorBuffer` and `TextDocument` when the re-read text matches. Worth stating because it looks
  dangerous and is not.
- **The first parse** has no tree: build the buffer from the current text, parse with `oldTree = null`,
  and skip `ts_tree_edit`. This is where an implementer double-applies the first edit.
- **Truncated files cannot reach here** — `EditorBuffer.TryOpen` requires `FileWriteBack.Reversible`
  and a truncated read yields `Refused{Truncated}`.
- **Memory:** per open tab, the parse buffer plus `SyntaxTree`'s own copy of the same bytes plus the
  native tree. Tabs live for the app session and `RepoDocuments` never evicts. Budget it; twenty
  200 KB tabs is a few MB.
- **`EditorRowSet.Rebuild` nulls the outline but keeps the highlight**, so after a resync the chevrons
  vanish while stale colours persist. Decide which is the intended interim state.

## Tests

**The headline test: equivalence under random edits.** Over a corpus, apply a random edit sequence and
assert after *each* edit that the incrementally maintained result equals a from-scratch parse of the
same text. A bad `TSInputEdit` produces plausible wrong colours, not an error, so nothing weaker will
catch it. Assert on `TokenSpan`s, **not** on byte captures — `TokenSpan` columns are tab-*expanded*, so
a byte-level comparison passes on tab-indented files that render wrong.

**What that test can and cannot pin.** It pins the byte arithmetic completely, which is the arithmetic
worth pinning. It pins nothing at all about the three point fields — see the offset-mapping section —
so do not add cases hoping to cover them, and do not read a green suite as evidence the points are
right. The submodule's suite behaves exactly this way and the effect is measured there.

- Decision 6's fallback fires on a deliberately corrupted edit, and recovers. **Both halves of it:**
  an edit that breaks a bound (refused by `Reparse`, arrives as an exception) and an edit that breaks
  no rule and is merely wrong about where the change was (caught by the extent check). The second is
  the one that matters and the one that looks untestable — it is not; drift the start *later*, since
  under-invalidation is what corrupts.
- Offset mapping over CRLF, lone-CR, CJK, surrogate pairs, tabs; a multi-line edit; a whole-line
  deletion; an edit at end of file.
- A widened edit (typing `\n` after a lone `\r`) maps correctly.
- Undo of a multi-edit transaction stamps the last revision.
- A reload discards the tree rather than editing it.
- A result whose revision no longer describes the document is refused, end to end.
- The regression this exists to fix: type at the head of a line and the keyword colouring follows the
  keyword.

## What revision 1 got wrong

Recorded so the next reader does not re-derive it: the timeout API does not exist in the pinned
version; `TSPoint` and the `oldTree` parameter already exist; the mapping must come from the inverse
edit, not the applied one, and its line-index and `newEndPoint` arithmetic were wrong; `SyntaxTree`
owning its source bytes was missed entirely; `RepoDocuments.Entry` is not a viable home; and
`DiffContentView.Opened` — the line that refuses every annotation regardless of producer — went
unnamed. Revision 1 also cited SQL, regex and Svelte injections, none of which have grammars here.

## What revision 2 got wrong

Found by building it, and recorded to the same standard:

- **The inverse edit cannot describe the change on its own**, and the offset formula assumed it could
  while simultaneously insisting nothing be read from the document. Both cannot hold: `abc` and `xyz`
  inserted at the same place produce identical inverse edits. The message carries the inserted text as
  a third value. This was the load-bearing error.
- **It named the wrong mechanism for `DiffContentView.Opened`.** "Even a perfect producer is refused"
  is false — a producer never goes through that line. The real casualty was the post-save re-read,
  which is the doc's own "does not recover on save" symptom, so the fix stood and the reasoning did
  not.
- **It treated the point arithmetic as load-bearing** and spent two of its three corrections there.
  The points do not affect the tree at all. The effort belongs on the byte offsets and the line-index
  splice.
- **It doubted nothing about Decision 6's invariant, which deserved doubting twice** — first whether
  it fires when it should not (it does not), then whether it ever fires at all (it does, on
  under-invalidation). Both were asserted before either was measured.
- **`IDocumentStore.Edited` was the wrong subscription** — a path with no payload, and no way to reach
  the buffer it names. Discovery had to be `Opened`/`Closed`.
- **`InputEdit` as a wrapper type was unnecessary** — `TSPoint` already crosses that boundary.
- What did survive contact with the build: the `DocumentAnnotations`-outside-the-store shape, the
  serialized-worker ownership model, the revision stamp as the staleness guard, and the
  `SyntaxTree`-owns-its-bytes hazard.

**The pattern across both revisions is worth naming**, since there will be a revision 3 of something:
every error here was a claim stated with the same confidence as the claims that were right, and the
ones that survived were the ones about *structure* — where a thing lives, who owns it, what cannot be
represented. The ones that failed were about *behaviour* nobody had run yet. Structure can be
reasoned out; behaviour has to be measured, and this design measured none of it until it was built.
