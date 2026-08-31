#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Runtime.CompilerServices;

var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ScriptPath())!, ".."));
var nativeBuild = Path.Combine(repoRoot, "external", "cs_tree_sitter", "native", "build.cs");

if (!File.Exists(nativeBuild))
{
    Console.Error.WriteLine(
        $"external/cs_tree_sitter is empty ({nativeBuild} is missing).{Environment.NewLine}" +
        "Run: git submodule update --init --recursive");
    return 1;
}

var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repoRoot };
start.ArgumentList.Add("run");
start.ArgumentList.Add(nativeBuild);

if (args.Length > 0)
{
    start.ArgumentList.Add("--");
    foreach (var argument in args)
    {
        start.ArgumentList.Add(argument);
    }
}

using var process = Process.Start(start)
    ?? throw new InvalidOperationException("Could not start 'dotnet'. Is the .NET SDK on PATH?");

process.WaitForExit();
return process.ExitCode;

static string ScriptPath([CallerFilePath] string path = "") => path;
