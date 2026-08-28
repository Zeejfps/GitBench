namespace GitBench.Pty.Tests;

/// <summary>
/// A throwaway directory for a child to start in. Its <see cref="Token"/> is unique, so finding it
/// in a terminal stream proves the child really started there.
/// </summary>
sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Token = Guid.NewGuid().ToString("N");
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gitbench-pty-{Token}");
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
