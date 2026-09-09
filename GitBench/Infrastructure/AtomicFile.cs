using System.Text;

namespace GitBench.Infrastructure;

// Sibling staging file then an atomic rename over the target, so a reader never sees a half-written
// file — but the rename itself is not fsynced, so a power cut can leave the previous one.
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoMark = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static int _staged;

    public static void WriteAllText(string path, string contents) =>
        WriteAllBytes(path, Utf8NoMark.GetBytes(contents));

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents)
    {
        var tmp = Stage(path);
        try
        {
            Fill(tmp, contents);
            Commit(tmp, path);
        }
        catch
        {
            Discard(tmp);
            throw;
        }
    }

    /// <summary>A staging file of an in-flight write, which the file tree and the working-tree
    /// watchers must not report as a change to the repository.</summary>
    public static bool IsStagingPath(string path) =>
        path.EndsWith(StagingSuffix, StringComparison.Ordinal);

    private const string StagingSuffix = ".gitbench.tmp";

    private static string Stage(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return $"{path}.{Environment.ProcessId}-{Interlocked.Increment(ref _staged)}{StagingSuffix}";
    }

    private static void Fill(string tmp, ReadOnlySpan<byte> contents)
    {
        using var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    private static void Discard(string tmp)
    {
        try
        {
            File.Delete(tmp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Commit(string tmp, string path)
    {
        CarryUnixMode(from: path, to: tmp);
        File.Move(tmp, path, overwrite: true);
    }

    private static void CarryUnixMode(string from, string to)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
