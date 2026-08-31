#!/usr/bin/env dotnet
// Builds a static Lua 5.4 archive for the host RID.
//
//   dotnet run scripts/build-lua.cs -- [--out <dir>] [--src <dir>]
//
// One implementation for Windows, macOS and Linux, so there is no .sh/.ps1 pair to drift.
// CI runs four jobs on four runners, so each builds its own architecture and nothing
// cross-compiles.
//
// Source resolution: --src, else vendor/lua, else a shallow clone of the pinned tag into
// spikes/lua-aot/.lua-src. Vendoring under vendor/lua is the intended end state, matching
// vendor/XtermSharp.

using System.Diagnostics;
using System.Runtime.InteropServices;

const string LuaTag = "v5.4.7";
const string LuaRepo = "https://github.com/lua/lua.git";

var outDir = ArgValue("--out") ?? Path.Combine(RepoRoot(), "spikes", "lua-aot", "native");
var srcDir = ArgValue("--src") ?? ResolveSource();

Directory.CreateDirectory(outDir);

// onelua.c is an amalgamation of every other file — including it duplicate-defines the whole API.
// lua.c and luac.c are the standalone interpreter and compiler, each with their own main().
string[] excluded = ["onelua.c", "lua.c", "luac.c"];
var sources = Directory.GetFiles(srcDir, "*.c")
    .Where(f => !excluded.Contains(Path.GetFileName(f)))
    .OrderBy(f => f)
    .ToArray();

if (sources.Length == 0) return Fail($"no Lua sources found in {srcDir}");
Console.WriteLine($"Lua source : {srcDir} ({sources.Length} files)");
Console.WriteLine($"Output     : {outDir}");

var sw = Stopwatch.StartNew();
var archive = OperatingSystem.IsWindows() ? BuildWindows() : BuildUnix();
sw.Stop();

var size = new FileInfo(archive).Length;
Console.WriteLine($"Built {Path.GetFileName(archive)} — {size / 1024} KB in {sw.Elapsed.TotalSeconds:F1}s");
return 0;

// ---------------------------------------------------------------------------------------

string BuildUnix()
{
    // LUA_USE_LINUX pulls in dlopen + readline for the standalone interpreter; we exclude lua.c,
    // so it only enables the POSIX bits of the library itself. LUA_USE_MACOSX is the same minus
    // the readline assumption.
    var define = OperatingSystem.IsMacOS() ? "LUA_USE_MACOSX" : "LUA_USE_LINUX";
    var objDir = Path.Combine(outDir, "obj");
    Directory.CreateDirectory(objDir);

    var cc = Which("clang") ?? Which("cc") ?? Which("gcc")
             ?? throw new InvalidOperationException("no C compiler found (clang, cc or gcc)");

    var objects = new List<string>();
    foreach (var src in sources)
    {
        var obj = Path.Combine(objDir, Path.GetFileNameWithoutExtension(src) + ".o");
        Run(cc, ["-O2", "-fPIC", $"-D{define}", "-c", src, "-o", obj]);
        objects.Add(obj);
    }

    var ar = Which("ar") ?? Which("llvm-ar") ?? throw new InvalidOperationException("no ar found");
    var archivePath = Path.Combine(outDir, "liblua54.a");
    File.Delete(archivePath);
    Run(ar, ["rcs", archivePath, .. objects]);
    return archivePath;
}

string BuildWindows()
{
    // clang-cl if present (matches the Unix path), else MSVC cl. Both emit .obj; lib.exe or
    // llvm-lib archives them. Only ever the static archive — a shared build would also emit a
    // lua54.lib import library with the same name, and staging the wrong one links cleanly while
    // quietly adding a lua54.dll dependency.
    var cc = Which("clang-cl") ?? Which("cl")
             ?? throw new InvalidOperationException("no C compiler found (clang-cl or cl); run from a VS developer prompt");
    var objDir = Path.Combine(outDir, "obj");
    Directory.CreateDirectory(objDir);

    var objects = new List<string>();
    foreach (var src in sources)
    {
        var obj = Path.Combine(objDir, Path.GetFileNameWithoutExtension(src) + ".obj");
        Run(cc, ["/nologo", "/O2", "/c", src, $"/Fo{obj}"]);
        objects.Add(obj);
    }

    var lib = Which("lib") ?? Which("llvm-lib") ?? throw new InvalidOperationException("no lib.exe found");
    var archivePath = Path.Combine(outDir, "lua54.lib");
    File.Delete(archivePath);
    Run(lib, ["/nologo", $"/OUT:{archivePath}", .. objects]);
    return archivePath;
}

string ResolveSource()
{
    var vendored = Path.Combine(RepoRoot(), "vendor", "lua");
    if (Directory.Exists(vendored) && Directory.GetFiles(vendored, "*.c").Length > 0) return vendored;

    var cloned = Path.Combine(RepoRoot(), "spikes", "lua-aot", ".lua-src");
    if (!Directory.Exists(cloned))
    {
        Console.WriteLine($"Cloning Lua {LuaTag} (no vendor/lua present)...");
        Run("git", ["clone", "-q", "--depth", "1", "--branch", LuaTag, LuaRepo, cloned]);
    }
    return cloned;
}

void Run(string exe, string[] args)
{
    var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var a in args) psi.ArgumentList.Add(a);

    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
        throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode})\n{stdout}\n{stderr}");
}

string? Which(string tool)
{
    var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" };
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        foreach (var ext in exts)
        {
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir, tool + ext);
            if (File.Exists(candidate)) return candidate;
        }
    return null;
}

string RepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? Directory.GetCurrentDirectory();
}

string? ArgValue(string name)
{
    var argv = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

int Fail(string message) { Console.Error.WriteLine($"error: {message}"); return 1; }
