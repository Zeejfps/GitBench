using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using GitBench.Git;

namespace GitBench.Features.Pairing;

/// <summary>
/// Which repository paths count as test files — the only files an agent may write while pairing.
/// A path is a test file when a directory on it is a test directory (<c>test</c>, <c>tests</c>,
/// <c>__tests__</c>, <c>spec</c>, <c>specs</c>, or a name ending in <c>Tests</c> / <c>.Test</c>,
/// as .NET test projects are named), or when the file's own name says so (<c>FooTest.cs</c>,
/// <c>FooTests.cs</c>, <c>foo.test.ts</c>, <c>foo.spec.js</c>, <c>foo_test.go</c>, <c>test_foo.py</c>).
/// </summary>
internal static class TestFiles
{
    private static readonly string[] Directories = ["test", "tests", "__tests__", "spec", "specs"];

    // Case matters for the suffixes: "FooTests" is a test project and "latest" is not.
    private static readonly string[] DirectorySuffixes = ["Tests", ".Test", ".Tests"];
    private static readonly string[] PascalStemSuffixes = ["Test", "Tests", "Spec", "Specs"];
    private static readonly string[] LowerStemSuffixes = ["_test", ".test", ".spec", "_spec"];

    // Files a test runner or a build reads as configuration rather than as a test: writing one would
    // let an agent change what the test command does, not just what it tests.
    private static readonly string[] ConfigNames =
    [
        "conftest.py", "pytest.ini", "tox.ini", "setup.py", "setup.cfg", "pyproject.toml", "package.json",
        "makefile", "directory.build.props", "directory.build.targets", "global.json", "nuget.config",
        "cargo.toml", "go.mod", "build.gradle", "pom.xml",
    ];

    private static readonly string[] ConfigExtensions =
    [
        ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx", ".runsettings",
        ".toml", ".ini", ".cfg", ".yml", ".yaml", ".sh", ".ps1", ".bat", ".cmd", ".mk",
    ];

    public static bool IsTestPath(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var fileName = parts[^1].ToLowerInvariant();
        if (ConfigNames.Contains(fileName) || fileName.Contains(".config.", StringComparison.Ordinal)
            || ConfigExtensions.Contains(Path.GetExtension(fileName)))
            return false;
        foreach (var part in parts)
            if (part is "." or "..")
                return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (Directories.Contains(parts[i].ToLowerInvariant())) return true;
            foreach (var suffix in DirectorySuffixes)
                if (parts[i].EndsWith(suffix, StringComparison.Ordinal))
                    return true;
        }

        var stem = Path.GetFileNameWithoutExtension(parts[^1]);
        if (stem.Length == 0 || stem.Length == parts[^1].Length) return false;
        if (stem.StartsWith("test_", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var suffix in PascalStemSuffixes)
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        foreach (var suffix in LowerStemSuffixes)
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>A repository's test command, with <c>{test}</c> where the test's name goes.</summary>
internal readonly record struct TestCommand(string Template)
{
    public const string Placeholder = "{test}";

    /// <summary>The command line for one test, or null when the name could reach the shell as
    /// anything but a name. The name comes from the agent, and this command is the one thing it
    /// makes the app run.</summary>
    public string? For(string testName) =>
        IsPlainName(testName) ? Template.Replace(Placeholder, testName, StringComparison.Ordinal) : null;

    /// <summary>A name that is one argument and no option: no spaces, nothing a shell reads as
    /// syntax, and no leading '-' that would make it a flag of the test runner.</summary>
    public static bool IsPlainName(string testName)
    {
        if (testName.Length == 0 || testName[0] == '-') return false;
        foreach (var c in testName)
            if (!(char.IsLetterOrDigit(c) || "_.:/-~=,*[]()#@+".Contains(c)))
                return false;
        return true;
    }
}

/// <summary>Runs a test command through the platform shell and reads its result.</summary>
internal static class TestCommandRunner
{
    /// <summary>How much of the end of the output is kept: the failure is at the end.</summary>
    public const int OutputTail = 8_000;

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public static async Task<TestRun> RunAsync(string workingDirectory, string commandLine, CancellationToken ct)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{commandLine}\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", commandLine } };
        start.WorkingDirectory = workingDirectory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        foreach (var (key, value) in LoginShellEnvironment.ForChildProcess) start.Environment[key] = value;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return new TestRun.Unrunnable($"The test command could not be started: {e.Message}");
        }

        using (process)
        {
            var output = new StringBuilder();
            void Append(string? line)
            {
                if (line is null) return;
                lock (output)
                {
                    output.Append(line).Append('\n');
                    if (output.Length > OutputTail * 2) output.Remove(0, output.Length - OutputTail);
                }
            }

            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                if (ct.IsCancellationRequested) throw;
                return new TestRun.Unrunnable($"The test command ran past {(int)Timeout.TotalMinutes} minutes and was stopped.");
            }

            // Drains the redirected streams to their end.
            process.WaitForExit();
            string text;
            lock (output) text = output.Length > OutputTail ? output.ToString(output.Length - OutputTail, OutputTail) : output.ToString();
            return process.ExitCode == 0 ? new TestRun.Passed(text) : new TestRun.Failed(process.ExitCode, text);
        }
    }
}
