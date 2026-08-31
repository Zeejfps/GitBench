# Lua plugins — extending DiffDino from the outside

> **Framing:** the goal is not "an escape hatch bolted on the side". It is a *contribution seam* that
> the app itself goes through, proven by moving real shipped features onto it. If four built-in
> features can be deleted from the C# and re-appear as bundled Lua plugins with no user-visible
> change — same strings, same icons, same seven locales — then the seam is real. If they can't, the
> seam is a demo and third-party plugins would have hit the same wall on day one.
>
> The constraint that shapes everything: **we publish Native AOT**. That rules out the reflection-based
> embedding everyone reaches for first, and it means the plugin API is a hand-written marshalling
> boundary rather than a generated one. Which is fine — a hand-written boundary is a *parsed* boundary,
> and `docs/coding_rules.md` Rule 1 wanted that anyway.
>
> **v1 is declarative contributions only.** Plugins describe menu items, toolbar buttons, commands and
> event handlers as data. They do not build widget trees. See [Why not widget trees](#why-not-widget-trees).

## Decisions

| Area | Decision |
|---|---|
| Runtime | **Lua 5.4 via KeraLua**, statically linked into the AOT image. Not MoonSharp: its interop layer resolves members by reflection, which is exactly what AOT trimming removes, so every exposed type would need a hand-written descriptor — the same work as a C binding, minus the ecosystem and the speed. Precedent for a native dependency exists (`TextMateSharp`'s onigwrap, noted NativeAOT-clean in `GitBench.csproj`), and Lua's ~200KB static build is friendlier than that one. Tracks the same appetite as `docs/plans/glfw-static-linking.md`. |
| What a plugin can extend | v1: **context menu items, toolbar buttons, named commands with keybindings, repo event handlers, and its own localized strings.** Nothing else. |
| Contribution model | Plugins return **data**, the host builds widgets. A contribution crosses the FFI boundary once, is **parsed into a sum type**, and the raw Lua table is never seen downstream (Rule 1: parse at every boundary). |
| Menu item type | `RepoBarContextMenu.Item` is today an optional-field bag — 9 fields, of which `IsSeparator: true` makes seven meaningless. It moves to `Controls/MenuItem.cs` and becomes a sum type (`Action | Submenu | Separator`) *before* plugins can construct one. A plugin author is exactly the caller who will write the nonsense combination. |
| Registry | One `IPluginHost`, registered in `AppServices` and **injected** where it is consumed — not a static, not a service locator. Contributions are enumerable and every one is attributable to a plugin id. |
| Threading | Menu builders and `enabled`/`visible` predicates run **synchronously on the UI thread** under a deadline; they may not call git. `on_select` bodies are dispatched like any other command and may go async. One `lua_State` per plugin, all UI-thread in v1. |
| Localization | Plugins ship `strings/<locale>.json` in the app's flat dotted-key format. Lookup is **runtime** (`plugin.t("key")`), because `Strings` is source-generated from `Localization/Strings/*.json` and closed to outsiders. Fallback chain: requested locale → `en` → the key itself. |
| Trust | **Bundled plugins are trusted. User-installed plugins are not.** Untrusted plugins load without `io`, `os.execute`, `package.loadlib` or `require` of C modules; filesystem and process access go through host APIs that prompt, reusing the existing `ToolApproval` machinery from the assistant. |
| Errors | Every callback is wrapped. An error becomes a toast naming the plugin plus a log line; three errors from one plugin disables it for the session. A plugin never takes down a menu, a frame, or the app. |
| Distribution | Folder-based. Bundled: `<app>/plugins/<id>/`. User: `<AppData>/plugins/<id>/` via `AppPaths`. No registry, no marketplace, no auto-update in v1. |
| Out of scope for v1 | Widget trees, custom panels, custom themes from plugins, diff transformers, assistant tools written in Lua, forge/CI providers, plugin-to-plugin dependencies, hot reload, a plugin marketplace. Each is a later phase, and none of them changes the v1 seam. |

## Modules

Six pieces. The first two are the only ones that touch Lua; everything else is ordinary app code.

**1. `GitBench.Plugins` — discovery, lifetime, containment.**
Enumerates plugin folders, parses `plugin.json` manifests, owns the load order, and holds the
contribution registry. Knows nothing about Lua: it talks to an `IPluginRuntime` seam. That seam is
what makes the whole thing testable — the test suite registers contributions from a fake runtime
and never boots an interpreter.

**2. `GitBench.Plugins.Lua` — the binding layer.**
KeraLua P/Invoke, the `gitbench` global table, and the marshalling in both directions. This is the
only module allowed to hold a `lua_State`, and the only place in the codebase where a Rule 3 level-1
justification comment is expected on every function — an FFI call is a checker bypass by
construction.

**3. The contribution model.**
`MenuContribution`, `ToolbarContribution`, `CommandContribution` — records built *only* by the
parser, so an ill-formed manifest cannot become a contribution. Plus `MenuAnchor`, a closed enum:
plugins bind to anchors we named, not to strings they invent.

**4. Menu and toolbar integration.**
The anchor call sites (below) and the toolbar's plugin section. Mechanical work — one line per
anchor — but it is where the `ctx` schemas get fixed, and those are the part we are stuck with.

**5. `PluginStrings` — per-plugin localization.**
Loads `strings/<locale>.json`, resolves against the app's current `Locale`, re-resolves when the
user switches language. Same file format as `Localization/Strings/*.json`, so the existing
translation workflow transfers unchanged.

**6. The bundled plugins.**
Four of them, each a converted built-in. These are the acceptance test for modules 1–5.

## Anchor points

An anchor is a named place a plugin may contribute menu items, plus the shape of the `ctx` table its
builder receives. The `ctx` is a **flat snapshot**, not a live handle: it is built once when the menu
opens and is inert afterward. A plugin that wants fresh data asks for it in `on_select`.

| Anchor | Call site | `ctx` fields |
|---|---|---|
| `repo.node` | `Features/Repos/RepoNodeViewModel.cs:222` | `path`, `name`, `branch`, `is_detached`, `has_upstream`, `ahead`, `behind`, `is_dirty` |
| `commit.row` | `Features/Commits/CommitsView.cs:1079` (`BuildCommitMenuItems`) | `sha`, `short_sha`, `subject`, `author`, `date`, `refs[]` (`{name, kind}`), `parents[]`, `is_merge` |
| `branch.local` / `branch.remote` | `Features/Branches/BranchRowState.cs:59` | `name`, `full_ref`, `upstream`, `ahead`, `behind`, `is_head` |
| `localchanges.file` | `Features/LocalChanges/FileOpsContextMenu.cs` | `files[]` (`{path, absolute_path, status, staged}`) — always a list, selection is multi |
| `file_browser.row` | `Features/FileBrowser/FileBrowserContextMenu.cs` | `path`, `absolute_path`, `is_directory` |
| `toolbar.actions` | `Features/Toolbar/ActionsToolbar.cs:55` (`BuildActions`) | repo snapshot, same fields as `repo.node` |

`stash.row`, `worktree.row`, `submodule.row` and `tag` follow the same pattern and are deferred to
phase 4 — not because they are hard, but because four anchors is enough to find out whether the `ctx`
design holds, and every one we ship is one we cannot casually change.

**Ordering.** Plugin items land in a section below a separator, after the built-ins, in plugin load
order (manifest id, alphabetical). A contribution may request `position = "top"` to sit above the
built-ins; nothing finer. Menus are muscle memory, and letting plugins interleave arbitrarily is how
that gets destroyed.

## The Lua surface

```lua
-- plugins/open-remote/init.lua
local p = require("gitbench")

p.menu("repo.node", function(ctx)
  local url = p.git.remote_web_url(ctx.path, "origin")
  if not url then return {} end
  return {
    { label = p.t("menu.open_remote"), icon = "ExternalLink",
      on_select = function() p.shell.open_url(url) end },
  }
end)
```

Item fields: `label`, `icon` (a name from `Controls/LucideIcons.cs`; an unknown name renders a
default glyph rather than throwing), `on_select`, `enabled`, `checked`, `shortcut`, `submenu`,
`separator = true`. That is deliberately the same vocabulary the C# `Item` already has — the
conversion of a built-in should be a transliteration, not a redesign.

Host tables in v1: `p.t`, `p.git` (reads only), `p.shell`, `p.clipboard`, `p.ui` (toast, confirm,
prompt), `p.on` (repo events off `IMessageBus`), `p.log`.

## Localization

Plugins ship the same flat dotted-key JSON the app uses:

```
plugins/open-remote/strings/en.json     { "menu.open_remote": "Open on remote" }
plugins/open-remote/strings/ja.json     { "menu.open_remote": "リモートで開く" }
```

Keys are namespaced by plugin at load, so two plugins can both use `menu.open`. Lookup is runtime
rather than generated, which costs a dictionary hit per label and buys the thing that matters: a
plugin author adds a locale by dropping in a file, with no build step and no access to our source
generator.

**The bundled plugins are why this gets tested properly.** Converting them means *moving* roughly
fifteen already-translated keys out of seven `Localization/Strings/*.json` files and into the
plugins' own `strings/` folders. So the plugin localization path ships covering seven real locales
including RTL Arabic, rather than an English-only stub that quietly breaks the first time someone
translates a plugin. The migration is mechanical but must be exact — a key moved with its
translations intact, not re-keyed and re-translated.

## Dogfooding: four built-ins become bundled plugins

Chosen so that each one proves a different part of the seam, and so that together they cover every
v1 capability. Ordered by what they prove, which is also the order to build them.

**1. `shell-tools` — the shell escapes.**
Takes over the two toolbar icon buttons (`ActionsToolbar.cs:94,100` — Open Folder, Open Terminal) and
the "Open in terminal" / "Reveal" / "Open in default app" items on both `file_browser.row` and
`localchanges.file`. Proves: toolbar contributions, one plugin contributing at three anchors,
platform-varying labels (Finder / Explorer / file manager), and the host shell API.

Chosen first because these are the only built-in toolbar buttons that are *not* core git verbs.
Everything else on that toolbar is fetch/pull/push/branch/stash — those stay in C# and should.

**2. `open-remote` — the remote link.**
Takes over `RepoNodeViewModel.AddOpenRemoteItem` (`:327`) and the `RemoteWebUrl` derivation behind
it. Proves: git reads from Lua, and conditional contribution (no item at all for a local-path
remote). Also the most valuable one to have as a plugin: forge-specific URL shapes — PR links,
compare links, blame permalinks for Azure DevOps or a self-hosted GitLab — are exactly what we do not
want to accumulate in the binary. This conversion is the on-ramp to forge providers in phase 5.

**3. `copy-paths` — the clipboard items.**
Takes over the three copy items in `FileOpsContextMenu.AddCopyItems` and their file-browser
counterparts. Proves: multi-select `ctx` (`files[]` with N > 1), and **pluralized strings** through
the plugin string table — `s.FilesStage(count)` style parameterization has to survive the move to
runtime lookup, and if it doesn't, better to find out on a copy item than on a third-party plugin.

**4. `tag-actions` — dynamic submenus and a write.**
Takes over `AddTagMenuItems` (`CommitsView.cs:1105`): one submenu per tag on the commit, each with
"Delete Tag…". Proves: submenus built from `ctx` data rather than declared statically, and a
**destructive action** routed through the confirmation dialog. This is the one that tests the trust
boundary, since a write from Lua is the case the permission model exists for.

**Rules for a conversion.** It is a regression if any of these slip:
- Strings are *moved*, never rewritten. All seven locales come along.
- Keyboard shortcuts and icons are identical.
- The existing tests covering the feature keep passing, adjusted only for where the item comes from.
- The item appears in its original position, not shunted into a plugin section — bundled plugins may
  target an exact index; third-party ones may not.
- Bundled plugins are enabled by default and **can be disabled**. "Open on remote" disappearing when
  a user turns off `open-remote` is the feature working, not a regression.

## Why not widget trees

The tempting next step is letting Lua build UI directly. It should be resisted in v1, for a reason
specific to this codebase: ZGF widgets are records built through a source generator, wired to
`Prop<T>`/`IReadable<T>` observables and rebuilt as state changes. Bridging that to Lua means
marshalling across FFI on every rebuild — the exact design that makes plugin systems slow and makes
host crashes look like plugin bugs.

The middle tier, when we want it (phase 6), is a **constrained panel schema**: the plugin returns a
tree from a small closed vocabulary — `vstack`, `hstack`, `label`, `button`, `list`, `spinner` — which
we render with our own widgets, refreshed when the plugin says so rather than per frame. That is
enough for a PR list or a CI status pane without exposing the layout engine. It is a strictly larger
version of the same parse-at-the-boundary design as menus, so nothing in v1 blocks it.

## Tension with `docs/coding_rules.md`

Worth naming rather than discovering in review. Rule 2 says: no implicit registries, no ambient
pub/sub where the handler set isn't statically knowable. A plugin system is, on its face, both.

The resolution is that the handler set is knowable — just at load time rather than compile time —
and we make that literal:

- `IPluginHost` is **injected**, never reached for. If a call site needs plugin contributions it says
  so in its constructor, like every other dependency.
- The registry is **enumerable and attributable**: at any moment we can list every contribution, its
  anchor, and the plugin that owns it. A "Plugins" settings pane that shows exactly this is a v1
  deliverable, not a nicety — it is what keeps the registry from being opaque.
- Anchors are a **closed enum**, so the set of extension points is a compile-time fact and a plugin
  binding to a name we don't have fails at load with a clear error.
- Event handlers registered through `p.on` subscribe to `IMessageBus` **through the host**, which
  holds the subscriptions and drops them when the plugin unloads. No plugin gets a raw bus reference.

What remains genuinely at odds with Rule 2 is that a menu's contents are no longer readable from one
file. That is the actual cost of the feature and it is accepted knowingly, bounded by the settings
pane above.

## Phases

1. **Host skeleton.** `IPluginHost`, manifest parse, discovery, contribution registry, error
   containment, settings pane. No Lua at all — a hardcoded fake runtime. The registry and the
   parse boundary get their tests here.
2. **Lua runtime.** KeraLua, static link, AOT publish verified on all three platforms *before*
   anything else depends on it. This phase is a spike that can fail; if static linking fights back,
   we find out before building on top of it.
3. **Menus + `shell-tools`.** Two anchors, the toolbar section, and the first conversion.
4. **Remaining anchors + the other three conversions.** By the end, the four bundled plugins own
   their features and the C# is deleted.
5. **Commands and keybindings**, then forge providers built on `open-remote`.
6. **Panel schema**, assistant tools in Lua, diff transformers.

Phase 2 is the risk. Everything else is ordinary work.

## Risks

- **Static-linking Lua under AOT on three platforms** is the one unknown. Mitigated by making it
  phase 2, standalone, and abandonable — a dynamic per-RID native, like onigwrap, is the fallback.
- **Startup cost.** Converting built-ins means Lua must be loaded before the first menu opens. Budget
  it explicitly: plugin load is measured at startup and a slow plugin is named in the log. A Git
  client that got slower to launch in exchange for a plugin system is a bad trade — Native AOT was
  chosen for launch speed in the first place.
- **`ctx` schemas are permanent.** Once a third-party plugin reads `ctx.sha`, we own that name. Hence
  four anchors in v1, not ten.
- **Menu latency.** A builder that blocks is a menu that stutters. Deadline them, log the offender,
  and drop a plugin that repeatedly overruns.
- **Trust.** A plugin is code on the user's machine with access to their repositories. The bundled/
  user split plus the `ToolApproval` reuse is the v1 answer; it is not a sandbox and should never be
  described as one.
