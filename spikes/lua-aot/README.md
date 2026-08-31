# Phase 0.5 spike — Lua under NativeAOT

Throwaway. Answers the questions in `docs/plans/lua-plugins.md` § Prerequisites, then gets deleted
once its findings are written into a `## Findings — Lua under NativeAOT, measured` section.

Everything goes through **our own P/Invoke against a statically linked Lua** — the shape we intend
to ship, not an approximation. No KeraLua, no NuGet dependency at all.

Not in `GitBench.sln` on purpose: it must not affect the app build.

## Run it

```bash
dotnet run scripts/build-lua.cs                        # builds native/liblua54.a (or lua54.lib)
cd spikes/lua-aot
dotnet publish -c Release -r linux-x64                 # or win-x64 / osx-arm64 / osx-x64
./bin/Release/net10.0/<rid>/publish/LuaAotSpike
```

Exits non-zero if any check fails, so it drops straight into CI across all four release RIDs.

**Publish, don't `dotnet run`.** A CoreCLR run proves nothing about `[UnmanagedCallersOnly]`,
`longjmp`, or static linking — the three things this exists to test.

## What each check decides

| Check | Question | What a failure changes |
|---|---|---|
| Q1 | Does a statically linked Lua execute under AOT? | Fall back to a per-RID dynamic native from the same build script. The binding layer is indifferent. |
| Q2a | Does an `[UnmanagedCallersOnly]` callback work? | The reason for hand-rolling evaporates; reconsider KeraLua or MoonSharp. |
| Q2b | Does a managed exception unwinding into Lua kill the process? | If it dies, the "one total wrapper" rule is load-bearing, not tidiness. |
| Q2c | Is a `lua_error` beneath a managed frame catchable, and does `finally` run? | If `finally` is skipped, no cleanup may live in a frame Lua can `longjmp` past — the host owns all disposal outside the callback. |
| Q3 | Can a count hook abort a runaway script? | If not, the synchronous-with-deadline threading model is unavailable and menu builders become pure and non-looping. |
| Q4 | What does one interpreter boot cost? | Confirms or replaces the ≤ 15 ms first-paint budget. |

Q2b and Q3 print `CRASH-IF-ABSENT:` before the risky call and `...survived` after. **A first line
with no second line is the finding** — the process died, which is a result, not a broken spike.

## Verifying the static link

A green publish is not evidence. `DirectPInvoke` emits no diagnostic when it fails; it silently
falls back to a lazy `dlopen`. What makes that survivable here is that we ship no `lua54` dynamic
library at all, so the fallback has nothing to find and Q1 fails loudly at the first call.

Check the published binary directly as well:

```bash
ldd    publish/LuaAotSpike | grep -i lua   # Linux  — expect no output
otool -L publish/LuaAotSpike | grep -i lua # macOS  — expect no output
dumpbin /dependents publish\LuaAotSpike.exe | findstr /i lua   # Windows — expect nothing
```

## Notes for the real thing

- The `DirectPInvoke` / `NativeLibrary` ItemGroup in `LuaAotSpike.csproj` is guarded on
  `'$(RuntimeIdentifier)' != ''`. Without that guard `$(PublishAot)` is empty and the whole
  ItemGroup vanishes silently. In the app it must live in an `<Import>` placed *after*
  `GitBench.csproj`'s first `PropertyGroup`, because MSBuild items do not cross a
  `ProjectReference` — see `docs/plans/glfw-static-linking.md`.
- Use `NativeSystemLibrary` / `NativeFramework` rather than `LinkerArg` for any system libraries:
  `Native.Unix.targets` appends `@(NativeLibrary)` into `@(LinkerArg)` during target execution, so
  evaluation-time `LinkerArg` entries land ahead of the archives.
- `onelua.c`, `lua.c` and `luac.c` are excluded from the build. The first is an amalgamation that
  duplicate-defines the whole API; the other two carry their own `main()`.
- `scripts/build-lua.cs` prefers `vendor/lua` and otherwise shallow-clones the pinned tag into
  `.lua-src`. Vendoring under `vendor/lua` — as `vendor/XtermSharp` already is — is the end state.
