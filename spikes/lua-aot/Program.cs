using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KeraLua;

// Phase 0.5 spike for docs/plans/lua-plugins.md.
//
// Answers four questions the plugin contracts hang on, under a real NativeAOT publish.
// Every test prints PASS/FAIL/CRASH-IF-ABSENT and the process exits non-zero if any failed,
// so this can run in CI on all four release RIDs.
//
// A "CRASH-IF-ABSENT" line is printed BEFORE the risky call and a matching "survived" line
// after it. If you see the first without the second, the process died — which is the finding.

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Console.WriteLine($"=== Lua-under-NativeAOT spike ===");
        Console.WriteLine($"RID hint : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"OS       : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Arch     : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        Q1_LoadsAndRuns();
        Q2a_ManagedCallbackThrows();
        Q2b_LuaErrorBeneathManagedFrame();
        Q2c_UnmanagedCallersOnlyCallback();
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

    // ---- Q1: does the native library load and execute at all under AOT? -------------------

    private static void Q1_LoadsAndRuns()
    {
        using var lua = new Lua();
        lua.DoString("result = 6 * 7");
        lua.GetGlobal("result");
        var got = lua.ToInteger(-1);
        Check("Q1 native loads and executes", got == 42, $"6*7 = {got}");
    }

    // ---- Q2a: a managed callback that throws ---------------------------------------------
    // The plan's error containment says no managed exception may unwind into native Lua.
    // This deliberately does the WRONG thing to find out what happens: if the process dies
    // here, the "one total wrapper" rule is load-bearing rather than merely tidy.

    private static void Q2a_ManagedCallbackThrows()
    {
        using var lua = new Lua();
        var fn = new LuaFunction(BoomUnwrapped);
        lua.Register("boom_unwrapped", fn);

        Console.WriteLine("  CRASH-IF-ABSENT: about to let a managed exception unwind into Lua...");
        var status = lua.DoString("local ok, err = pcall(boom_unwrapped); return ok");
        Console.WriteLine("  ...survived; the runtime did not fail-fast.");

        Check("Q2a managed exception unwinding into Lua is survivable", !status,
              "if this line printed at all, the process lived");
        GC.KeepAlive(fn);
    }

    private static int BoomUnwrapped(IntPtr _) => throw new InvalidOperationException("boom");

    // ---- Q2b: a Lua error raised beneath a managed frame ----------------------------------
    // Lua raises by longjmp. A managed frame between lua_pcall and the raise point may not
    // run its finally blocks. This checks whether the finally executes and whether the
    // error is catchable.

    private static void Q2b_LuaErrorBeneathManagedFrame()
    {
        using var lua = new Lua();
        _finallyRan = false;
        var fn = new LuaFunction(CallsBackIntoLuaThatErrors);
        lua.Register("managed_frame", fn);

        Console.WriteLine("  CRASH-IF-ABSENT: about to longjmp past a managed frame...");
        lua.DoString("function will_error() error('from lua') end\nok = pcall(managed_frame)");
        Console.WriteLine("  ...survived.");

        lua.GetGlobal("ok");
        var caught = lua.ToBoolean(-1) == false;
        Check("Q2b lua_error beneath a managed frame is catchable", caught);
        Check("Q2b managed finally ran despite longjmp", _finallyRan,
              _finallyRan ? null : "finally was SKIPPED — no cleanup may live in a frame Lua can longjmp past");
        GC.KeepAlive(fn);
    }

    private static bool _finallyRan;

    private static int CallsBackIntoLuaThatErrors(IntPtr statePtr)
    {
        var state = Lua.FromIntPtr(statePtr)!;
        try
        {
            state.GetGlobal("will_error");
            state.Call(0, 0);   // unprotected: raises, longjmps out of this managed frame
            return 0;
        }
        finally
        {
            _finallyRan = true;
        }
    }

    // ---- Q2c: the [UnmanagedCallersOnly] path ---------------------------------------------
    // KeraLua marshals callbacks with Marshal.GetFunctionPointerForDelegate. This tests the
    // AOT-blessed alternative against the same native library, via raw P/Invoke. If this
    // works and the delegate path does not, hand-rolled bindings are the answer rather than
    // a fallback.

    private static void Q2c_UnmanagedCallersOnlyCallback()
    {
        var L = Raw.luaL_newstate();
        try
        {
            Raw.luaL_openlibs(L);
            unsafe
            {
                delegate* unmanaged<IntPtr, int> fp = &AnswerUCO;
                Raw.lua_pushcclosure(L, (IntPtr)fp, 0);
            }
            Raw.lua_setglobal(L, "answer_uco");

            var script = "return answer_uco()"u8.ToArray();
            var loaded = Raw.luaL_loadbufferx(L, script, (nuint)script.Length, "spike"u8.ToArray(), null);
            var called = loaded == 0 && Raw.lua_pcallk(L, 0, 1, 0, IntPtr.Zero, IntPtr.Zero) == 0;
            var value = called ? Raw.lua_tointegerx(L, -1, IntPtr.Zero) : -1;

            Check("Q2c [UnmanagedCallersOnly] callback via raw P/Invoke", value == 42, $"got {value}");
        }
        finally
        {
            Raw.lua_close(L);
        }
    }

    [UnmanagedCallersOnly]
    private static int AnswerUCO(IntPtr L)
    {
        Raw.lua_pushinteger(L, 42);
        return 1;
    }

    // ---- Q3: can a count hook abort a runaway script? --------------------------------------
    // This is the deadline mechanism. The hook raises, which longjmps. If it cannot be done
    // safely, menu builders cannot be interrupted and the threading contract changes.

    private static void Q3_CountHookAbortsRunawayScript()
    {
        var L = Raw.luaL_newstate();
        try
        {
            Raw.luaL_openlibs(L);
            unsafe
            {
                delegate* unmanaged<IntPtr, IntPtr, void> hook = &DeadlineHook;
                Raw.lua_sethook(L, (IntPtr)hook, Raw.LUA_MASKCOUNT, 100_000);
            }

            var script = "while true do end"u8.ToArray();
            Raw.luaL_loadbufferx(L, script, (nuint)script.Length, "runaway"u8.ToArray(), null);

            Console.WriteLine("  CRASH-IF-ABSENT: about to abort an infinite loop from a count hook...");
            var sw = Stopwatch.StartNew();
            var rc = Raw.lua_pcallk(L, 0, 0, 0, IntPtr.Zero, IntPtr.Zero);
            sw.Stop();
            Console.WriteLine("  ...survived.");

            Check("Q3 count hook aborts a runaway script", rc != 0, $"pcall rc={rc} after {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            Raw.lua_close(L);
        }
    }

    [UnmanagedCallersOnly]
    private static void DeadlineHook(IntPtr L, IntPtr ar) => Raw.luaL_error(L, "deadline exceeded"u8.ToArray());

    // ---- Q4: what does booting an interpreter cost? ----------------------------------------

    private static void Q4_StartupCost()
    {
        var script = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"local v{i} = {i} * 2"));

        // Warm once so the measurement is steady-state rather than first-touch page faults.
        using (var warm = new Lua()) warm.DoString(script);

        const int iterations = 20;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            using var lua = new Lua();       // luaL_newstate + openlibs
            lua.DoString(script);
        }
        sw.Stop();

        var perBoot = sw.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine($"[INFO] Q4 newstate + openlibs + 200-line script: {perBoot:F2} ms per plugin");
        Check("Q4 within the 15 ms first-paint budget", perBoot < 15.0, $"{perBoot:F2} ms");
    }
}

/// Minimal hand-rolled bindings, used by Q2c and Q3 to exercise the paths KeraLua's API shape
/// cannot reach. This is also a sample of what owning the P/Invoke surface looks like.
internal static class Raw
{
    private const string Lib = "lua54";

    public const int LUA_MASKCOUNT = 1 << 3;

    [DllImport(Lib)] public static extern IntPtr luaL_newstate();
    [DllImport(Lib)] public static extern void luaL_openlibs(IntPtr L);
    [DllImport(Lib)] public static extern void lua_close(IntPtr L);
    [DllImport(Lib)] public static extern void lua_pushcclosure(IntPtr L, IntPtr fn, int n);
    [DllImport(Lib)] public static extern void lua_pushinteger(IntPtr L, long n);
    [DllImport(Lib)] public static extern void lua_setglobal(IntPtr L, string name);
    [DllImport(Lib)] public static extern long lua_tointegerx(IntPtr L, int idx, IntPtr isnum);
    [DllImport(Lib)] public static extern int lua_pcallk(IntPtr L, int nargs, int nresults, int errfunc, IntPtr ctx, IntPtr k);
    [DllImport(Lib)] public static extern int luaL_loadbufferx(IntPtr L, byte[] buff, nuint sz, byte[] name, string? mode);
    [DllImport(Lib)] public static extern void lua_sethook(IntPtr L, IntPtr f, int mask, int count);
    [DllImport(Lib)] public static extern int luaL_error(IntPtr L, byte[] fmt);
}
