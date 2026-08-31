---
name: plugin-seam-facts-gitbench
description: Load-bearing facts about the app-side surfaces any GitBench extensibility/plugin seam must sit on — menu/toolbar reactivity, repo identity, shell intent vs mechanism, the localization source generator, and what the "deletion test" can actually prove
metadata:
  type: project
---

Established 2026-08-31 reviewing `docs/plans/lua-plugins.md` (branch `claude/lua-scripting-plugins-2l6hk0`).
These are facts about the app, not opinions about the plan; they decide any future contribution seam.

**Repo identity is `Guid Repo.Id`, never a path.** Every message in `GitBench/Messages/*.cs` is
`(Guid RepoId, ...)`; every git capability takes a `Repo` (`IGitRemoteOperations.GetRemoteUrl(Repo, string)`).
A seam that names repos by path string buys a partial lookup in the adapter and freezes a bad
identifier. Repo path is display data; identity is the id.

**Menu/toolbar contributions cannot be inert snapshots.** `ToolbarButton.Label` / `ToolbarIconButton.Tooltip`
are `Prop<string?>` bound via `L.T(s => ...)` so labels follow a locale switch *without* a rebuild, and
`ActionsToolbarViewModel.OpenFolder/OpenTerminal` are `Command`s gated on a reactive
`repoActionsEnabled` slice (`ActionsToolbarViewModel.cs:84`). A contract project that "depends on
nothing" therefore cannot carry `string Label` / `bool Enabled` without losing locale-follow and
disable-when-no-repo. The minimum shape that works is thunks (`Func<string>`, `Func<bool>`) the app
wraps in its own binding. Context menus are exempt (built on open); the toolbar is not.

**"Open in terminal" already means two different things.** `FileBrowserContextMenu.OpenTerminalAt`
prefers the *embedded* pane — sends `cd <dir>\r` into `ITerminalSessionStore` and flips
`State<MainViewMode>` — while `LocalChangesViewModel.OpenInTerminal:807` calls `_shell.OpenTerminal`.
Any port exposing "open a terminal" must be intent-shaped and own that choice, or one of the two
anchors regresses.

**`IPlatformShell.OpenFile` is a code-execution gesture on Windows** (UseShellExecute), and
`FileBrowserContextMenu`'s remarks say the defense is deliberately at the caller, not in the shell.
Exposing it to untrusted callers moves the defense to a prompt whose only content is a path.

**Localized strings are source-generated from `GitBench/Localization/Strings/*.json`.** Moving a key
out deletes the generated member, so surviving C# references are a *build* break (good). But keys
are shared across features — `common.open_folder` has three call sites, all in `RepoNodeViewModel`
(:253, :376, :400) — so "move the string with the feature" is impossible for shared keys; they must
be duplicated, with drift.

**Destructive actions are bespoke dialogs, not generic confirms.** `CommitsViewModel.RequestDeleteTag:373`
opens `DeleteTagDialog` with a "delete from remote repositories" checkbox, a Destructive-role button,
`ConfirmKeys`, and five dedicated strings. A `ui.confirm(text)` port cannot reproduce it — a
write-capable extension seam needs a closed sum of *app intents* the app renders itself, not a
generic confirm plus a git-exec.

**`ToolApproval` is not a reusable prompt surface.** `IToolApprovalGate`/`PendingToolApproval` are
internal to `Features/Assistant`; `ToolApprovalQueue` posts into the assistant *transcript*
(`TranscriptRow.cs:34` → `ToolApprovalCard`). "Reuse the ToolApproval machinery" means either prompts
appearing inside the assistant conversation or a real extraction.

**`MessageBus` is a bare `Dictionary<Type, List<Delegate>>` with no locking**, handlers run on the
broadcasting thread. Any event port handed to a foreign runtime must marshal through `IUiDispatcher`
itself; the bus will not.

**A "delete the plugin system" property must be a build configuration, not a manual exercise.** The
version that survives is *"the app builds and the non-plugin suite passes with the Null bindings
bound"* — machine-checkable every CI run. The version that reads better ("behaves exactly as today")
expires the moment a bundled plugin owns a shipped feature, and conflicts with "bundled plugins can
be disabled".

Related: [[seam-conventions-gitbench]], [[recurring-seam-mistakes-gitbench]]
