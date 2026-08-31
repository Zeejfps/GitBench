# Statically linking GLFW into the AOT build

> **STATUS: NOT VIABLE AS WRITTEN.** Reviewed 2026-08-30 against real NativeAOT publishes
> (ILCompiler 10.0.3). Three load-bearing parts were disproven: the `#if GLFW_STATIC` fix cannot
> be built in this project graph, the post-publish smoke test cannot detect the failure it exists
> to catch, and the `.gitattributes` claim below is factually wrong. Kept for the constraints and
> landmines, which held up. Do not implement from this document — see "Review findings" at the end.

## Goal

Link the patched GLFW into the NativeAOT `DiffDino` executable instead of shipping
`libglfw.3.dylib` / `libglfw.so.3` / `glfw3.dll` beside it — for Release/AOT builds only.

## Why

Not size and not speed. The argument is that `Glfw.NET/Native/README.md` documents a set of
hazards that all share one shape — *"compiles and packages perfectly, fails at load on the user's
machine"* — and static linking converts each into a CI link error:

| Hazard (documented today) | Today | After |
| --- | --- | --- |
| Distro's unpatched `libglfw3` wins resolution on Linux → silent CJK loss | guarded by load order + `NativeLibraryResolver` + `ProbeExport`'s keep-going loop | no resolution step exists |
| Thin dylib in an `osx-*` slot → "fails at `dlopen` under the other architecture" | guarded by a `lipo` assert in the natives workflow | wrong arch = link error |
| LFS pointer stub (131 bytes) → "packages cleanly and fails at load" | unguarded | malformed archive = link error |
| A GLFW bump silently drops an export we P/Invoke → `EntryPointNotFoundException` | guarded by `GlfwImeNativeTests` | unresolved symbol = link error |

Secondary: the macOS `.app` becomes a single Mach-O. No signing today (`release.yml` has no
`codesign`/notarize step), but every nested dylib needs its own signature and hardened-runtime
pass once there is, so this shrinks that surface before it exists.

Expect ~200–300 KB of binary growth-or-shrink noise and no measurable startup or call-rate
change. If size or speed is the justification, don't do this.

## Constraints discovered

These are the two findings that dictate the design.

### 1. `PublishAot` is Release-only, so both paths must ship

`GitBench.csproj:9` sets `PublishAot` only when `Configuration == Release`, deliberately (Hot
Reload). `DirectPInvoke`/`NativeLibrary` are ILC items and are inert without AOT. So:

- **Debug / `dotnet run` / all test projects / `ZGF.Gui.Prototype` / `GitBench.Automation`**
  keep loading the shared library exactly as today.
- **Release AOT publish** links the static archive.

Both artifacts must therefore be produced by the same `glfw-natives.yml` run from the same SHA.
A plan that deletes the dylib is wrong.

### 2. `GlfwIme.IsSupported` probes a library handle that will not exist

`Glfw.NET/GlfwIme.cs` gates the entire IME on `ProbeExport`, which calls
`NativeLibrary.TryLoad` + `TryGetExport`. Under `DirectPInvoke` there is no handle to load.
`TryLoad` fails, `IsSupported` returns **false**, and CJK input dies silently in Release —
reproducing precisely the failure this whole subsystem exists to prevent, and invisible to
`GlfwImeNativeTests` because the test project does not AOT.

This is the single thing most likely to be shipped broken, and it must be fixed *with* the link
change, not after.

The fix is not to patch the probe. Under static linking the probe is answering a question that
the linker has already answered: if `glfwSetPreeditCallback` were absent, the link would have
failed. So on the static path both properties become compile-time `true`:

```csharp
#if GLFW_STATIC
    public static bool IsSupported => true;
    public static bool IsTextInputFocusSupported => true;
#else
    public static bool IsSupported => _isSupported ??= ProbeExport("glfwSetPreeditCallback");
    ...
#endif
```

A runtime probe becomes a build-time guarantee. That is the actual upgrade; the single-file
bundle is a side effect.

## Landmines found while reading

- **`Glfw.cs:25-29` has a dead `#if`.** `Glfw.NET.csproj` defines no `OSX`/`Windows` constants,
  so `LIBRARY` is `"glfw3"` on every platform — the `#elif OSX` → `"libglfw.3"` branch never
  compiles. It happens to be consistent with the csproj's `TargetPath`s. Do not "fix" it as part
  of this change; `DirectPInvoke Include="glfw3"` must match the *effective* value.
- **The universal-dylib rule does not apply to the archive.** `Native/README.md` requires both
  macOS slots to hold the same universal binary *because* the RID-less Debug fallback bundles the
  `osx-x64` file. AOT publish is always RID-specific, so the static archives should be **thin,
  per-RID** — correct by construction, no `lipo` fat-archive step. Keep the universal rule on the
  dylib.
- **Windows CRT must match.** The natives workflow builds with
  `-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded` (static CRT). NativeAOT also links the static CRT,
  so this lines up — but a mismatch surfaces as `LNK4098`/duplicate symbols, so it is worth an
  explicit check rather than an assumption.
- **The Linux glibc floor moves.** The natives workflow builds the `.so` in an `ubuntu:22.04`
  container for glibc 2.35. The static archive gets linked on the `release.yml` runner
  (`ubuntu-latest` = 24.04), which is already where `DiffDino`'s own floor is set today — so the
  22.04 container buys the archive nothing. Either accept that the floor was always the release
  runner's, or move the release Linux job into a container too. **Out of scope, but state which.**
- **GLFW 3.4 `dlopen`s its backends.** X11, Wayland and xkbcommon are loaded via
  `_glfwPlatformLoadModule`, not linked, so the Linux link flags should be close to
  `-ldl -lpthread -lm`. Verify against `build/src/CMakeFiles/glfw.dir/link.txt` rather than
  guessing.

## Steps

### Phase 1 — produce the archives

`framework/.github/workflows/glfw-natives.yml`. Today `CMAKE_COMMON` pins
`-DBUILD_SHARED_LIBS=ON` (line 38).

1. Split `CMAKE_COMMON` into the shared flags plus a per-pass `BUILD_SHARED_LIBS`, and give each
   of the three jobs a second configure+build pass with `OFF` into `build-static/`.
2. Stage the static output alongside the shared one:
   - win-x64: `build-static/src/Release/glfw3.lib`
   - linux-x64: `build-static/src/libglfw3.a`
   - macOS: **two thin builds** (`-DCMAKE_OSX_ARCHITECTURES=x86_64` and `=arm64`) →
     `out/osx-x64/libglfw3.a`, `out/osx-arm64/libglfw3.a`. Do not `lipo` them together.
3. Capture the link line from the shared build (`build/src/CMakeFiles/glfw.dir/link.txt`) into the
   artifact. It is the authoritative list of system libraries the static link now has to supply
   by hand, and it belongs in the manifest, not in a plan's guesswork.
4. Extend the `report` job's manifest table with the static files and that link line.

Same SHA, same run, both artifact kinds — they must never drift.

### Phase 2 — vendor and wire

1. Drop `libglfw3.a` / `glfw3.lib` into the existing `Native/<rid>/` folders next to the shared
   library. Add `*.a`/`*.lib` to `.gitattributes` for LFS — the existing patterns
   (`*.dll`, `*.so.*`, `*.dylib`) do not match them, and the README already records that an
   unmatched pattern means 131-byte stubs.
2. Update `Native/README.md`: the checksum table gains the static files, and the
   "Verifying a candidate binary" section gains the archive equivalent
   (`nm -g libglfw3.a | grep glfwSetTextInputFocus`, `dumpbin /symbols`).
3. Add `framework/Glfw.NET/Glfw.NET.Static.props` (or `.targets`) carrying the `DirectPInvoke`,
   `NativeLibrary` and per-OS `LinkerArg`/`NativeFramework` items, guarded on
   `'$(PublishAot)' == 'true'`, and defining `GLFW_STATIC`.
   - macOS frameworks: Cocoa, IOKit, CoreFoundation, QuartzCore.
   - Windows: `gdi32.lib`, `user32.lib`, `shell32.lib`.
   - Linux: per the captured `link.txt`.
   - **Open question for review:** `DirectPInvoke`/`NativeLibrary` items are evaluated by the
     project being published, and MSBuild items do not flow across a `ProjectReference`. So this
     props file must be `<Import>`ed by `GitBench.csproj` explicitly (a relative path across the
     submodule boundary), or exported via `buildTransitive` conventions. Verify which actually
     reaches ILC before writing either.
4. `GlfwIme.cs`: the `#if GLFW_STATIC` change above. `ProbeExport` and `LibraryNames` stay for the
   non-static path.
5. `NativeLibraryResolver.cs`: leave it. It is dead on the static path (`DirectPInvoke` bypasses
   `SetDllImportResolver`) and load-bearing on the Debug path. Add one sentence to its doc comment
   saying so, so the next reader does not delete it as unreachable.
6. `Glfw.NET.csproj`: the `Content` items stay unconditionally — Debug still needs them, and the
   AOT publish copying an unused dylib is harmless. Optionally exclude it under
   `PublishAot == true` as a follow-up, once the static path is proven; not in this change.

### Phase 3 — guard it

The dylib guard (`GlfwImeNativeTests`) does not cover the static path, and cannot: the test
project does not AOT. Two guards are needed, and neither is a unit test.

1. **Link-time.** Unresolved `glfwSetPreeditCallback`/`glfwSetTextInputFocus` already fails the
   publish. Confirm this is true rather than assumed — check that ILC/`DirectPInvoke` does not
   silently degrade to lazy binding when a symbol is missing. **If it does, this whole plan's
   central claim is false and the design needs rethinking.** Verify this first, before Phase 1.
2. **Post-publish smoke.** In `release.yml`, after the publish step, assert the shipped binary has
   no GLFW dependency and does carry the symbol:
   - macOS: `otool -L publish/<rid>/DiffDino | grep -q glfw && exit 1`, plus `nm` for the entry point.
   - Linux: `ldd`/`readelf -d` equivalent.
   - Windows: `dumpbin /dependents`.
   This is the check that catches "the dylib was still being picked up and the archive was never
   actually linked", which is otherwise invisible and would make the whole change a no-op that
   *looks* like it worked.

### Phase 4 — verify by hand

Automated checks prove linkage, not behaviour. On at least macOS and Windows, from a Release
publish (not a Debug run): type CJK through an IME into a text field, confirm the candidate window
positions against the caret, and confirm bare-letter shortcuts still fire while the IME is active
but not composing. These are the three things `SetPreeditCursorRectangle` and `SetTextInputFocus`
exist for, and the only evidence that the statically-linked GLFW behaves like the dylib.

## Rollback

`git revert` the vendor commit; the dylib path is untouched and still primary in Debug. There is
no data migration and no user-visible surface, so rollback is free at any point.

## Explicitly out of scope

libgit2, oniguruma, FreeType and HarfBuzz all arrive as prebuilt shared natives from NuGet.
Statically linking them means building each from source for three RIDs — libgit2 alone drags
zlib, llhttp, a TLS stack and libssh2, along with a permanent security-patch obligation — and
LibGit2Sharp's `DllImport` name is the hash-suffixed `git2-3f4182d`, pinned to their build. The
cost/benefit is nothing like GLFW's, where we already build from source and the archive is a
second `cmake` pass. Do not extend this plan to them.

---

# Review findings (2026-08-30)

Verified empirically, not from memory: the reviewer built NativeAOT publishes with a real static
archive and observed the behaviour.

## Disproven

1. **The `#if GLFW_STATIC` fix cannot be built.** A `ProjectReference` compiles once per
   Configuration, RID- and publish-agnostic, so there is no per-publish compilation of Glfw.NET
   for a `#if` to key off. Props imported by `GitBench.csproj` define the constant in *GitBench's*
   compilation, not `GlfwIme.cs` — shipping exactly the silent-IME bug this plan calls "the single
   thing most likely to be shipped broken." Defining it inside Glfw.NET guarded on `$(PublishAot)`
   is worse: `Glfw.NET.csproj:5` sets `PublishAot` **unconditionally**, so it would be defined in
   every build, and `GlfwImeNativeTests` — the only guard against a stock GLFW landing in
   `Native/` — would degrade to `Assert.True(true)` everywhere.
   `NativeLibrary.GetMainProgramHandle()` does not rescue it: `-exported_symbols_list /dev/null`
   (`Native.targets:361`) makes statically linked symbols un-`dlsym`-able.
   **Use a `RuntimeHostConfigurationOption` feature switch + `AppContext.TryGetSwitch` instead** —
   it flows from the publishing project and becomes an ILC constant.

2. **The Phase 3.2 smoke test is inert.** `otool -L` never lists a P/Invoke target under
   NativeAOT — the dynamically-bound build shows no `glfw` either, so the check passes identically
   before and after. `nm` finds nothing (`-dead_strip` + empty export list). Sizes differ by 128
   bytes. The real discriminator is `! strings DiffDino | grep -q glfwSetPreeditCallback`: the lazy
   path stores module and entry-point names as literal strings, the static path does not.

3. **`.gitattributes` already covers `*.a`.** Line 45 is `*.a filter=lfs …`; `git check-attr`
   confirms. Only `*.lib` is genuinely unmatched. This plan was written from `Native/README.md`
   rather than the file it proposed to edit.

## The mechanism fails open

`DirectPInvoke` produces no error, warning, or diagnostic when its item does not reach ILC — it
silently emits the ordinary lazy path. A `NativeLibrary` item naming a nonexistent file is not an
error either. Wiring it up wrong is indistinguishable from wiring it up right at build time, and
Phase 2.6 (keep shipping the dylib) makes it indistinguishable at run time too. That also negates
the "single Mach-O" benefit at plan:21 — `release.yml`'s `cp -R publish/<rid>/.` still puts the
dylib in the bundle.

## Confirmed but narrower than claimed

- Missing symbols **do** hard-fail the link — but only for P/Invokes that survive ILC's dependency
  analysis. An unreferenced `DllImport` with a bogus entry point publishes silently. The guarantee
  is a property of the call graph, not of the link. It holds today only because
  `ZGF.Desktop/Input/GlfwImeBridge.cs:31,56,63,70` calls all four entry points.
- **MSBuild items do not flow across a `ProjectReference`** (confirmed by inspecting ILC's item
  list). `buildTransitive` is a NuGet-package convention and does not apply — Glfw.NET is a
  ProjectReference. Explicit `<Import>` works, with three unstated caveats: it must come *after*
  `GitBench.csproj`'s first `PropertyGroup` (or `$(PublishAot)` is empty and the ItemGroup vanishes
  silently); archive paths need `$(MSBuildThisFileDirectory)`; and the guard needs
  `and '$(RuntimeIdentifier)' != ''`.
- **Wrong item types.** `Native.Unix.targets:231` appends `@(NativeLibrary)` into `@(LinkerArg)`
  *during* target execution, so evaluation-time `LinkerArg` entries land before the archives.
  Use `NativeSystemLibrary` (`:242`) and `NativeFramework` (`:269`).

## Missed entirely

- **Windows import-library collision.** A *shared* GLFW build also emits `glfw3.lib` — the import
  library — same filename as the static lib. Staging the wrong one links cleanly and quietly adds
  a `glfw3.dll` import.
- **The Windows CRT reasoning is backwards.** `Native.Windows.targets:114-117` does
  `/NODEFAULTLIB:libucrt.lib` + `/DEFAULTLIB:ucrt.lib` — NativeAOT links the UCRT *dynamically*.
  Outcome is probably fine; the stated reason is wrong and the deferred LNK4098 check tests the
  wrong thing.
- **Linux needs zero added flags.** This GLFW `dlopen`s even libX11
  (`x11_init.c:1288-1302`; no `target_link_libraries` for X11 at all), and ILC already passes
  `-ldl -lrt -lm -pthread`. Phase 1.3's `link.txt` capture is ceremony for the one platform that
  needs nothing. macOS needs Cocoa/IOKit/QuartzCore (CoreFoundation is already at `:203`);
  Windows needs gdi32 and shell32 (`user32` already at `Native.Windows.targets:81`).
- **`-ObjC`/`-force_load` genuinely does not apply** — the pinned GLFW has no Obj-C category
  implementations and no runtime class lookup, and every Obj-C class shares a TU with referenced C
  entry points. The plan's silence was luck, not analysis; record the finding so nobody
  "simplifies" the build later.
- **Phase 2.6 must be struck, not deferred.** `PublishAot` is true for any Release build, so
  excluding the `Content` dylib would break `dotnet build -c Release` for GitBench and
  GitBench.Automation.
- **There is no CI.** `.github/workflows/` holds only `release.yml`, on `push: tags: ['v*']`.
  Nothing builds or runs tests on push or PR, and `GlfwImeNativeTests` — credited in the table
  above as today's guard — is run by no workflow. "Runtime failure modes become CI link errors" is
  really "become release-build errors, found after you push a tag," with `fail-fast: false`
  already packaging the other three platforms.

## Verdict

Don't do this as written. Of the four hazards in the table above: the Linux distro-GLFW row is a
genuine elimination; the wrong-arch row is already covered by the `lipo` assert; the LFS row was
already covered and isn't eliminated anyway while the dylib still ships; the dropped-export row
narrows to "reachable P/Invokes only." Against that, the change adds a silent failure mode (link
didn't happen, dylib covered for it) and a silent regression (`IsSupported` lying in either
direction depending on placement).

**A PR-time CI workflow that builds and runs `GlfwImeNativeTests` delivers most of the claimed
benefit for a fraction of the risk, and is the thing this repo is actually missing.** If static
linking is still wanted afterwards, it needs a different plan: feature switch instead of `#if`,
`strings` instead of `otool`, and *excluding* the dylib from AOT publish so the no-op cannot
masquerade as success.
