# Phase 0.5 spike — Lua under NativeAOT

Throwaway. Answers the four questions in `docs/plans/lua-plugins.md` § Prerequisites, then gets
deleted once its findings are written into a `## Findings — Lua under NativeAOT, measured` section.

Not in `GitBench.sln` on purpose — it must not affect the app build.

## Run it

```bash
cd spikes/lua-aot
dotnet publish -c Release -r linux-x64    # or win-x64 / osx-arm64 / osx-x64
./bin/Release/net10.0/<rid>/publish/LuaAotSpike
```

Exits non-zero if any check fails, so it can go straight into CI across all four release RIDs.
**Publish, don't `dotnet run`** — the whole point is the AOT binary. A CoreCLR run proves nothing
about callbacks or `longjmp`.

## What each check decides

| Check | Question | What a failure changes |
|---|---|---|
| Q1 | Does the native load and execute under AOT? | KeraLua is out; hand-rolled bindings or MoonSharp. |
| Q2a | Does a managed exception unwinding into Lua kill the process? | If it dies, the "one total wrapper" rule is load-bearing, not tidiness. |
| Q2b | Is a `lua_error` beneath a managed frame catchable, and does `finally` run? | If `finally` is skipped, no cleanup may live in a frame Lua can `longjmp` past — the host must own all disposal outside the callback. |
| Q2c | Does `[UnmanagedCallersOnly]` work against the same native? | If this passes where the delegate path struggles, hand-roll the bindings rather than falling back to MoonSharp. |
| Q3 | Can a count hook abort a runaway script? | If not, the synchronous-with-deadline threading model is unavailable and builders become pure and non-looping. |
| Q4 | What does one interpreter boot cost? | Confirms or replaces the ≤ 15 ms first-paint budget. |

Q2a and Q3 print a `CRASH-IF-ABSENT:` line before the risky call and a `...survived` line after.
**A first line with no second line is the finding** — the process died, and that is a result, not a
broken spike.

## Reading Q2c

Q2c and Q3 use the hand-rolled `Raw` bindings at the bottom of `Program.cs` rather than KeraLua,
because KeraLua's API is shaped around instance delegates marshalled with
`Marshal.GetFunctionPointerForDelegate` and cannot express a static
`delegate* unmanaged<IntPtr, int>`. That comparison is the point: if the AOT-blessed callback path
works and the delegate path does not, the engine decision changes from "use KeraLua" to "use
KeraLua's native, own the bindings".

`Raw` is also a fair sample of what owning the P/Invoke surface costs — about a dozen declarations
here, perhaps forty for the real thing.

## Building the native yourself

If the decision goes to hand-rolled bindings, the native side is cheap. Measured on Ubuntu 24.04,
clang 18, from a clean `lua/lua` v5.4.7 checkout:

```bash
clang -O2 -shared -fPIC -DLUA_USE_LINUX -o liblua54.so \
    $(ls *.c | grep -vE '^(lua|luac|onelua)\.c$') -lm
```

33 source files, no dependencies, **5.2 s**, 313 KB, 154 exported `lua*` symbols. Note the
`onelua.c` exclusion — it is an amalgamation of the others and duplicate-defines everything if left
in. This is not the GLFW situation: there is no X11/Wayland/Cocoa tail behind it.
