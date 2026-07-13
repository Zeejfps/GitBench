#!/usr/bin/env bash
# Generates a whole suite of git repos parked in specific states so GitBench's
# flows can be stress-tested: working-tree variety, conflicts and *in-progress*
# operations (merge / rebase / cherry-pick / revert / bisect), branch & remote
# states, stashes, submodules, worktrees, cross-repo change sets (same-named
# branches linked across sibling repos), and large-history perf.
#
# Each scenario lands in its own folder under the root, so you can add the whole
# root to GitBench (or pick individual repos). Bare remotes used to fake
# ahead/behind live under <root>/.remotes and aren't meant to be opened.
#
# Usage:
#   scripts/make-test-repos.sh [--root DIR] [--list] [scenario ...]
#
#   --root DIR   where to generate (default: $HOME/gitbench-test-repos)
#   --list       print the scenario list and exit
#   scenario...  only generate the named scenarios (folder names); default: all
#
# Env knobs:
#   COUNT_BIG    commits in the big-history repo (default: 800)
set -euo pipefail

ROOT="$HOME/gitbench-test-repos"
LIST_ONLY=0
SELECT=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --root|-r) ROOT="$2"; shift 2 ;;
    --list|-l) LIST_ONLY=1; shift ;;
    --help|-h) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *) SELECT+=("$1"); shift ;;
  esac
done

# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

# Deterministic, monotonic commit timestamps via raw epoch (@unixtime) so we
# never shell out to date(1) and the result is identical on macOS and Linux.
_EPOCH_BASE=1704099600   # 2024-01-01 09:00:00 UTC, arbitrary stable anchor
_EPOCH=$_EPOCH_BASE
_STEP=540                # 9 minutes between commits
stamp() {
  export GIT_AUTHOR_DATE="@${_EPOCH} +0000"
  export GIT_COMMITTER_DATE="@${_EPOCH} +0000"
  _EPOCH=$((_EPOCH + _STEP))
}

init_repo() {
  git init -q -b main
  git config user.name "GitBench Tester"
  git config user.email "tester@gitbench.local"
  git config commit.gpgsign false
  git config core.quotepath false      # show UTF-8 paths literally
  git config advice.detachedHead false
}

new_repo() { # new_repo <folder-name>
  local dir="$ROOT/$1"
  rm -rf "$dir"
  mkdir -p "$dir"
  cd "$dir"
  _EPOCH=$_EPOCH_BASE
  CURRENT_REPO="$dir"
  init_repo
}

write()  { local f="$1"; shift; printf '%s\n' "$@" >  "$f"; }
append() { local f="$1"; shift; printf '%s\n' "$@" >> "$f"; }
ci()     { git add -A; stamp; git commit -q -m "$1"; }
commit() { # commit <msg> [file] [text]
  local m="$1" f="${2:-changes.txt}" t="${3:-$1}"
  printf '%s\n' "$t" >> "$f"; git add -A; stamp; git commit -q -m "$m"
}
merge()  { stamp; git merge --no-ff -q -m "$1" "${@:2}"; }

add_remote() { # add_remote [refspec ...]   (default: --all) push to a fresh bare origin
  local remote="$ROOT/.remotes/$(basename "$CURRENT_REPO").git"
  rm -rf "$remote"; mkdir -p "$(dirname "$remote")"
  git init -q --bare "$remote"
  git remote add origin "$remote"
  if [ "$#" -gt 0 ]; then git push -q -u origin "$@"; else git push -q origin --all; fi
}

sub_upstream() { # sub_upstream <name> <n>   build $ROOT/.subs/<name> with n commits on main
  local dir="$ROOT/.subs/$1" n="$2" i
  rm -rf "$dir"; mkdir -p "$dir"
  ( cd "$dir"; init_repo
    for ((i = 1; i <= n; i++)); do printf 'v%d\n' "$i" > lib.txt; git add -A; stamp; git commit -q -m "lib v$i"; done )
}

# ---------------------------------------------------------------------------
# scenarios
# ---------------------------------------------------------------------------

gen_empty() {                                   # 00-empty
  new_repo 00-empty
  write staged.txt "staged but never committed"; git add staged.txt
  write untracked.txt "untracked file"
}

gen_single() {                                  # 01-single-commit
  new_repo 01-single-commit
  commit "Initial commit" README.md "# Hello"
}

gen_linear() {                                  # 02-linear
  new_repo 02-linear
  local i
  for i in $(seq 1 10); do commit "Commit message number $i" log.txt "entry $i"; done
}

gen_detached() {                                # 03-detached-head
  new_repo 03-detached-head
  commit "c1" a.txt 1; commit "c2" a.txt 2
  commit "c3" a.txt 3; commit "c4" a.txt 4
  git checkout -q HEAD~2
}

gen_long_messages() {                           # 04-long-messages
  new_repo 04-long-messages
  commit "Initial commit" a.txt seed
  append a.txt change; git add -A; stamp
  git commit -q \
    -m "Refactor the entire rendering pipeline to support sub-pixel anti-aliasing, gradient lane edges, and a brand new layout pass that should finally fix the long-standing clipping issues on narrow sidebars" \
    -m "This is the body of the commit. It spans multiple paragraphs to exercise the commit detail view's wrapping and scrolling behaviour." \
    -m "- bullet one with some detail
- bullet two that is considerably longer than the first bullet so it wraps
- bullet three
Closes #1234, #1235. Co-authored-by: Someone <someone@example.com>"
  commit "short" a.txt tail
}

gen_mixed_changes() {                           # 10-mixed-changes
  new_repo 10-mixed-changes
  write tracked-a.txt "original a"
  write tracked-b.txt "original b"
  write tracked-c.txt "original c"; ci "Initial files"
  append tracked-a.txt "staged change"; git add tracked-a.txt
  append tracked-b.txt "unstaged change"
  write untracked.txt "brand new"
  write staged-new.txt "added to index"; git add staged-new.txt
  append tracked-c.txt "staged part"; git add tracked-c.txt
  append tracked-c.txt "then unstaged part"
}

gen_staged_only() {                             # 11-staged-only
  new_repo 11-staged-only
  write base.txt "base"; ci "base"
  append base.txt "more"; write extra.txt "extra"; git add -A
}

gen_renames() {                                 # 12-renames
  new_repo 12-renames
  write a-config.yaml "key1: value1" "key2: value2" "key3: value3" "key4: value4"; ci "add config"
  write helper.py "def helper():" "    return 42" "" "x = 1" "y = 2" "z = 3"; ci "add helper"
  write keep.txt "stable line one" "stable line two" "stable line three"; ci "add keep"
  git mv a-config.yaml settings.yaml          # staged pure rename
  git mv helper.py utils.py                    # staged rename + modify
  append utils.py "# appended after rename"; git add utils.py
  mv keep.txt renamed-on-disk.txt              # unstaged rename
}

gen_deletions() {                               # 13-deletions
  new_repo 13-deletions
  write d1.txt one; write d2.txt two; write d3.txt three; ci "add files"
  git rm -q d1.txt                             # staged delete
  rm d2.txt                                     # unstaged delete
}

gen_mode_change() {                             # 14-mode-change
  new_repo 14-mode-change
  write build.sh "#!/bin/sh" "echo building"
  write deploy.sh "#!/bin/sh" "echo deploy"; chmod +x deploy.sh
  ci "add scripts (deploy executable)"
  chmod +x build.sh                            # unstaged: -x -> +x
  chmod -x deploy.sh                           # unstaged: +x -> -x
}

gen_many_files() {                              # 15-many-changes
  new_repo 15-many-changes
  mkdir -p src
  local i
  for i in $(seq 1 150); do printf 'content %d\n' "$i" > "src/file_$i.txt"; done
  ci "add 150 files"
  for i in $(seq 1 150); do printf 'modified %d\n' "$i" >> "src/file_$i.txt"; done
  for i in $(seq 1 30);  do printf 'new %d\n' "$i" > "src/new_$i.txt"; done
  for i in $(seq 1 10);  do rm "src/file_$i.txt"; done
  for i in $(seq 80 150); do git add "src/file_$i.txt"; done   # stage ~half
}

gen_binary_large() {                            # 16-binary-large
  new_repo 16-binary-large
  head -c 2048 /dev/urandom > image.bin
  seq 1 5000 > big.txt; ci "add binary + large text"
  head -c 1024 /dev/urandom >> image.bin       # modify binary (unstaged)
  awk 'NR==2500{$0="CHANGED LINE 2500"} NR==4000{$0="CHANGED LINE 4000"} {print}' \
    big.txt > big.txt.tmp && mv big.txt.tmp big.txt
}

gen_unicode() {                                 # 17-unicode-emoji
  new_repo 17-unicode-emoji
  printf 'café résumé naïve\n'   > "café-menu.txt"
  printf '日本語のテキスト\n'      > "日本語.txt"
  printf 'launch 🚀🔥✨\n'        > "rocket-🚀.txt"
  git add -A; stamp; git commit -q -m "Add unicode files ☕🌍"
  printf 'more 中文 content\n' >> "日本語.txt"      # unstaged change
  printf 'añadir contenido\n'  > "español-ñ.txt"    # untracked unicode
  printf 'special of the day\n' >> "café-menu.txt"; git add -A; stamp
  git commit -q -m "Update menu 🍽️" -m "Added daily specials ✨ and fixed a typo"
}

gen_line_endings() {                            # 18-line-endings
  new_repo 18-line-endings
  printf 'first\r\nsecond\r\nthird\r\n'  > crlf.txt
  printf 'alpha\nbeta\ngamma\n'          > spaces.txt; ci "add crlf + normal"
  printf 'first\nsecond\nthird\n'        > crlf.txt    # CRLF -> LF, whole file
  printf 'alpha   \nbeta\t\ngamma\n'     > spaces.txt  # trailing whitespace only
}

gen_gitignore() {                               # 19-gitignored
  new_repo 19-gitignored
  printf '*.log\nbuild/\n.env\n' > .gitignore
  write app.txt "app"; ci "add app + gitignore"
  printf 'secret\n' > .env
  printf 'log line\n' > debug.log
  mkdir -p build; printf 'artifact\n' > build/out.o
  printf 'real new file\n' > feature.txt              # untracked, not ignored
  write tracked.log "tracked before it was ignored"
  git add -f tracked.log; ci "force-add a .log"
  append tracked.log "change to a tracked-but-ignored file"
}

gen_intra_line() {                              # 1a-intra-line
  # Exercises intra-line (changed-character) emphasis, which fires on REPLACE BLOCKS: a run of
  # removed lines immediately followed by a run of added lines, paired index-wise and word-diffed.
  # Covers single-word/leading/trailing/multi-range edits, the similarity gate (a full rewrite
  # must read as a plain delete+add with NO emphasis), tab/whitespace boxing, an unbalanced block
  # (more removed than added -> only the paired index lights up), and a multi-line block. Uses .ts
  # so the emphasis layers under syntax colors. Spread across a commit diff + staged + unstaged.
  new_repo 1a-intra-line

  # --- a committed in-place edit, so the COMMIT diff also shows intra-line emphasis ---
  write committed-edit.ts \
    "export function calculateTotalPrice(quantity: number): number {" \
    "  const unitPrice = 9.99;" \
    "  return quantity * unitPrice;" \
    "}"
  ci "Add price helper"
  write committed-edit.ts \
    "export function calculateTotalPrice(amount: number): number {" \
    "  const unitCost = 9.99;" \
    "  return amount * unitCost;" \
    "}"
  ci "Rename quantity->amount, unitPrice->unitCost"

  # --- baseline for the working-tree demo files ---
  write edits.ts \
    "// Intra-line emphasis demo - each edited line below is its own replace block." \
    "export const greeting = \"hello world\";" \
    "const KEEP_A = true;" \
    "const radius = 10;" \
    "const KEEP_B = true;" \
    "let userName = \"alice\";" \
    "const KEEP_C = true;" \
    "const total = price + tax + shipping;" \
    "const KEEP_D = true;" \
    "function add(a: number, b: number): number { return a + b; }" \
    "const KEEP_E = true;" \
    "const filePath = \"src/old/module/file.ts\";" \
    "const KEEP_F = true;" \
    "const message = \"completely unrelated original sentence\";"
  write whitespace.txt \
    "keep this line stable" \
    "this line gains trailing space" \
    "columns    are    space    aligned" \
    "keep this one stable too"
  write block-reindent.ts \
    "function reindentDemo() {" \
    $'\tconst first = 1;' \
    $'\tconst second = 2;' \
    $'\tconst third = 3;' \
    $'\treturn first + second + third;' \
    "}"
  write unbalanced.txt \
    "config alpha = 1" \
    "config beta = 2" \
    "config gamma = 3" \
    "config delta = 4"
  ci "Add working-tree intra-line demo files"

  # --- UNSTAGED edits: single-line cases + whitespace boxing ---
  write edits.ts \
    "// Intra-line emphasis demo - each edited line below is its own replace block." \
    "export const greeting = \"hello there\";" \
    "const KEEP_A = true;" \
    "const radius = 25;" \
    "const KEEP_B = true;" \
    "let userName = \"bob\";" \
    "const KEEP_C = true;" \
    "const total = cost + tax + freight;" \
    "const KEEP_D = true;" \
    "function add(a: number, c: number): number { return a + c; }" \
    "const KEEP_E = true;" \
    "const filePath = \"src/new/module/file.ts\";" \
    "const KEEP_F = true;" \
    "const note = 42;"
  write whitespace.txt \
    "keep this line stable" \
    "this line gains trailing space   " \
    "columns are space aligned" \
    "keep this one stable too"

  # --- STAGED edits: a tab reindent block + an unbalanced (3 removed, 1 added) block ---
  write block-reindent.ts \
    "function reindentDemo() {" \
    $'\t\tconst first = 1;' \
    $'\t\tconst second = 2;' \
    $'\t\tconst third = 3;' \
    $'\t\treturn first + second + third;' \
    "}"
  git add block-reindent.ts
  write unbalanced.txt \
    "config alpha = 100" \
    "config delta = 4"
  git add unbalanced.txt
}

gen_merge_conflict() {                          # 20-merge-conflict
  new_repo 20-merge-conflict
  write README.md "# Project" "" "Status: stable"; ci "Add README"
  write app.py "def main():" "    print('hello')"; ci "Add app"
  write notes.txt "keep me around"; ci "Add notes"

  git switch -q -c feature
  write README.md "# Project" "" "Status: EXPERIMENTAL"; ci "feature: mark experimental"
  write app.py "def main():" "    print('hello from feature')"; ci "feature: change greeting"
  write shared-new.txt "feature flavour"; ci "feature: add shared-new"
  git rm -q notes.txt; ci "feature: remove notes"

  git switch -q main
  write README.md "# Project" "" "Status: production"; ci "main: mark production"
  write app.py "def main():" "    print('hello from main')"; ci "main: change greeting"
  write shared-new.txt "main flavour"; ci "main: add shared-new"
  append notes.txt "extra note"; ci "main: extend notes"

  stamp; git merge --no-ff -m "Merge feature into main" feature || true   # leaves merge in progress
}

gen_rebase_conflict() {                         # 21-rebase-conflict
  new_repo 21-rebase-conflict
  write data.txt "shared base line"; ci "base"
  git switch -q -c topic
  # Five topic commits replay onto main; the first two add new files and apply
  # cleanly, the third edits the shared line and conflicts, so the rebase stops at
  # 3/5 — a multi-step rebase that exercises the progress indicator mid-way.
  commit "topic: add readme"  readme.txt  "topic readme"
  commit "topic: add helper"  helper.txt  "topic helper"
  write data.txt "topic version of the line"; ci "topic: edit data"
  commit "topic: add widget"  widget.txt  "topic widget"
  commit "topic: add tests"   tests.txt   "topic tests"
  git switch -q main
  write data.txt "main version of the line"; ci "main: edit data"
  git switch -q topic
  stamp; git rebase main || true                # stops at 3/5 on the data.txt conflict
}

gen_cherry_pick_conflict() {                    # 22-cherry-pick-conflict
  new_repo 22-cherry-pick-conflict
  write x.txt "original"; ci "base"
  git switch -q -c side
  write x.txt "side edit"; ci "side: change x"
  git switch -q main
  write x.txt "main edit"; ci "main: change x"
  stamp; git cherry-pick "$(git rev-parse side)" || true
}

gen_revert_conflict() {                         # 23-revert-conflict
  new_repo 23-revert-conflict
  write y.txt "first"; ci "base"
  write y.txt "second"; ci "change to second"   # the commit we'll try to revert
  write y.txt "third"; ci "change to third"
  stamp; git revert --no-edit "$(git rev-parse HEAD~1)" || true
}

gen_rebase_edit_stop() {                        # 24-rebase-edit-stop
  new_repo 24-rebase-edit-stop
  commit "c1" a.txt one; commit "c2" a.txt two
  commit "c3" a.txt three; commit "c4" a.txt four
  stamp
  GIT_SEQUENCE_EDITOR='sed -i.bak "2s/^pick/edit/"' git rebase -i HEAD~3 || true
}

gen_bisect() {                                  # 25-bisect
  new_repo 25-bisect
  local i
  for i in $(seq 1 12); do commit "commit $i" code.txt "line $i"; done
  git bisect start            >/dev/null
  git bisect bad  HEAD        >/dev/null
  git bisect good "$(git rev-parse HEAD~10)" >/dev/null
}

gen_merge_ready() {                             # 26-merge-ready
  new_repo 26-merge-ready
  commit "base" base.txt root
  git switch -q -c feature
  commit "feature: add module" feature.txt f1
  commit "feature: more work" feature.txt f2
  git switch -q main
  commit "main: docs" docs.txt d1            # clean merge of feature is available
}

gen_rebase_ready() {                            # 27-rebase-ready
  new_repo 27-rebase-ready
  commit "base" base.txt root
  git switch -q -c topic
  commit "topic: a" topic.txt a
  commit "topic: b" topic.txt b
  git switch -q main
  commit "main: c" mainline.txt c
  git switch -q topic                         # clean rebase onto main is available
}

gen_many_branches() {                           # 30-many-branches
  new_repo 30-many-branches
  commit "base" main.txt root; commit "more" main.txt next
  local names=(feature/login feature/signup feature/oauth bugfix/crash-on-start \
    bugfix/memory-leak release/1.0 release/1.1 release/2.0 hotfix/security \
    users/alice/wip users/bob/experiment chore/deps chore/ci docs/readme spike/new-renderer)
  local n
  for n in "${names[@]}"; do git branch "$n"; done
  git switch -q feature/oauth; commit "oauth work" oauth.txt x
  git switch -q spike/new-renderer; commit "spike 1" spike.txt y; commit "spike 2" spike.txt z
  git branch "feature/a-really-extremely-long-branch-name-that-should-definitely-overflow-the-sidebar-and-test-truncation"
  git switch -q main
}

gen_ahead_behind() {                            # 31-ahead-behind
  new_repo 31-ahead-behind
  commit "c1" f.txt a; commit "c2" f.txt b; commit "c3" f.txt c
  add_remote main
  commit "origin-only 1" o.txt x; commit "origin-only 2" o.txt y
  git push -q origin main
  git reset -q --hard HEAD~2                   # rewind local back to c3
  commit "local-only 1" l.txt p; commit "local-only 2" l.txt q
  git fetch -q origin                          # main now diverged: 2 ahead, 2 behind
}

gen_fast_forward() {                            # 32-fast-forward
  new_repo 32-fast-forward
  commit "c1" f.txt a; commit "c2" f.txt b; commit "c3" f.txt c
  add_remote main
  commit "ahead 1" f.txt d; commit "ahead 2" f.txt e
  git push -q origin main
  git reset -q --hard HEAD~2                   # local 2 behind, 0 ahead -> FF available
  git fetch -q origin
}

gen_many_tags() {                               # 33-many-tags
  new_repo 33-many-tags
  local i
  for i in $(seq 1 14); do commit "commit $i" log.txt "entry $i"; done
  git tag v0.1.0 HEAD~13
  git tag -a v0.2.0 -m "Release 0.2.0" HEAD~11
  git tag v0.3.0 HEAD~9
  git tag -a v1.0.0 -m "First stable release.

Lots of notes in this annotated tag to exercise the tag detail view." HEAD~7
  git tag v1.0.1 HEAD~6
  git tag -a v1.1.0 -m "Minor release" HEAD~4
  git tag v1.1.1 HEAD~3
  git tag -a v2.0.0 -m "Major release" HEAD~1
  git tag latest HEAD
  git tag release/2024-06 HEAD~2
  git tag -a nightly/build-42 -m "nightly" HEAD~5
}

gen_remote_gone() {                             # 34-remote-gone
  new_repo 34-remote-gone
  commit "c1" main.txt a
  add_remote main
  git switch -q -c stale-feature
  commit "feature work" feat.txt x
  git push -q -u origin stale-feature
  git push -q origin --delete stale-feature
  git fetch -q --prune origin                  # upstream now [gone] while config remains
}

gen_unmerged() {                               # 35-unmerged
  new_repo 35-unmerged
  commit "base" main.txt root
  local b
  for b in alpha beta gamma delta; do
    git switch -q -c "feature/$b" main
    commit "feature/$b: work 1" "$b.txt" 1
    commit "feature/$b: work 2" "$b.txt" 2
  done
  git switch -q main
  commit "main moves on" main.txt next
}

gen_stashes() {                                 # 40-stashes
  new_repo 40-stashes
  commit "base" a.txt one; commit "more" a.txt two
  append a.txt "wip refactor"; git stash push -q -m "wip: refactor pass"
  write scratch.txt "scratch"; append a.txt "edit"; git stash push -q -u -m "wip: scratch + edit"
  append a.txt "staged"; git add a.txt; append a.txt "unstaged"
  git stash push -q -m "wip: mixed staged/unstaged"
}

gen_stash_conflict() {                          # 41-stash-conflict
  new_repo 41-stash-conflict
  write data.txt "line A" "line B" "line C"; ci "base"
  write data.txt "line A" "line B-stashed" "line C"
  git stash push -q -m "wip: edit line B"
  write data.txt "line A" "line B-branch" "line C"; ci "branch edits line B too"
}

gen_submodules() {                              # 50-submodules
  new_repo 50-submodules
  commit "Initial" README.md "main repo"
  local sub="$ROOT/.subs/widget-lib"; rm -rf "$sub"; mkdir -p "$sub"
  ( cd "$sub"; init_repo; printf 'widget\n' > lib.txt; git add -A; stamp; git commit -q -m "widget lib v1" )
  git -c protocol.file.allow=always submodule add -q "$sub" vendor/widget-lib
  stamp; git commit -q -m "Add widget-lib submodule"
  local sub2="$ROOT/.subs/theme-lib"; rm -rf "$sub2"; mkdir -p "$sub2"
  ( cd "$sub2"; init_repo; printf 'theme\n' > theme.txt; git add -A; stamp; git commit -q -m "theme lib v1" )
  git -c protocol.file.allow=always submodule add -q "$sub2" vendor/theme-lib
  stamp; git commit -q -m "Add theme-lib submodule"
  git submodule deinit -q -f vendor/theme-lib            # leave one uninitialised
}

gen_worktrees() {                               # 51-worktrees
  new_repo 51-worktrees
  commit "c1" main.txt one; commit "c2" main.txt two
  git branch feature-x; git branch feature-y
  git worktree add -q "$ROOT/51-worktrees.wt-feature-x" feature-x
  git worktree add -q "$ROOT/51-worktrees.wt-feature-y" feature-y
  printf 'wip in linked worktree\n' >> "$ROOT/51-worktrees.wt-feature-x/main.txt"
}

gen_submodule_detached() {                      # 52-submodule-detached
  # Four submodules, each parked in a distinct detached-HEAD state, to exercise the
  # detached-HEAD banner's two shapes (Switch-to-branch vs at-risk Create-branch) and
  # the reachable-but-not-a-tip case that shows nothing. Activate each submodule in
  # GitBench to see its banner.
  sub_upstream sd-on-tip   2
  sub_upstream sd-behind   3
  sub_upstream sd-stranded 2
  sub_upstream sd-pinned   3

  new_repo 52-submodule-detached
  commit "Initial" README.md "detached-submodule banner demo"
  git -c protocol.file.allow=always submodule add -q "$ROOT/.subs/sd-on-tip"   subs/on-tip
  git -c protocol.file.allow=always submodule add -q "$ROOT/.subs/sd-behind"   subs/behind
  git -c protocol.file.allow=always submodule add -q "$ROOT/.subs/sd-stranded" subs/stranded
  git -c protocol.file.allow=always submodule add -q "$ROOT/.subs/sd-pinned"   subs/pinned-old
  stamp; git commit -q -m "Add submodules"
  git config protocol.file.allow always

  # on a local branch tip                       -> banner: Switch to main
  git -C subs/on-tip checkout -q --detach main

  # local main behind, HEAD on origin/main tip  -> banner: Switch to main (fast-forwards)
  git -C subs/behind checkout -q --detach origin/main
  git -C subs/behind branch -f main HEAD~1

  # a commit made on the detached HEAD, on no branch -> banner: Create branch (at-risk)
  git -C subs/stranded checkout -q --detach main
  ( cd subs/stranded; printf 'orphan\n' >> lib.txt; git add -A; stamp; git commit -q -m "work with nowhere to go" )

  # pinned to an older commit (reachable, not a tip) -> no banner (control)
  git -C subs/pinned-old checkout -q --detach origin/main~1
  git -C subs/pinned-old branch -q -D main 2>/dev/null || true
}

gen_submodule_reattach() {                      # 53-submodule-reattach
  # A superproject one commit behind its origin, where the pending commit bumps a
  # submodule forward. Pulling detaches the submodule on origin/main's tip; GitBench
  # then auto-reattaches it onto its branch. Re-run this scenario to reset it.
  local lib="$ROOT/.subs/sr-lib"
  rm -rf "$lib"; mkdir -p "$lib"
  ( cd "$lib"; init_repo
    printf 'v1\n' > lib.txt; git add -A; stamp; git commit -q -m "lib v1"
    printf 'v2\n' > lib.txt; git add -A; stamp; git commit -q -m "lib v2" )

  new_repo 53-submodule-reattach
  commit "Initial" README.md "pull-reattach demo"
  git -c protocol.file.allow=always submodule add -q "$lib" vendor/lib
  stamp; git commit -q -m "Add vendor/lib (v2)"
  git config protocol.file.allow always
  add_remote main                                    # origin/main = this commit (C1)

  # advance lib to v3, bump the recorded pointer, and publish as C2
  ( cd "$lib"; printf 'v3\n' > lib.txt; git add -A; stamp; git commit -q -m "lib v3" )
  git -C vendor/lib -c protocol.file.allow=always fetch -q origin
  git -C vendor/lib checkout -q --detach origin/main
  git add vendor/lib; stamp; git commit -q -m "Bump vendor/lib to v3"
  git push -q origin main                            # origin/main = C2

  # rewind this working copy so it's one commit behind origin, submodule back on v2
  git reset -q --hard HEAD~1
  git -c protocol.file.allow=always submodule update -q
}

gen_long_paths() {                              # 60-long-paths
  new_repo 60-long-paths

  # Deeply nested, realistic monorepo directories — long enough that a file path
  # overflows the dialog width and has to ellipsize.
  local dirs=(
    "server/cloud-run/src/creatorEconomy/pendingSpendValuation"
    "server/cloud-run/src/creatorEconomy/reconciliation"
    "server/cloud-run/src/services/wallet/internal"
    "packages/frontend/src/components/dashboard/widgets/analytics"
    "packages/frontend/src/components/dashboard/widgets/notifications"
    "packages/shared/src/domain/entitlements/validation/rules"
    "infrastructure/terraform/modules/networking/load-balancers"
    "apps/mobile/lib/features/onboarding/presentation/viewmodels"
  )

  # Seed each directory with a few files, then commit the baseline.
  local d i leaf
  for d in "${dirs[@]}"; do
    mkdir -p "$d"; leaf="$(basename "$d")"
    for i in 1 2 3 4; do
      printf 'export const value%d = %d;\n' "$i" "$i" > "$d/${leaf}Service${i}.ts"
    done
  done
  ci "Seed deeply-nested service modules"

  # A large working-tree diff with long paths: mostly UNSTAGED so the Discard dialog
  # shows a long, scrolling list, plus a few staged so Stash/mixed views have variety.
  for d in "${dirs[@]}"; do
    leaf="$(basename "$d")"
    append "$d/${leaf}Service1.ts" "// unstaged modification on a very long path"
    append "$d/${leaf}Service2.ts" "// another unstaged modification"
    printf 'export const generated = true;\n' > "$d/${leaf}Controller.generated.ts"
  done

  # Deletions and a rename, all on long paths, for status-badge variety (D / R).
  rm "server/cloud-run/src/services/wallet/internal/internalService4.ts"
  rm "packages/frontend/src/components/dashboard/widgets/notifications/notificationsService3.ts"
  git mv "packages/shared/src/domain/entitlements/validation/rules/rulesService4.ts" \
         "packages/shared/src/domain/entitlements/validation/rules/rulesServiceRenamedToAMuchLongerFileName.ts"

  # Stage roughly a third of the changes (two whole directories).
  git add "server/cloud-run/src/creatorEconomy/pendingSpendValuation" \
          "packages/frontend/src/components/dashboard/widgets/analytics"
}

gen_huge_changeset() {                          # 61-huge-changeset
  new_repo 61-huge-changeset
  # Enough changed paths that the combined pathspec (~1.4 MB) blows past every
  # process-spawn limit: Windows caps the whole CreateProcess command line at
  # 32,767 chars ("The filename or extension is too long") and POSIX exec caps
  # argv+env at ARG_MAX (~1 MB on macOS, E2BIG "Argument list too long").
  # Stage All / Unstage All / Discard All must route the path list around the
  # command line (stdin pathspec or chunking) to survive this repo.
  local n=6000 pad="abcdefghijklmnopqrstuvwxyz-0123456789-abcdefghijklmnopqrstuvwxyz"
  local i d
  printf '    generating %s tracked + %s untracked files (slow)...\n' "$n" "$n"
  for ((i = 0; i < n; i++)); do
    d="src/area-$((i % 40))/module-$((i % 400))"
    mkdir -p "$d"
    printf 'original %d\n' "$i" > "$d/tracked-$(printf '%05d' "$i")-$pad.ts"
  done
  ci "Seed $n tracked files"
  for ((i = 0; i < n; i++)); do
    d="src/area-$((i % 40))/module-$((i % 400))"
    printf 'modified %d\n' "$i" >> "$d/tracked-$(printf '%05d' "$i")-$pad.ts"
    printf 'untracked %d\n' "$i" > "$d/untracked-$(printf '%05d' "$i")-$pad.ts"
  done
}

# Three sibling repos wired the way the cross-repo change-set feature
# (docs/plans/cross-repo-change-sets.md) expects to find them, in one folder
# meant to be added to GitBench as a single group:
#   - feature/cross-repo in ALL THREE repos            -> the change set
#   - bugfix/shared-logging in service-b + service-c   -> a second, smaller set
#   - feature/only-in-a in service-a alone             -> decoy, never a set
#   - defaults are main/main/MASTER and must never read as a set
#     (exclusion is by each repo's default branch, not the literal name "main")
# plus per-repo variety the plan's phases test against: overlapping file paths
# across repos (src/index.ts), differing base resolution (published upstream vs
# default-branch fallback), and parked drift states (mismatched checkout, an
# unpushed commit, a moved base, a dirty tree, a member with no remote).
gen_change_set() {                              # 70-change-set
  rm -rf "$ROOT/70-change-set"

  # service-a — set branch CHECKED OUT, published with an upstream, then one
  # more local commit so it sits 1 ahead of origin (push-all / drift: unpushed).
  new_repo 70-change-set/service-a
  mkdir -p src api
  commit "a: initial"    src/index.ts   "export const a = 1;"
  commit "a: api schema" api/schema.txt "v1"
  git switch -q -c feature/cross-repo
  commit "cross-repo: extend schema" api/schema.txt "v2 shared-field"
  add_remote main feature/cross-repo
  commit "cross-repo: adopt shared field" src/index.ts "export const shared = true;"
  git branch feature/only-in-a                 # decoy: exists in this repo only

  # service-b — has the set branch but main is checked out (drift: mismatched
  # checkout). The branch was never published, so base resolution falls back to
  # the default branch — whose tip then moves on (drift: behind its base).
  # src/index.ts collides with service-a's path (repo-qualified tree case).
  new_repo 70-change-set/service-b
  mkdir -p src
  commit "b: initial" src/index.ts "export const b = 1;"
  commit "b: logging" src/log.txt  "log v1"
  git switch -q -c feature/cross-repo
  commit "cross-repo: consume shared field" src/index.ts "import { shared } from 'service-a';"
  commit "cross-repo: wire logging"         src/log.txt  "log shared-field"
  git switch -q -c bugfix/shared-logging main
  commit "shared-logging: fix double write" src/log.txt "log fix"
  git switch -q main
  add_remote main
  commit "b: main moves on" src/log.txt "log v2"

  # service-c — default branch named MASTER, no remote (push-all reports this
  # member's failure honestly), set branch checked out with a DIRTY tree.
  new_repo 70-change-set/service-c
  git symbolic-ref HEAD refs/heads/master      # default branch named master
  commit "c: initial" config.toml "name = \"service-c\""
  git switch -q -c bugfix/shared-logging
  commit "shared-logging: propagate fix" config.toml "log_fix = true"
  git switch -q -c feature/cross-repo master
  commit "cross-repo: read shared field" config.toml "shared_field = true"
  append config.toml "wip = true"              # uncommitted edit on the set branch
}

gen_big_history() {                             # 90-big-history
  new_repo 90-big-history
  local count="${COUNT_BIG:-800}" i
  printf '    generating %s commits (slow)...\n' "$count"
  for i in $(seq 1 "$count"); do
    printf 'entry %d\n' "$i" >> history.log
    git add history.log; stamp; git commit -q -m "Commit number $i"
    if [ $((i % 100)) -eq 0 ]; then git tag "v0.$((i / 100)).0"; fi
  done
}

gen_wide_graph() {                              # 91-wide-graph
  new_repo 91-wide-graph
  commit "root" main.txt r
  local branches=(svc-auth svc-api svc-web svc-db svc-cache svc-jobs) b round
  for b in "${branches[@]}"; do git branch "$b"; done
  for round in 1 2 3; do
    for b in "${branches[@]}"; do git switch -q "$b"; commit "$b: round $round" "$b.txt" "$round"; done
    git switch -q main; commit "main: sync $round" main.txt "$round"
    merge "Merge ${branches[0]} (round $round)" "${branches[0]}" || true
    merge "Merge ${branches[1]} (round $round)" "${branches[1]}" || true
  done
  git switch -q main
}

# ---------------------------------------------------------------------------
# registry: folder | function | description
# ---------------------------------------------------------------------------
REGISTRY=(
  "00-empty|gen_empty|Unborn HEAD: no commits, one staged + one untracked file"
  "01-single-commit|gen_single|Repo with a single commit"
  "02-linear|gen_linear|Simple clean linear history (10 commits)"
  "03-detached-head|gen_detached|Detached HEAD checked out two commits back"
  "04-long-messages|gen_long_messages|Very long subject + multi-paragraph bodies"
  "10-mixed-changes|gen_mixed_changes|Staged + unstaged + untracked + partially-staged"
  "11-staged-only|gen_staged_only|Everything staged, clean worktree otherwise"
  "12-renames|gen_renames|Staged rename, rename+modify, and an unstaged rename"
  "13-deletions|gen_deletions|Staged delete + unstaged delete"
  "14-mode-change|gen_mode_change|Executable-bit changes both directions"
  "15-many-changes|gen_many_files|150 modified, 30 added, 10 deleted, ~half staged"
  "16-binary-large|gen_binary_large|Modified binary blob + 5000-line text file"
  "17-unicode-emoji|gen_unicode|Unicode/emoji filenames, content, and commit messages"
  "18-line-endings|gen_line_endings|CRLF->LF and whitespace-only changes"
  "19-gitignored|gen_gitignore|Ignored files, untracked, and a tracked-but-ignored file"
  "1a-intra-line|gen_intra_line|Intra-line emphasis: replace blocks, gate, tab/ws, unbalanced (commit+staged+unstaged)"
  "20-merge-conflict|gen_merge_conflict|MERGE in progress: content + add/add + modify/delete"
  "21-rebase-conflict|gen_rebase_conflict|REBASE stopped mid-conflict"
  "22-cherry-pick-conflict|gen_cherry_pick_conflict|CHERRY-PICK stopped mid-conflict"
  "23-revert-conflict|gen_revert_conflict|REVERT stopped mid-conflict"
  "24-rebase-edit-stop|gen_rebase_edit_stop|Interactive rebase stopped at 'edit'"
  "25-bisect|gen_bisect|Bisect in progress (detached at midpoint)"
  "26-merge-ready|gen_merge_ready|Two branches that merge cleanly (happy path)"
  "27-rebase-ready|gen_rebase_ready|A branch that rebases cleanly onto main"
  "30-many-branches|gen_many_branches|~17 branches incl. folders + a very long name"
  "31-ahead-behind|gen_ahead_behind|main diverged from origin (2 ahead, 2 behind)"
  "32-fast-forward|gen_fast_forward|Local behind origin with no local commits (FF)"
  "33-many-tags|gen_many_tags|Lightweight + annotated tags across history"
  "34-remote-gone|gen_remote_gone|Branch whose upstream was deleted ([gone])"
  "35-unmerged|gen_unmerged|Several parallel unmerged feature branches"
  "40-stashes|gen_stashes|Three stashes incl. untracked and staged content"
  "41-stash-conflict|gen_stash_conflict|A stash that conflicts when popped"
  "50-submodules|gen_submodules|One initialised + one uninitialised submodule"
  "51-worktrees|gen_worktrees|Two linked worktrees, one with a local edit"
  "52-submodule-detached|gen_submodule_detached|Submodules parked in each detached-HEAD banner state"
  "53-submodule-reattach|gen_submodule_reattach|Behind superproject; pulling auto-reattaches a submodule"
  "60-long-paths|gen_long_paths|Many long deeply-nested paths (M/A/D/R), mostly unstaged"
  "61-huge-changeset|gen_huge_changeset|6000 modified + 6000 untracked; pathspec > CreateProcess/ARG_MAX caps"
  "70-change-set|gen_change_set|Three repos linked by same-named branches (change set + decoys)"
  "90-big-history|gen_big_history|Large history for perf (COUNT_BIG, default 800)"
  "91-wide-graph|gen_wide_graph|Many concurrent lanes with periodic merges"
)

if [ "$LIST_ONLY" -eq 1 ]; then
  printf '%-24s %s\n' "SCENARIO" "DESCRIPTION"
  for entry in "${REGISTRY[@]}"; do
    IFS='|' read -r folder _fn desc <<<"$entry"
    printf '%-24s %s\n' "$folder" "$desc"
  done
  exit 0
fi

wanted() { # wanted <folder> -> 0 if it should run
  [ "${#SELECT[@]}" -eq 0 ] && return 0
  local s; for s in "${SELECT[@]}"; do [ "$s" = "$1" ] && return 0; done
  return 1
}

mkdir -p "$ROOT"
echo "Generating GitBench test repos in: $ROOT"
echo

CREATED=()
for entry in "${REGISTRY[@]}"; do
  IFS='|' read -r folder fn desc <<<"$entry"
  wanted "$folder" || continue
  printf '  %-24s %s\n' "$folder" "$desc"
  if ( set -e; "$fn" ); then
    CREATED+=("$folder")
  else
    printf '    !! %s FAILED\n' "$folder" >&2
  fi
done

# One summary line per repo. A scenario is usually its own repo, but a
# repo-family scenario (70-change-set) is a plain folder of sibling repos —
# list each nested repo instead of poking git at the folder itself.
summary_line() { # summary_line <label> <repo-dir>
  local head_line
  head_line=$(git -C "$2" status -sb 2>/dev/null | head -1) || true
  printf '  %-24s %s\n' "$1" "$head_line"
}

# Index file at the root.
{
  echo "GitBench test repos"
  echo "==================="
  echo
  echo "Generated by scripts/make-test-repos.sh"
  echo "Add this folder (or individual subfolders) to GitBench."
  echo "(.remotes and .subs hold bare/helper repos and aren't meant to be opened.)"
  echo
  for entry in "${REGISTRY[@]}"; do
    IFS='|' read -r folder _fn desc <<<"$entry"
    printf '%-24s %s\n' "$folder" "$desc"
  done
} > "$ROOT/README.txt"

echo
echo "Summary:"
for folder in "${CREATED[@]}"; do
  dir="$ROOT/$folder"
  if [ -e "$dir/.git" ]; then
    summary_line "$folder" "$dir"
  else
    for sub in "$dir"/*/; do
      [ -e "$sub.git" ] || continue
      summary_line "$folder/$(basename "$sub")" "$sub"
    done
  fi
done
echo
echo "Done. ${#CREATED[@]} repos created under $ROOT"
