---
name: no-lookup-escape-hatches
description: Don't propose indexers/lookup helpers on shared data records; the house pattern is a total switch at the consumer, and validation never goes in a record's primary constructor
metadata:
  type: feedback
---

Do not design a public indexer or `Get(int)` helper onto a shared data record (theming
palettes, style bags) just because one consumer wants integer lookup. Write the total
switch at the consumer instead — a `byte`/enum switch whose arms are exhaustive so the
`_` arm is a legal case rather than a throwing escape hatch.

**Why:** adjudicated on `AnsiColors` during the terminal palette work (round 1 review of
`GitBench.Tests/Terminal/TerminalPaletteTests.cs`). A public unchecked integer path on a
shared type, plus a throwing default, was judged worse than duplicating a switch. The
repo has zero indexers in `GitBench/Theming` or `GitBench/Features`, and
`DiffRowPainter.SlotColor` / `CodeBlockWidget.SlotColor` are the same total switch
duplicated across two files on purpose.

**How to apply:** when a test wants `styles.Ansi[n]`, that is a signal to test through the
consumer, not to add the indexer. A test that can only be written via the indexer
(e.g. "dark and light disagree on every slot") becomes a `[Fact]` over a named-slot
helper array — `(string Name, uint Color)[] AnsiSlots(theme, ansi)` — so the failure
message still names the slot, and reuse that helper anywhere else enumerating the same
fields so the hand-maintained list exists once.

Related: **never put validation in a record's primary constructor** and then rely on it.
`with` uses the compiler-generated copy constructor and does not re-run primary-ctor
validation, so test fixtures built with `with { … }` bypass it silently. For invariants
like "every theme colour is opaque", tolerate-and-force at the consumer and guard the real
theme literals with a separate CI test that walks the named fields.

See [[project-terminal-renderer-modules]].
