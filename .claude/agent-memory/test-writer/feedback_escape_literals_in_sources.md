---
name: escape-literals-in-sources
description: Terminal test sources must spell control characters as \u escapes — and the Write tool emits literal control bytes instead, so always scan and post-fix
metadata:
  type: feedback
---

Never write a literal ESC (or any control character, or a non-ASCII glyph) into a C# source file in this repo — always `"\u001b"`, `"\u007f"`, `"\u00e9"`, `"\U0001F600"`.

**Why:** the repo states the reason in `GitBench.Tests/Terminal/TerminalGridViewTests.cs` — an escape character in a source literal is invisible in every diff and review that follows. Terminal work is full of them, so this is not a corner case there.

**How to apply:** writing a file through the Write tool reliably emits the *actual* control byte when the intent was the escape text, and the mistake is invisible on screen. After writing any file containing control-character or non-ASCII literals, scan it and fix in place:

```
perl -0777 -ne 'my %h; while (/([\x00-\x08\x0b\x0c\x0e-\x1f\x7f]|[^\x00-\x7f])/g) { $h{sprintf("%02x",ord($1))}++ } print join(", ", map {"0x$_=$h{$_}"} sort keys %h), "\n"' FILE
perl -0777 -i -pe 's/\x1b/\x5cu001b/g; s/\x7f/\x5cu007f/g' FILE
```

Use `\x5c` for the backslash in the replacement: a literal `\\u001b` written through the Bash tool arrives at perl as `\u001b`, where perl reads `\u` as its titlecase operator and silently eats the backslash. Em dashes in comments are fine and match house style; leave them.

Best structure for escape-heavy expectations: `const string Esc = "\u001b"; const string Csi = Esc + "["; const string Ss3 = Esc + "O";` — constant concatenation is legal inside `[InlineData(...)]`, so a table reads as `Csi + "1;5C"`.

Related: [[gui-harness-keyboard]].
