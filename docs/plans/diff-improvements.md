# Diff improvements

A backlog of code-review enhancements beyond the current line-based Diff View. Single-line
descriptions only — each to be broken down further later.

1. **Intra-line highlighting + moved-code detection** — highlight only the characters that changed within a line, and render relocated blocks as "moved" instead of delete + unrelated add.
2. **Symbol-level change outline** — a tree of what changed structurally (added/modified/removed functions, classes, methods) alongside the hunks.
3. **Semantic (tree-based) diff** — diff the syntax tree instead of lines, so renames, rewraps, and reindents stop reading as noise.
4. **Dependency-graph delta** — show which module edges the change added or removed, and flag new cycles or layer-boundary crossings.
5. **Stacked diffs** — review a large change as a chain of small, individually-reviewable increments.
