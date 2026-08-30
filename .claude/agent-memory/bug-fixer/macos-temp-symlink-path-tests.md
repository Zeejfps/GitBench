---
name: macos-temp-symlink-path-tests
description: On macOS, Path.GetFullPath vs git's realpath diverge under /var/folders (TMPDIR), breaking any test or code that string-compares a self-built path against a git-reported path
metadata:
  type: project
---

macOS `Path.GetTempPath()` returns `/var/folders/...`, and `/var` is a symlink to `/private/var`. .NET `Path.GetFullPath` normalizes *lexically* and does NOT resolve symlinks; git resolves working-tree paths to their realpath. So any comparison of a test-constructed temp path against a git-reported path (`git worktree list --porcelain`, `git rev-parse --git-common-dir` from a linked worktree) fails on macOS while passing on Linux CI and Windows.

**Why:** two known pre-existing macOS-only failures both trace to exactly this — `WorktreeRemoveTests.FailsAndKeepsTheTreeWhenGitRefuses` (GitService.IsRegisteredWorktree's `NormalizeWorktreePath` uses GetFullPath) and `GitRepoLocksTests.Real_git_resolves_a_linked_worktree_onto_the_primary_family` (GitRepoLocks.Normalize, GitBench/Git/GitRepoLocks.cs:103-107). Several worktree tests were authored/validated on Windows (they shell out to `cmd.exe` / `mklink /J`) and have never passed on darwin.

**How to apply:** when a path-comparison test fails only on this machine, check `/var` vs `/private/var` before anything else. The fix altitude is a shared realpath-resolving normalizer (`File/DirectoryInfo.ResolveLinkTarget` or `Path.GetFullPath` + realpath) used by both `GitRepoLocks.Normalize` and `GitService.NormalizeWorktreePath` — not per-test `realpath` calls. Note the same divergence can bite production in `WorktreeSyncService.ScheduleSync`, where a user-picked primary path containing a symlink won't match git's realpath'd entry and the primary would not be filtered out of its own worktree list.
