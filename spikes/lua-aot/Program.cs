using System.Diagnostics;
using System.Runtime.InteropServices;

// Phase 0.5 spike for docs/plans/lua-plugins.md.
//
// Everything here goes through our own P/Invoke against a statically linked Lua — the shape we
// intend to ship, not an approximation of it. Answers the questions the plugin contracts hang on,
// under a real NativeAOT publish, and exits non-zero on failure so it can run across all four
// release RIDs in CI.
//
// Q2 and Q3 print "CRASH-IF-ABSENT:" before the risky call and "...survived" after. A first line
// with no second line is the finding: the process died. That is a result, not a broken spike.

internal static unsafe class Program
{
    private static int _failures;

    private static int Main()
    {
        Console.WriteLine("=== Lua under NativeAOT, own P/Invoke, statically linked ===");
        Console.WriteLine($"RID   : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"OS    : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Arch  : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        Q1_LoadsAndRuns();
        Q2a_UnmanagedCallersOnlyCallback();
        Q2b_ManagedExceptionInsideCallback();
        Q2c_LuaErrorBeneathManagedFrame();
        Q3_CountHookAbortsRunawayScript();
        Q4_StartupCost();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $" — {detail}")}");
    }

    // ---- Q1: does a statically linked Lua load and execute under AOT? ---------------------
    // If DirectPInvoke silently fell back to the lazy dynamic path, there is no lua54 to find
    // and this fails at the first call rather than at publish. That loudness is the whole reason
    // owning the native build makes static linking viable.

    private static void Q1_LoadsAndRuns()
    {
        var L = NewState();
        try
        {
            Check("Q1 statically linked Lua executes", Eval(L, "return 6 * 7") && Lua.lua_tointegerx(L, -1, null) == 42);
        }
        finally { Lua.lua_close(L); }
    }

    // ---- Q2a: the [UnmanagedCallersOnly] callback path ------------------------------------
    // The reason we hand-roll rather than use KeraLua: a static method and a plain function
    // pointer, with no delegate for the GC to collect while Lua holds a pointer to it.

    private static void Q2a_UnmanagedCallersOnlyCallback()
    {
        var L = NewState();
        try
        {
            Register(L, "answer", &Answer);
            var ok = Eval(L, "return answer()");
            Check("Q2a [UnmanagedCallersOnly] callback", ok && Lua.lua_tointegerx(L, -1, null) == 42);
        }
        finally { Lua.lua_close(L); }
    }

    [UnmanagedCallersOnly]
    private static int Answer(nint L)
    {
        Lua.lua_pushinteger(L, 42);
        return 1;
    }

    // ---- Q2b: a managed exception inside a callback ----------------------------------------
    // Deliberately does the wrong thing. StartupHealth.cs says a managed exception unwinding a
    // native callback frame fail-fasts under NativeAOT. If that holds, the plan's "one total
    // wrapper" rule is load-bearing rather than tidiness — and this process dies here.

    private static void Q2b_ManagedExceptionInsideCallback()
    {
        var L = NewState();
        try
        {
            Register(L, "boom", &Boom);
            Console.WriteLine("  CRASH-IF-ABSENT: letting a managed exception unwind into Lua...");
            var ok = Eval(L, "local ok, err = pcall(boom); return ok");
            Console.WriteLine("  ...survived; the runtime did not fail-fast.");
            Check("Q2b managed exception unwinding into Lua is survivable", ok,
                  "reaching this line at all is the result");
        }
        finally { Lua.lua_close(L); }
    }

    [UnmanagedCallersOnly]
    private static int Boom(nint L) => throw new InvalidOperationException("boom");

    // ---- Q2c: a lua_error raised beneath a managed frame -----------------------------------
    // Lua raises by longjmp. A managed frame between lua_pcall and the raise point may not run
    // its finally blocks. If it does not, no cleanup may live in a frame Lua can longjmp past
    // and the host must own all disposal outside the callback.

    private static void Q2c_LuaErrorBeneathManagedFrame()
    {
        var L = NewState();
        try
        {
            _finallyRan = false;
            Register(L, "managed_frame", &CallsBackIntoLuaThatErrors);
            Console.WriteLine("  CRASH-IF-ABSENT: longjmp past a managed frame...");
            Eval(L, "function will_error() error('from lua') end\nreturn pcall(managed_frame)");
            Console.WriteLine("  ...survived.");

            Check("Q2c lua_error beneath a managed frame is catchable", Lua.lua_toboolean(L, -1) == 0);
            Check("Q2c managed finally ran despite longjmp", _finallyRan,
                  _finallyRan ? null : "finally was SKIPPED — keep all cleanup outside the callback frame");
        }
        finally { Lua.lua_close(L); }
    }

    private static bool _finallyRan;

    [UnmanagedCallersOnly]
    private static int CallsBackIntoLuaThatErrors(nint L)
    {
        try
        {
            Lua.lua_getglobal(L, "will_error"u8);
            Lua.lua_callk(L, 0, 0, 0, null);   // unprotected: raises, longjmps out of this frame
            return 0;
        }
        finally { _finallyRan = true; }
    }

    // ---- Q3: can a count hook abort a runaway script? --------------------------------------
    // This is the deadline mechanism for menu builders. If it cannot be done safely, builders
    // become pure and non-looping and the threading contract changes.

    private static void Q3_CountHookAbortsRunawayScript()
    {
        var L = NewState();
        try
        {
            Lua.lua_sethook(L, &DeadlineHook, Lua.LUA_MASKCOUNT, 100_000);

            var chunk = "while true do end"u8;
            fixed (byte* p = chunk)
                Lua.luaL_loadbufferx(L, p, (nuint)chunk.Length, "runaway"u8, null);

            Console.WriteLine("  CRASH-IF-ABSENT: aborting an infinite loop from a count hook...");
            var sw = Stopwatch.StartNew();
            var rc = Lua.lua_pcallk(L, 0, 0, 0, 0, null);
            sw.Stop();
            Console.WriteLine("  ...survived.");

            Check("Q3 count hook aborts a runaway script", rc != 0,
                  $"pcall rc={rc} after {sw.ElapsedMilliseconds} ms");
        }
        finally { Lua.lua_close(L); }
    }

    [UnmanagedCallersOnly]
    private static void DeadlineHook(nint L, nint ar)
    {
        fixed (byte* msg = "deadline exceeded"u8) Lua.luaL_error(L, msg);
    }

    // ---- Q4: what does booting an interpreter cost? ----------------------------------------

    private static void Q4_StartupCost()
    {
        var script = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"local v{i} = {i} * 2"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(script);

        var warm = NewState(); Load(warm, bytes); Lua.lua_close(warm);

        const int iterations = 20;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var L = NewState();
            Load(L, bytes);
            Lua.lua_close(L);
        }
        sw.Stop();

        var perBoot = sw.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine($"[INFO] Q4 newstate + openlibs + 200-line script: {perBoot:F2} ms per plugin");
        Check("Q4 within the 15 ms first-paint budget", perBoot < 15.0, $"{perBoot:F2} ms");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static nint NewState()
    {
        var L = Lua.luaL_newstate();
        if (L == 0) throw new InvalidOperationException("luaL_newstate returned null");
        Lua.luaL_openlibs(L);
        return L;
    }

    private static void Register(nint L, ReadOnlySpan<byte> name, delegate* unmanaged<nint, int> fn)
    {
        Lua.lua_pushcclosure(L, fn, 0);
        Lua.lua_setglobal(L, name);
    }

    private static bool Load(nint L, byte[] chunk)
    {
        fixed (byte* p = chunk)
            if (Lua.luaL_loadbufferx(L, p, (nuint)chunk.Length, "spike"u8, null) != 0) return false;
        return Lua.lua_pcallk(L, 0, -1, 0, 0, null) == 0;
    }

    private static bool Eval(nint L, string source) => Load(L, System.Text.Encoding.UTF8.GetBytes(source));
}

/// The hand-written P/Invoke surface. The real thing is roughly forty of these; this is the subset
/// the spike exercises. Every one is a Rule 3 level-1 checker bypass and is why this file is the
/// only place allowed to hold a lua_State.
internal static unsafe partial class Lua
{
    private const string Lib = "lua54";

    public const int LUA_MASKCOUNT = 1 << 3;

    [LibraryImport(Lib)] public static partial nint luaL_newstate();
    [LibraryImport(Lib)] public static partial void luaL_openlibs(nint L);
    [LibraryImport(Lib)] public static partial void lua_close(nint L);

    [LibraryImport(Lib)] public static partial void lua_pushinteger(nint L, long n);
    [LibraryImport(Lib)] public static partial long lua_tointegerx(nint L, int idx, int* isnum);
    [LibraryImport(Lib)] public static partial int lua_toboolean(nint L, int idx);

    [LibraryImport(Lib)] public static partial void lua_pushcclosure(nint L, delegate* unmanaged<nint, int> fn, int n);
    [LibraryImport(Lib)] public static partial void lua_setglobal(nint L, ReadOnlySpan<byte> name);
    [LibraryImport(Lib)] public static partial int lua_getglobal(nint L, ReadOnlySpan<byte> name);

    [LibraryImport(Lib)] public static partial int lua_pcallk(nint L, int nargs, int nresults, int errfunc, nint ctx, nint k);
    [LibraryImport(Lib)] public static partial void lua_callk(nint L, int nargs, int nresults, nint ctx, nint k);
    [LibraryImport(Lib)] public static partial int luaL_loadbufferx(nint L, byte* buff, nuint sz, ReadOnlySpan<byte> name, byte* mode);

    [LibraryImport(Lib)] public static partial void lua_sethook(nint L, delegate* unmanaged<nint, nint, void> f, int mask, int count);
    [LibraryImport(Lib)] public static partial int luaL_error(nint L, byte* fmt);
}
