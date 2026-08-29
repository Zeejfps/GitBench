namespace GitBench.Pty.Tests;

/// <summary>
/// A throwaway directory for a child to start in. Its <see cref="Token"/> is unique, so finding it
/// in a terminal stream proves the child really started there.
/// </summary>
sealed class TempDirectory : IDisposable
{
    /// <param name="nameSuffix">
    /// Appended to the directory name, for the tests that need a path a shell would mangle if it
    /// were ever pasted into a command line rather than passed as one argument.
    /// </param>
    public TempDirectory(string nameSuffix = "")
    {
        Token = Guid.NewGuid().ToString("N");
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"gitbench-pty-{Token}{(nameSuffix.Length == 0 ? "" : " " + nameSuffix)}");
        Directory.CreateDirectory(Path);
    }

    public string Token { get; }

    public string Path { get; }

    public string File(string name, string contents)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>A file the host will agree to run, for the tests about how a program is resolved.</summary>
    /// <remarks>
    /// The Windows guard is load-bearing rather than decoration: <c>SetUnixFileMode</c> is annotated
    /// unsupported on Windows, and calling it unguarded is a CA1416 warning in a project that is
    /// meant to build without any.
    /// </remarks>
    public string Executable(string name, string contents)
    {
        var path = File(name, contents);

        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
