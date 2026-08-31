# Lua plugins — extending DiffDino from the outside

> **Framing:** the goal is not "an escape hatch bolted on the side". It is a *contribution seam* the
> app itself goes through, proven by moving real shipped features onto it.
>
> Two constraints shape everything. **We publish Native AOT**, four RIDs on four runners — so the
> plugin API is a hand-written marshalling boundary, which is to say a *parsed* one. And **the
> surfaces plugins land on are reactive while the actions they trigger are bespoke** — so a
> contribution cannot be an inert snapshot, and a port cannot be a thin wrapper over a git verb.
>
> **v1 is declarative contributions only.** Plugins describe menu items, toolbar buttons, commands and
> event handlers as data. They do not build widget trees. See [Why not widget trees](#why-not-widget-trees).

## Prerequisites

Two things must exist before phase 1, and neither is really about plugins.

**Phase 0 — PR-time CI.** `.github/workflows/` contains one file. `release.yml` triggers on
`push: tags: ['v*']` and its only build step is `dotnet publish`. **Nothing builds or runs
`GitBench.Tests` on push or PR.** This plan leans on a machine-checked architecture boundary in three
places; all three are unimplementable until a workflow exists to run them. `glfw-static-linking.md`
reached the same conclusion independently, which makes this the second plan blocked on it.

**Phase 0.5 — the measured Lua-under-AOT spike.** One week, four questions, all answered with
numbers, all code thrown away. This is what `terminal.md` did with ConPTY — *"a spike ahead of Phase 1,
because the Windows engine choice hangs off it"* — and the findings changed that seam.

1. Does a hello-world with our own P/Invoke AOT-publish and run on all four RIDs, with Lua
   **statically linked** and no dynamic dependency left behind?
2. Does an `[UnmanagedCallersOnly]` callback work, and does a managed callback invoked from Lua
   survive (a) throwing and (b) a `lua_error` raised beneath it? `StartupHealth.cs:7-10` says a
   managed exception unwinding a native callback frame **fail-fasts under NativeAOT**, so error
   containment is unproven until this is run.
3. Can a `lua_sethook` count hook abort a runaway script without taking the process down? Lua raises
   by `longjmp`; if that cannot cross a managed frame safely, **the deadline in the threading decision
   is not implementable** and the contract changes.
4. What does `luaL_newstate` + `openlibs` + one 200-line `init.lua` cost in milliseconds on the
   slowest release runner?
5. Does `scripts/build-lua.cs` produce a working archive on all three host OSes from one
   implementation?

Publish the answers as a `## Findings — Lua under NativeAOT, measured` section in the shape of
`terminal.md:156-224`. **Then** freeze contracts. Questions 2 and 3 decide two rows of the table
below, which is why they cannot come after.

## Decisions

| Area | Decision |
|---|---|
| Isolation | **Four projects, contracts in the middle.** `GitBench.Extensibility` holds the contracts and depends on nothing. The app and the plugin host both depend on it and never on each other. Enforced by an architecture test **in both directions**, per Rule 2's "encode the intended boundaries as machine-checked rules". |
| Runtime | **Lua 5.4, our own P/Invoke, our own native build, statically linked.** Not KeraLua (its API is shaped around instance delegates marshalled with `Marshal.GetFunctionPointerForDelegate`, so it cannot express the `[UnmanagedCallersOnly]` callback path AOT prefers), not NLua (KeraLua plus reflection-based object binding), not MoonSharp (reflection interop; the hardwired-descriptor generator that makes it AOT-viable is a codegen step to maintain). We hand-write the bindings regardless — the marshalling we want is parse-at-the-boundary, not general-purpose. |
| Native build | **Vendored Lua source, built per-RID in CI.** `vendor/lua`, matching how `vendor/XtermSharp` is already carried. Measured on Ubuntu 24.04 / clang 18 from a clean v5.4.7 checkout: 33 dependency-free source files, one compiler invocation, **5.2 s**, 313 KB shared / ~1 MB static archive. This is not the GLFW situation — there is no X11/Wayland/Cocoa tail behind it. |
| Build tooling | **One C# file-based app**, `scripts/build-lua.cs`, run as `dotnet run scripts/build-lua.cs`. One implementation across Windows, macOS and Linux instead of a `.sh` and a `.ps1` that drift. The SDK is already a build prerequisite, so this adds no toolchain. |
| Contribution declaration | **Declared as data in `plugin.json`; Lua loads lazily on first invocation.** Anchors, item ids, string keys, icons and ordering are readable without an interpreter. |
| Contribution model | Plugins return **data**, the host builds widgets. Crossing the FFI boundary happens once and is **parsed into a contract type**; the raw Lua table is never seen downstream (Rule 1). |
| Reactivity | Contributions carry **thunks, not snapshots** — `Func<string>` labels, `Func<bool>` enablement. `ToolbarButton.Label` is a `Prop<string?>` bound through `L.T(...)` so labels follow a locale switch without a rebuild; a plain `string` would ship a tooltip stuck in the previous language. |
| Menu merging | `IMenuExtensions` returns **contributions only**; the app merges them with a pure `MenuMerge.Apply`. A plugin cannot remove or reorder built-in items, and the ordering policy becomes a tested function rather than a convention inside code the app can't see. |
| Menu item type | Plugins produce `Extensibility.MenuItem`, a sum type (`Action \| Submenu \| Separator`), projected onto the existing `RepoBarContextMenu.Item`. The app's own 9-field bag is left alone — 29 files touch it, and the invariant is enforced at the parse boundary instead. |
| Writes | Plugins never call git writes. `IPluginRepoActions` takes a closed sum type of **app intents** (`RepoAction.DeleteTag`), routed to the flow that already owns the dialog, the confirmation and the translations. |
| Threading | Menu builders and predicates run **synchronously on the UI thread** under a deadline **whose mechanism Phase 0.5 establishes**; they may not call git. Data a builder needs but cannot compute — a remote URL — is pre-resolved into `MenuTarget` by the app. `on_select` may go async. One `lua_State` per plugin, all UI-thread in v1. |
| Localization | Plugins ship `strings/<locale>.json`, loaded into a dictionary for the selected language. Shared app keys are referenced as `@app/<key>`, not copied. Plurals go through `ZGF.Gui.Localization.PluralRules`, the same runtime call the generated `Strings` makes. |
| Trust | **Bundled plugins are trusted; user-installed plugins are not.** Untrusted plugins load without `io`, `os.execute`, `package.loadlib`, C `require` and `debug`, and with `load(chunk, name, "t")` — text chunks only, since Lua 5.4 has no bytecode verifier and crafted binary chunks are arbitrary native code. |
| Errors | Every callback wrapped; **no managed exception may unwind into native Lua**, enforced by one total wrapper rather than by convention. Faults become a toast naming the plugin. Repeat offenders are disabled, **persisted to disk**. |
| Distribution | Folder-based. Bundled: `<app>/plugins/<id>/` as `Content` items. User: `<AppData>/plugins/<id>/`. No registry or auto-update in v1. |
| Out of scope for v1 | Widget trees, custom panels, plugin themes, diff transformers, assistant tools in Lua, forge providers, plugin-to-plugin deps, hot reload, a marketplace, static linking. |

## The seam

### The shape

```
                     GitBench.Extensibility          (depends on nothing)
                     ─────────────────────
   inbound  · MenuAnchor, MenuTarget, MenuContext, MenuItem, ToolbarItem
            · IMenuExtensions, IToolbarExtensions, ICommandContributor
  outbound  · IPluginGitReads, IPluginRepoActions, IPluginShell, IPluginFileLaunch,
              IPluginClipboard, IPluginUi, IPluginEvents, IIconResolver, IPluginStrings
            · Null* implementations of all of it
                          ▲                    ▲
             implements   │                    │   implements
             the ports    │                    │   the extensions
                     GitBench              GitBench.Plugins
                     (the app)   ·······►   (manifest, registry, containment)
                                one line          ▲
                             in AppServices        │
                                             GitBench.Plugins.Lua
                                             (KeraLua — the only Lua in the codebase)
```

**Inbound** is what plugins contribute; the app asks for contributions and merges them itself.
**Outbound** is what plugins may call, implemented in the app as adapters over `IGitService`,
`IPlatformShell`, `IMessageBus`, `LucideIcons` and the localization stack.

### Every contribution names its repo

Repo identity in this app is a `Guid`, never a path: `IGitRemoteOperations.GetRemoteUrl(Repo, string)`
takes a `Repo`, and every message in `GitBench/Messages/` is `(Guid RepoId, ...)`. Contributions
follow the same rule, and the repo travels in an envelope so every anchor has one:

```csharp
public readonly record struct RepoRef(Guid Value);      // identity; marshals to Lua as an opaque string
public sealed record MenuTarget(RepoRef Repo, string RepoPath, MenuContext Context);

public interface IMenuExtensions {
    IReadOnlyList<MenuContribution> Contributions(MenuAnchor anchor, MenuTarget target);
}
public sealed record MenuContribution(PluginId Plugin, MenuPlacement Placement, IReadOnlyList<MenuItem> Items);
public abstract record MenuPlacement { /* Below | Above | At(int) — At is bundled-only */ }
```

`RepoPath` is display and shell data; `RepoRef` is identity and every port takes it. Per-anchor growth
lands in the `MenuContext` union, where a new variant *should* break the build, rather than being
copied into six records.

### Merging is the app's job

```csharp
// FileBrowserContextMenu.Build — last line
return MenuMerge.Apply(items, _extensions.Contributions(MenuAnchor.FileBrowserRow, target));
```

`MenuMerge.Apply` is a pure function in the app: built-ins first, plugin sections below a separator in
load order, `Above` before, `At` honored only for bundled plugins. Directly testable with no host and
no interpreter — including the property that matters most:

```csharp
[Fact] void APluginCannotRemoveBuiltInItems() {
    var merged = MenuMerge.Apply(BuiltIns, [ new(new("evil"), MenuPlacement.Below, []) ]);
    Assert.Equal(BuiltIns, merged.Take(BuiltIns.Count).ToList());
}
```

Handing the item list *into* the extension point and taking one back would let a plugin returning `{}`
delete Stage, Unstage and Discard, indistinguishable from an anchor that legitimately produced
nothing. The same argument applies more strongly to the toolbar, where `BuildActions` returns an
`IWidget[]` whose `Spacer`/`SeparatorSpacer` structure a plugin must not be able to touch.

### Ports are intent-shaped

**`IPluginRepoActions`** — plugins never call git writes:

```csharp
public interface IPluginRepoActions { void Request(RepoRef repo, RepoAction action); }
public abstract record RepoAction { public sealed record DeleteTag(string Name) : RepoAction; }
```

`CommitsViewModel.RequestDeleteTag:373` does not delete anything — it broadcasts a `ShowDialogMessage`
for `DeleteTagDialog`, which carries a *"delete from remote repositories"* checkbox, a
destructive-role button, `ConfirmKeys` and five dedicated strings. `p.ui.confirm(text)` reproduces
none of that. Routing an intent to the existing flow keeps the confirmation, the styling and the seven
locales in the app, and avoids the escape hatch this would otherwise grow under schedule pressure:
`p.command("commits.delete_tag")`, invoke-by-string-name, which un-closes exactly the surface
`MenuContext` is closed to protect.

**`IPluginShell`** — split by intent, with the code-execution primitive separated:

```csharp
public interface IPluginShell {
    void OpenUrl(string url);                             // adapter validates the scheme
    void OpenFolder(string path);
    void RevealPath(string path);
    void OpenTerminalAt(RepoRef repo, string directory);  // adapter owns embedded-vs-OS
}
public interface IPluginFileLaunch { void OpenWithDefaultApp(string path); }   // separate grant, off by default
```

`OpenTerminalAt` exists because "open in terminal" already means two things: `FileBrowserContextMenu`
prefers the embedded pane — `SendInput("cd <dir>\r")` into `ITerminalSessionStore`, then flips
`State<MainViewMode>` — while `LocalChangesViewModel:807` calls the OS shell. A port exposing the
mechanism would let a plugin pick, and one of the two anchors would regress.

`IPluginFileLaunch` is separate because `IPlatformShell.OpenFile` is `UseShellExecute` on Windows.
`FileBrowserContextMenu`'s own remarks explain that the defense against a `.bat` or `.lnk` from a
hostile repository is deliberately *at the caller*. Handing it to untrusted plugins moves that defense
to a prompt whose entire content is a path.

### Composition without a service locator

```csharp
public sealed record PluginHostPorts(IPluginGitReads Git, IPluginRepoActions Actions, IPluginShell Shell,
                                     IPluginFileLaunch FileLaunch, IPluginClipboard Clipboard,
                                     IPluginUi Ui, IPluginEvents Events, IIconResolver Icons,
                                     IPluginStrings Strings, IUiDispatcher Dispatcher);

public static class PluginBootstrap {
    public static PluginExtensions Install(PluginHostPorts ports);   // returns; registers nothing
}
```

Taking the ZGF `Context` instead would mean `GitBench.Plugins` referencing `ZGF.Gui` and being able to
`ctx.Get<T>()` anything the app registered — a type-keyed service locator, on Rule 2's explicit list,
and one the architecture test would not catch because it runs the other way. Returning a value also
removes the hazard where anything resolving the singleton before `Install` keeps the null.

`IPluginEvents` marshals through `IUiDispatcher` itself: `MessageBus` is an unlocked
`Dictionary<Type, List<Delegate>>` whose handlers run on the broadcasting thread.

### What removability means

Two properties, only one of them permanent.

- **P1 — holds forever, checked on every CI build.** The app builds and the non-plugin suite passes
  with the `Null*` bindings bound. A **build configuration**, not a manual exercise; nobody performs a
  manual exercise in month nine.
- **P2 — true until phase 3.** The app is *behaviourally* unchanged without plugins. Once bundled
  plugins own shipped features, removing them removes those features. That is the dogfooding working,
  and it means P2 expires by design.

The residue that stays forever, and is worth paying: `GitBench.Extensibility` remains referenced,
along with `MenuAnchor`/`MenuTarget` construction at six call sites, `IMenuExtensions` in five
constructors and the toolbar, and `App/PluginHostAdapters.cs`.

Static linking adds one more: the `<Import>` carrying the `DirectPInvoke` and archive items has to
sit in `GitBench.csproj` itself, because MSBuild items do not cross a `ProjectReference`. So removal
is two project references, one `PluginBootstrap.Install` line, and one `<Import>` — not the two-line
story it would be with a dynamic native. Worth naming, since the point of P1 is that it is checked
rather than asserted.

## Threading and error propagation

Three hazards. All three are Phase 0.5 questions before they are design decisions.

**Managed exceptions must not unwind into native Lua.** A `lua_CFunction` is a native callback frame,
and `StartupHealth.cs:7-10` states that a managed exception unwinding one fail-fasts under NativeAOT.
Every host port is invoked *from* native Lua. The rule is one total wrapper with one `catch`, applied
by construction — a `try`/`catch` written per-port by convention will be forgotten once, and the
failure mode is process death rather than an error toast. A caught exception is converted into a Lua
error at the boundary, not rethrown.

**Lua raises by `longjmp`.** `luaL_error`, `lua_error`, out-of-memory and a count-hook abort all
longjmp past intervening frames; a managed frame between `lua_pcall` and the raise point does not run
its `finally` blocks. This directly threatens the deadline mechanism, since a count hook is Lua 5.4's
only way to interrupt a runaway script. If Phase 0.5 question 3 comes back negative, builders become
pure and pre-validated with no ability to loop.

**Delegate lifetime.** KeraLua hands Lua a function pointer obtained from
`Marshal.GetFunctionPointerForDelegate`; the pointer does not keep the delegate alive. Every delegate
must be held in a field on the plugin's host object for the lifetime of its `lua_State`. A collected
delegate is a jump into freed memory, not an exception.

## Modules

**`GitBench.Extensibility` — contracts only.** Anchors, targets, contexts, item types, inbound
interfaces, outbound ports, null implementations. No logic beyond the nulls. This project is the API.

**`GitBench.Plugins` — discovery, lifetime, containment.** Folder enumeration, `plugin.json` parsing,
load order, the contribution registry, quarantine, error containment, `PluginBootstrap`. Knows nothing
about Lua: it talks to an `IPluginRuntime` seam, so most tests never boot an interpreter.

**`GitBench.Plugins.Lua` — the binding layer.** Our own `DllImport` surface over the Lua 5.4 C API,
the `gitbench` global, marshalling both ways, the callback wrapper. The only module holding a
`lua_State`, and the only place where a Rule 3 level-1 justification comment is expected on every
function — a P/Invoke declaration and an `[UnmanagedCallersOnly]` callback are checker bypasses by
construction. See below.

**In the app: `PluginHostAdapters`, `PluginStrings`, `MenuMerge`.** The port implementations, the
per-plugin string table, and the merge function.

Every JSON boundary — `plugin.json` and `strings/*.json` — needs a source-generated
`JsonSerializerContext`, as `PreferencesStore.cs:186-191` and four other stores already do. The
strings file is an open-ended dictionary with plural sub-objects, so it needs a hand-written
`Utf8JsonReader` path.

### The binding layer

We own the P/Invoke surface: roughly forty `DllImport` declarations against the Lua 5.4 C API, which
has not moved in years. No third-party binding, and no dependency whose AOT behaviour we do not
control.

**Why hand-rolled rather than KeraLua.** The deciding factor is `[UnmanagedCallersOnly]` — the
AOT-blessed reverse-P/Invoke path. It takes a static method and a plain function pointer, with no
delegate involved, which removes two hazards at once: there is no delegate for the GC to collect
while Lua holds a pointer to it, and there is no reliance on
`Marshal.GetFunctionPointerForDelegate`, which is supported under NativeAOT but is not the annotated
path and which KeraLua ships with no AOT claim at all. KeraLua's API is shaped around instance
delegates, so it structurally cannot offer this.

**What the layer does, per plugin:**

1. Create one `lua_State`. Open the standard libraries, then remove `io`, `os.execute`,
   `package.loadlib`, C `require` and `debug` for an untrusted plugin.
2. Build the `gitbench` global as a table of C functions, each a `static` method carrying
   `[UnmanagedCallersOnly]` and pushed as a `delegate* unmanaged<IntPtr, int>`. Per-plugin state
   hangs off the `lua_State` (registry or an upvalue), not off a captured closure — an
   `[UnmanagedCallersOnly]` method cannot capture.
3. Wrap every one in a single total handler: read arguments off the stack with explicit typed reads,
   build the contract record, catch everything, and convert a fault into a Lua error rather than
   letting it unwind into native code.
4. Load `init.lua` with `load(chunk, name, "t")` — text chunks only.
5. Return contributions by walking the returned table once into `MenuItem`/`ToolbarItem` records. The
   raw table never travels further than this function.

Point 2 is the substantive design consequence of hand-rolling: because the callbacks are static, the
per-plugin context has to live on the Lua side rather than in a C# closure. That is a small amount of
registry bookkeeping, and it is the price of the safer callback path.

### Building the native

**Vendored, not fetched.** Lua source lives in `vendor/lua`, the way `vendor/XtermSharp` already
does — reproducible, offline, auditable, and about a megabyte. Building it is one compiler
invocation over 33 files with no dependencies:

```bash
clang -O2 -fPIC -DLUA_USE_LINUX -c $(ls *.c | grep -vE '^(lua|luac|onelua)\.c$') && ar rcs liblua54.a *.o
```

`onelua.c` must be excluded — it is an amalgamation of the others and duplicate-defines everything if
left in.

**`scripts/build-lua.cs`**, a C# file-based app run with `dotnet run`, replaces what would otherwise
be a shell script and a PowerShell script that drift apart. It selects the platform compiler and
flags (`LUA_USE_LINUX` / `LUA_USE_MACOSX` / default on Windows), builds the archive for the host RID,
and writes it where the `<Import>` below expects it. CI already runs four jobs on four runners, so
each builds its own architecture and no cross-compilation is needed.

**Static linking is back on the table, and for a specific reason.** `glfw-static-linking.md:225-232`
rejected it because `DirectPInvoke` emits no diagnostic when it fails — it silently falls back to the
lazy dynamic path — *and the shared native was still shipping, so the fallback worked and the failure
was invisible*. Owning the build removes the second half: we ship no `lua54` dynamic library at all,
so a failed static link becomes an unresolved load at the first Lua call. Loud, at startup, on the
build that produced it.

The mechanics that review established still apply and are not optional:

- The `DirectPInvoke` and archive items **cannot live in a plugin project** — MSBuild items do not
  flow across a `ProjectReference`. They go in an `<Import>` into `GitBench.csproj`, placed *after*
  its first `PropertyGroup` (or `$(PublishAot)` is empty and the ItemGroup vanishes silently), with
  archive paths through `$(MSBuildThisFileDirectory)` and a guard of `'$(RuntimeIdentifier)' != ''`.
- Use `NativeSystemLibrary` and `NativeFramework`, **not** `LinkerArg` — `Native.Unix.targets`
  appends `@(NativeLibrary)` into `@(LinkerArg)` during target execution, so evaluation-time
  `LinkerArg` entries land before the archives.
- The Windows import-library collision that review flagged does not apply here: it comes from a
  *shared* build emitting a same-named `.lib`, and we only ever produce the static archive.
- Verify positively in CI: the published binary must have **no dynamic dependency on Lua**
  (`ldd` / `otool -L` / `dumpbin /dependents`) and no residual DllImport module string. A green
  publish is not evidence.

If any of this fights back, the fallback is a per-RID dynamic native built by the same script — the
binding layer does not care which it links against.

## Anchor points

An anchor is a named place a plugin may contribute, plus the `MenuContext` variant carried in the
`MenuTarget`. Contexts are inert snapshots built at menu-open.

| Anchor | Call site | Context fields (beyond the envelope's repo) |
|---|---|---|
| `repo.node` | `RepoNodeViewModel.cs:222` | `name`, `branch`, `is_detached`, `has_upstream`, `ahead`, `behind`, `is_dirty`, `remote_web_url` (pre-resolved) |
| `commit.row` | `CommitsView.cs:1079` (`BuildCommitMenuItems`) | `sha`, `short_sha`, `subject`, `author`, `date`, `refs[]`, `parents[]`, `is_merge` |
| `branch.local` / `branch.remote` | `BranchRowState.cs:59` — see below | `name`, `full_ref`, `upstream`, `ahead`, `behind`, `is_head` |
| `localchanges.file` | `FileOpsContextMenu.cs` — three call sites | `files[]` (`{path, absolute_path, status, staged}`) |
| `file_browser.row` | `FileBrowserContextMenu.cs` | `path`, `absolute_path`, `is_directory` |
| `toolbar.actions` | `ActionsToolbar.cs:55` (`BuildActions`) | same as `repo.node` |

Two call sites need more than a wrapped return:

- **`BranchRowState.cs:59` dispatches nine row kinds** — local, remotes and stashes headers, remote
  headers, local and remote folders, local and remote branches, and stash rows. One
  `MenuContext.Branch` cannot describe a folder or a stash, so this is per-case anchor selection.
  Stash and folder anchors are deferred rather than wrongly unified.
- **`FileOpsContextMenu` has three call sites**, two in the review window (`ReviewDiffList.cs:948`,
  `ReviewWindowRootView.cs:124`). Those windows have their own `Context`, so `IMenuExtensions` must be
  registered there too or contributions silently vanish in one surface.

`remote_web_url` is pre-resolved by the app because the threading rule forbids builders from calling
git.

**Ordering.** Plugin items land below a separator, after the built-ins, in load order. `Above` is
available; `At(index)` is bundled-only. Menus are muscle memory, and arbitrary interleaving destroys
that.

## Startup and the load model

Imperative registration is not deferrable: you cannot know what `p.menu("repo.node", fn)` contributes
without executing `init.lua`, and toolbar contributions are needed on frame one. So **`plugin.json`
declares contributions as data and Lua loads lazily, on first invocation.**

Startup becomes N small JSON reads rather than N interpreter boots. The registry is statically
enumerable from disk, so the settings pane and a crash report can name the owning plugin of a
contribution that has never run.

This matches how the app already handles startup cost by construction rather than by warning:
`AppHostSetup.cs:100-115` reads system font fallbacks on a worker because *"none needed until
non-Latin text appears, so reading them must not block first paint"*, and `AppServices.cs:74-75`
defers repo sweeps behind the active repo's first load.

**Budget: the plugin subsystem adds ≤ 15 ms to first paint on the slowest release runner, asserted by
a test.** Phase 0.5 question 4 confirms that number or replaces it.

## Crash containment

`Program.cs:13-19` and `StartupHealth.cs:14`: two consecutive launches that never reach the run loop
make the app assume a bad *build* and call `RecoveryUpdater.TryApplyLatest()`. A user plugin that
faults during load produces exactly that signature — but it lives in `AppData` and **survives the
update**. The result is crash, crash, download, crash, crash, download. In-session "three strikes"
does not help: a native fail-fast never reaches it.

Phase 1 adds, ahead of `RecoveryUpdater`:

- A **persisted quarantine**: write the plugin id to disk before loading, clear it after. On the next
  launch an id still recorded from a launch that never reached `MarkHealthy` is skipped and shown as
  disabled in the settings pane.
- **Safe mode**: `StartupHealth.IsCrashLooping` starts the app with all plugins disabled.
- **Plugin ids in `CrashLog`**: `CrashLog.Install` captures the loaded manifest (id + version) in every
  entry header. It is the only reporting this app has — there is no telemetry — and without it the
  first report after phase 4 is "the app crashes" with no way to know a plugin was involved.

## Where plugins live, and how they load

### Two roots

| Root | Path | Lifecycle |
|---|---|---|
| **Bundled** | `<app>/plugins/<id>/` | Ships as `Content` items; Velopack replaces the app directory wholesale on update, so these update with the app. On macOS this resolves to `DiffDino.app/Contents/MacOS/plugins/`, because `release.yml:128` copies the publish directory into `Contents/MacOS/` — not `Resources/`. |
| **User** | `<AppData>/plugins/<id>/` | Survives updates and uninstalls. These are the ones that can go stale against a new `api_version`, and the ones a crash loop can strand — hence quarantine. |

`AppPaths` currently exposes only `AppDataPath(string fileName)`; it gains an `AppDataDir(string name)`
alongside it. The env-var override and the legacy-folder seeding already handle everything else, so a
scratch data dir gets a scratch plugin set for free.

There is no in-app installer in v1. Installing a user plugin is dropping a folder in; the settings
pane offers a "reveal plugins folder" button and nothing more. No registry, no auto-update, no hot
reload — a change requires a restart, and the settings pane says so.

### A plugin folder

```
plugins/open-remote/
  plugin.json          manifest — the only file read at startup
  init.lua             behaviour — loaded lazily, on first invocation
  strings/en.json      optional; one file per locale the plugin supports
  strings/ja.json
```

`plugin.json` carries id, version, `api_version`, and the contributions as **data**: anchors, item
ids, string keys, icons, placement. That is what makes the registry enumerable without an
interpreter, and it is the whole reason startup is N small JSON reads rather than N interpreter
boots.

### Load order

1. **Enumerate both roots and read `plugin.json` only.** No Lua is loaded, no `lua_State` created.
2. **Parse each manifest into a typed record** (Rule 1 — this is a boundary). A malformed manifest is
   skipped with an error naming the file, not a crash and not a silent omission.
3. **Refuse manifests outside the supported `api_version` range**, naming the plugin and the range.
4. **Resolve id collisions: bundled wins.** A user plugin sharing an id with a bundled one is
   ignored and shown as shadowed in the settings pane. The alternative — user overrides bundled —
   would let a dropped-in folder silently replace a shipped feature, which is both a support problem
   and a trust hole.
5. **Drop disabled and quarantined ids**, read from the host's own state file.
6. **Register contributions from the manifest.** The app now knows every menu item, toolbar button
   and command that exists, and which plugin owns it, without having executed anything.
7. **On first invocation of a contribution**, create that plugin's `lua_State`, load `init.lua`, and
   keep the state for the rest of the session.

Step 6 is the line between "the app knows what a plugin contributes" and "the app has run a plugin's
code". Everything before it is parsing; the interpreter only appears when the user actually clicks
something.

### Host state

Two files under `<AppData>/plugins/`, written through the existing `AtomicFile`:

- `state.json` — disabled plugin ids. **Not `Preferences`**: `Preferences` is a `public sealed record`
  serialized to disk, so a plugin field there would survive removal of the plugin system as dead
  public API with an orphaned key in the user's file. `Preferences.cs` already argues this convention
  for the assistant, holding provider settings as plain strings so the preferences layer stays free
  of assistant types. Keeping the disabled set here means `Preferences` needs no edit at all, which
  removes one touch point from the budget.
- `quarantine.json` — the id currently being loaded, written before the load and cleared after. On
  the next launch, an id still recorded from a launch that never reached `MarkHealthy` is skipped.
  See [Crash containment](#crash-containment).

### What happens on update and uninstall

- **Update**: bundled plugins are replaced with the new build's copies. User plugins are untouched,
  which is why `api_version` refusal has to be a clear message rather than a silent skip — a stale
  user plugin disappearing without explanation after an update is the worst version of this.
- **Uninstall**: `<AppData>/plugins/` is left behind, like the rest of the app data folder. Worth
  naming; not worth solving differently from the existing state files.

## Localization

A plugin ships `strings/<locale>.json` in the app's flat dotted-key format. At load, the file for the
selected language is parsed into a dictionary; switching language reloads it. Keys are namespaced by
plugin id, so two plugins may both use `menu.open`.

```lua
p.t("menu.open_remote")           -- the plugin's own table
p.t("@app/common.open_folder")    -- the app's generated Strings, read-only
p.t("files.selected", count)      -- plural
```

### Plurals

`ZGF.Gui.Localization.PluralRules.Select(CultureInfo, in PluralForms, long)` is public API
(`PluralRules.cs:7`), and `GitBench.csproj:95` already references `ZGF.Gui`. The generator bakes only
the *forms* into a `PluralForms` struct and emits a member that selects at runtime:

```csharp
// LocalizationGenerator.cs:314-315
public string {id}(int count) => string.Format(_culture, PluralRules.Select(_culture, {field}, count), count);
```

`PluginStrings` parses a plugin's plural object into a `PluralForms` and calls the same function.
One implementation, shared. `PluginStrings` lives app-side, so it may use `ZGF.Gui` freely and
`GitBench.Extensibility` still depends on nothing — the port only exposes `string t(key, count?)`.

The rule itself lives in `PluralRules.cs:10-16`: full CLDR for `ar` and `ru`, `n is 0 or 1` for
`fr`/`pt`, and `n == 1` otherwise. That last arm covers `ja`/`ko`/`zh`, deliberately departing from
CLDR so a bare singular can read naturally — `files.stage` in Japanese is `ステージ` for one and
`{count}個のファイルをステージ` otherwise. `LocalizationFormatTests.cs:35-43` pins it.

**Validation at load reuses the generator's own required-category table**
(`LocalizationGenerator.cs:34-43`): `{other}` for ja/ko/zh, `{one, two, few, many, other}` for ar,
`{one, few, many, other}` for ru, `{one, other}` elsewhere. `zero` is optional everywhere, by design —
only a count of 0 selects it and `other` reads acceptably there. A plugin table missing a required
category fails at load naming plugin, key and locale; anything else falls through `PluralForms.Get`,
which resolves a missing category to `other`.

**Interpolation goes through `string.Format` with the culture**, never a string replace. The generator
rewrites `{count}` → `{0}` at build time and the emitted member does
`string.Format(_culture, form, count)`; `PluginStrings` does the same rewrite at load and the same
formatted render, or number formatting drifts from the app's.

Two quirks worth documenting for plugin authors: Russian's `other` form is unreachable for integer
counts (`Russian()`'s default arm returns `Many`), and Arabic renders ASCII digits, not Arabic-Indic —
.NET's `string.Format` ignores `NativeDigits`. If the plugin suite adds plural tests, **assert the
rendered string, not just the category**; the existing tests deliberately stop short of that.

### Shared keys

`common.open_folder` has three call sites, all in `RepoNodeViewModel` (`:253`, `:376`, `:400`), and
`repo.node` is not in `shell-tools`' scope. Moving it is impossible; copying it means seven locales of
drift. Hence `@app/`: shared keys stay in the app, owned in one place, and only genuinely private keys
move. `@app/` resolves against the generated `Strings`, so a key that disappears from the app is a
load-time failure with a clear message rather than a blank label.

**Sharing is the norm, not the exception.** Across the candidate key families, 22 keys have more than
one call site — including `commits.context_delete_tag` (`CommitsView.cs:1048` and `:1120`).
**Enumerate the move list against real call sites before Phase B**; anything shared becomes an `@app/`
reference. Two keys (`repos.group_new_default`, `repos.group_default_name`) appear to have no call
sites and want deleting rather than migrating.

### Pseudo locale

`Locale.Pseudo` is a deterministic transform of English, and `LocalizationServiceTests` asserts it
differs from English for every key. Plugin tables ship no `pseudo.json`, so `PluginStrings` applies
the same transform at runtime — plugin strings stay in the layout-QA pass and no plugin author ever
writes one.

The transform is `Pseudoize` (`LocalizationGenerator.cs:639-660`): wrap in `[`…`]`, accent vowels plus
`cCnNyY`, append `max(1, len/3)` middle dots. Three details it must match: pseudo is built from the
**English** catalog with culture `en`; for parameterized and plural keys the transform runs on the
**positional** string *after* `{count}` → `{0}`, so pad length is `positional.Length / 3`; and
per-category text comes from the English form for that category.

### Migration

Two-phase, so a conversion is reversible by deleting a folder:

- **Phase A** — the plugin ships with no string table at all and uses `@app/` for every key. Nothing
  moves.
- **Phase B** — a release later, keys private to the plugin move into its own table. `@app/` remains
  for the shared ones, permanently.

Roughly 16 keys are candidates to move: four `file_browser.*`, four `toolbar.*`, five
`localchanges.*`, `commits.context_delete_tag`, two `repos.*`. None is plural — every plural key in the
catalog belongs to a stage/unstage/discard/stash item that stays in C#. Grep the **generated member**,
not the JSON key, for each candidate: a key with a surviving C# reference is `@app/`-referenced rather
than moved. Because `Strings` is source-generated, getting this wrong is a build break, not a runtime
one.

> Unrelated cleanup, found while surveying: `ar.json` carries 698 keys against 696 elsewhere. The
> extras, `review.context_stage` and `review.context_unstage`, exist in no other catalog, generate
> nothing, and raise a LOC005 warning on every build.

## Dogfooding: four built-ins become bundled plugins

Ordered by how much contract each one needs to already be right.

**1. `open-remote` — the remote link.** Takes over `RepoNodeViewModel.AddOpenRemoteItem` (`:327`). The
only true transliteration in the set.

`OpenRemote:339` wraps `GetRemoteNames` + `GetRemoteUrl` in `Task.Run` because they are two process
spawns, and builders may not call git — so `MenuTarget` carries a pre-resolved `remote_web_url` and
the plugin keeps today's always-show-then-error shape. Today the item always appears and failure
surfaces `ReposErrorNoRemoteUrl` or `ReposErrorRemoteUrlNotWeb(rawUrl)`, a formatted message naming
the offending URL; a hidden item would tell the user nothing. If phase 5 wants forge-specific URL
shaping in Lua, the port must expose the *raw* remote URL and `Git/RemoteWebUrl.cs` moves into plugin
code — at which point those two error strings need an owner. Settle it in phase 1; it changes the port
signature.

**2. `copy-paths` — the clipboard items.** Takes over the three copy items in
`FileOpsContextMenu.AddCopyItems` and their file-browser counterparts. Proves multi-select context
(`files[]` with N > 1) and the `@app/` reference, since it sits alongside items whose strings stay in
the app.

**3. `tag-actions` — dynamic submenus and a write.** Takes over `AddTagMenuItems`
(`CommitsView.cs:1105`). Proves submenus built from context data and the `IPluginRepoActions` intent
port. These submenus sit at the **top** of the commit menu, so the conversion exercises
`MenuPlacement.At` — a bundled-only placement no third-party plugin may use. A real limit of the
dogfooding claim, stated rather than glossed.

**4. `shell-tools` — the shell escapes.** Takes over the two toolbar icon buttons
(`ActionsToolbar.cs:94,100` — Open Folder, Open Terminal) and the open/reveal/terminal items on two
menu anchors. Last because it is hardest: it needs the thunk-shaped toolbar contract, the
intent-shaped `OpenTerminalAt`, the separate `IPluginFileLaunch` grant, and an OS-name port for
platform-varying labels. Every one of those is a contract phases 1–3 must already have gotten right.

**Rules for a conversion.** Strings moved with all seven locales, or `@app/`-referenced where shared;
identical shortcuts and icons; existing tests still passing; the item in its original position.
Bundled plugins are enabled by default and can be disabled — "Open on remote" disappearing when a user
turns off `open-remote` is deliberate, and is why P2 above expires.

## Testing

The fake-runtime seam keeps most tests interpreter-free, but four shipped features currently covered
by a 110-file test project are moving to Lua. So, in addition:

- **A bundled-plugin suite that does boot Lua**: loads all four plugins, builds every anchor's menu in
  all seven locales, and asserts labels, icons, shortcuts and order against the pre-conversion C#
  output.
- **A hostile-plugin corpus**: throws, infinite loop, returns nil, returns a 10k-item list, returns a
  table whose `__index` metamethod errors, returns cyclic data, declares an unknown anchor, ships a
  plural table missing a required category. The plugin equivalent of `terminal.md`'s recorded byte
  corpora, and the asset that keeps paying.
- **A published-binary smoke test.** `GitBench.Tests` does not set `PublishAot`, so it runs under
  CoreCLR — the same trap `glfw-static-linking.md:53-59` documented. Green Lua tests prove nothing
  about callbacks, `longjmp` or delegate lifetime in the shipped binary. `release.yml` needs a step
  that loads the bundled plugins in the published app and exits non-zero on fault.

## API versioning

`plugin.json` carries an integer `api_version`; the host declares a supported range and refuses
outside it, naming the plugin and the range. Adding a field to a context is a minor bump; removing or
retyping one is a major bump and a load refusal.

Version the icon name table too — `IIconResolver` exposes `LucideIcons` *names* to plugins, so
renaming or dropping an icon is a plugin-visible break.

## Rollback

- **Phases 0–2** — free. Nothing shipped depends on them.
- **Phase 3** — free. Contributions are additive; the `Null*` bindings restore previous behavior.
- **Phase 4** — the expensive one, entirely because of localization. The two-phase string migration is
  what makes it reversible. **Do not delete a migrated key from the app catalog until the release
  after its conversion has shipped.**

## Why not widget trees

ZGF widgets are records built through a source generator, wired to `Prop<T>`/`IReadable<T>` and
rebuilt as state changes. Bridging that to Lua means marshalling across FFI on every rebuild — the
design that makes plugin systems slow and makes host crashes look like plugin bugs.

The middle tier, when we want it (phase 6), is a **constrained panel schema**: a tree from a small
closed vocabulary — `vstack`, `hstack`, `label`, `button`, `list`, `spinner` — rendered with our own
widgets and refreshed when the plugin says so. A strictly larger version of the same
parse-at-the-boundary design, so nothing in v1 blocks it.

## Tension with `docs/coding_rules.md`

Rule 2 forbids implicit registries and ambient pub/sub whose handler set isn't statically knowable. A
plugin system is, on its face, both. The resolution:

- Contributions are **declared in `plugin.json`**, so the handler set is readable from disk without
  running anything — the strongest form of "statically knowable" available here.
- `IMenuExtensions` and friends are **injected**, never located; `PluginBootstrap` returns a value
  rather than taking the DI `Context`.
- The registry is **enumerable and attributable**, surfaced in a Plugins settings pane that is a v1
  deliverable rather than a nicety.
- Anchors are a **closed enum**; an unknown anchor fails at load with a clear message.
- Events go through `IPluginEvents`, which owns the subscriptions and drops them on unload.
- The module boundary is **machine-checked in CI, in both directions** — `Features/**` must not
  reference `GitBench.Plugins`, and `GitBench.Plugins` must reference only `GitBench.Extensibility`
  and the BCL.

What remains genuinely at odds is that a menu's contents are no longer readable from one file. That is
the real cost, accepted knowingly, bounded by the settings pane.

## Phases

0. **PR-time CI.** Build and run `GitBench.Tests` on push and PR.
0.5. **The measured Lua-under-AOT spike.** Four questions, numbers, findings published. Contracts do
   not freeze until this lands.
1. **Contracts + host skeleton.** `GitBench.Extensibility` and `GitBench.Plugins` with a fake runtime.
   Two anchors, null bindings, `MenuMerge`, the architecture test, quarantine and safe mode, the
   settings pane, `api_version`. P1 pinned in CI while P2 still holds.
2. **The Lua runtime**: vendor the source, write `scripts/build-lua.cs`, write the P/Invoke surface
   and the callback wrapper, wire the static-link `<Import>` and the CI dependency check.
3. **Remaining anchors, the toolbar, and `open-remote`.** Strings `@app/`-referenced, not yet moved.
4. **`copy-paths`, `tag-actions`, `shell-tools`**, in that order.
5. **Commands and keybindings**, then forge providers built on `open-remote`.
6. **Panel schema**, assistant tools in Lua, diff transformers.

Plugin commands are consulted **after** the built-in chords. `AppKeybindController` consumes Cmd+K,
Cmd+B, F5 and Cmd+1..9 unconditionally today, and letting a plugin shadow them by load order is a
policy decision, not an implementation detail. If overriding is wanted later it gets a visible
conflict UI.

## Risks

- **Phase 0.5 comes back negative on question 2 or 3.** Then error containment or the deadline is not
  available as designed and the contracts change. That is why the spike precedes the freeze.
- **Static linking fights back.** Mitigated by owning the build: with no dynamic `lua54` shipped, a
  failed link fails loudly at first use rather than silently falling through, and the fallback is a
  per-RID dynamic native from the same script. The binding layer is indifferent to which it links.
- **We now own a C library.** Roughly 30k lines of Lua source in `vendor/`, and its security patches.
  Lua's release cadence is slow and its CVE history is thin, but this is a real obligation that
  KeraLua would have carried for us.
- **Startup regression.** Native AOT was chosen for launch speed; a Git client that got slower to
  launch in exchange for a plugin system is a bad trade. Lazy loading plus a ≤ 15 ms asserted budget
  is the mitigation.
- **Context schemas are permanent.** Once a third-party plugin reads `ctx.sha`, we own it. Hence six
  anchors, `api_version`, and a written deprecation policy.
- **Seam decay.** The failure mode is a good design eroded by six months of "just import the host
  here". The bidirectional architecture test is the only real prevention, which is why phase 0 exists.
- **The localization migration is the expensive rollback.** Two-phase it, and do not delete a key from
  the app catalog until the conversion has shipped a release.
- **Trust.** A plugin is code on the user's machine with access to their repositories. The
  bundled/untrusted split, the text-chunks-only loader, the removed `debug` library and the separate
  `IPluginFileLaunch` grant are the v1 answer. It is not a sandbox and should never be described as
  one. A general consent surface does not exist yet — `ToolApproval` is `internal` to
  `Features/Assistant` and renders as an assistant transcript row — so building one is real work that
  belongs in the phase-1 budget.
