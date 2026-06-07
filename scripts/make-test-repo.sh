#!/usr/bin/env bash
# Generates a git repo with a deliberately busy history so GitBench's commit
# graph exercises every renderer path:
#   - many concurrent lanes (parallel branches -> pass-through verticals)
#   - 2-parent merges            -> gradient incoming curves
#   - an octopus (3-parent) merge -> several gradient incoming curves at once
#   - long-running unmerged branches
#   - stashes                    -> dashed edges
#   - tags, and a remote (origin) -> remote-branch badges
#
# Usage: scripts/make-test-repo.sh [target-dir]
set -euo pipefail

TARGET="${1:-$HOME/gitbench-graph-demo}"
REMOTE="${TARGET}-origin.git"

rm -rf "$TARGET" "$REMOTE"
mkdir -p "$TARGET"
cd "$TARGET"

git init -q -b main
git config user.name "Graph Demo"
git config user.email "demo@example.com"
git config commit.gpgsign false

# Monotonic, deterministic commit timestamps so lane ordering is stable.
BASE="2024-01-01 09:00:00"
T=0
stamp() {
  D=$(date -v+${T}M -jf "%Y-%m-%d %H:%M:%S" "$BASE" "+%Y-%m-%dT%H:%M:%S")
  T=$((T + 9))
}
commit() { # commit <message> <file> <text>
  printf '%s\n' "$3" >> "$2"
  git add -A
  stamp
  GIT_AUTHOR_DATE="$D" GIT_COMMITTER_DATE="$D" git commit -q -m "$1"
}
merge() { # merge <message> <branch...>
  stamp
  GIT_AUTHOR_DATE="$D" GIT_COMMITTER_DATE="$D" git merge --no-ff -q -m "$1" "${@:2}"
}

# --- mainline + a long-running parallel branch (develop) ---
commit "Initial commit" main.txt "root"
commit "Add build config" main.txt "build"
git branch develop

commit "main: docs" main.txt "docs"
git switch -q develop
commit "develop: scaffold" dev.txt "scaffold"

# --- feature-a branches off main, gets work, merges back ---
git switch -q main
git switch -q -c feature-a
commit "feature-a: start" a.txt "a1"
commit "feature-a: more" a.txt "a2"
git switch -q develop
commit "develop: api" dev.txt "api"
git switch -q feature-a
commit "feature-a: finish" a.txt "a3"

git switch -q main
commit "main: tweak" main.txt "tweak"
merge "Merge feature-a" feature-a
git tag v1.0
git branch -d feature-a >/dev/null 2>&1 || true

# --- two features that will land together in an octopus merge ---
git switch -q -c feature-b
commit "feature-b: ui" b.txt "b1"
commit "feature-b: ui polish" b.txt "b2"
git switch -q develop
commit "develop: db" dev.txt "db"
git switch -q main
git switch -q -c feature-c
commit "feature-c: cache" c.txt "c1"

git switch -q main
commit "main: release prep" main.txt "prep"
merge "Octopus: land feature-b and feature-c" feature-b feature-c
git branch -d feature-b >/dev/null 2>&1 || true
git branch -d feature-c >/dev/null 2>&1 || true

# --- a branch left UNMERGED so it stays a parallel lane to the end ---
git switch -q -c feature-d
commit "feature-d: experiment" d.txt "d1"
commit "feature-d: experiment 2" d.txt "d2"

git switch -q develop
commit "develop: telemetry" dev.txt "telemetry"

git switch -q main
commit "main: changelog" main.txt "changelog"
commit "main: bump version" main.txt "v2"
git tag v2.0

# --- stashes on main: dashed edges in the graph ---
printf 'wip edit one\n' >> main.txt
git stash push -q -m "wip: refactor pass"
printf 'wip edit two\n' >> scratch.txt
git add -A
git stash push -q -m "wip: scratch notes"

# --- a remote so remote-branch badges render ---
git init -q --bare "$REMOTE"
git remote add origin "$REMOTE"
git push -q origin main develop
# push a branch then delete it locally to leave a remote-tracking branch around
git switch -q -c hotfix-remote
commit "hotfix: remote-only fix" hot.txt "fix"
git push -q origin hotfix-remote
git switch -q main
git branch -D hotfix-remote >/dev/null 2>&1 || true
git fetch -q origin

git switch -q main
echo
echo "Created test repo at: $TARGET"
echo "  bare remote at:     $REMOTE"
echo
git -c color.ui=always log --all --oneline --graph --decorate | head -40
