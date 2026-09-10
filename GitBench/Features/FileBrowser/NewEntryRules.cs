using GitBench.Controls.Dialogs;
using GitBench.Localization;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// What may be typed into the "new file" and "new folder" dialogs. A name, not a path: it is
/// created inside the directory that was right-clicked, and the only thing that may cross a
/// directory boundary is a forward slash, which asks for the folders in between to be created too.
/// </summary>
/// <remarks>
/// Deliberately conservative about what it rejects outright. The filesystem is the source of truth —
/// a name this accepts can still fail to be created, and that failure is shown where every other
/// failed operation is — so the rules here only catch what would be a surprise rather than an error:
/// a name that would escape the directory, one that already exists, one carrying a character no
/// filesystem on either platform accepts.
/// </remarks>
internal static class NewEntryRules
{
    private enum Violation { None, Incomplete, InvalidCharacter, Traversal, Rooted, Exists }

    /// <summary>Whether the dialog's create button may be pressed.</summary>
    public static bool IsAcceptable(string name, string directory) =>
        Check(name, directory) == Violation.None;

    public static FieldStatus? Validate(string name, string directory, Strings s) =>
        Check(name, directory) switch
        {
            Violation.InvalidCharacter => new FieldStatus(FieldSeverity.Error, s.FileBrowserNameInvalidCharacter),
            Violation.Traversal => new FieldStatus(FieldSeverity.Error, s.FileBrowserNameTraversal),
            Violation.Rooted => new FieldStatus(FieldSeverity.Error, s.FileBrowserNameRooted),
            Violation.Exists => new FieldStatus(FieldSeverity.Error, s.FileBrowserNameExists),
            // Nothing typed yet, or a path that stops at a separator: incomplete rather than wrong,
            // so the field stays neutral while the create button is still gated on it.
            _ => null,
        };

    /// <summary>Where the entry goes, or null when the name is not one this would create.</summary>
    public static string? Resolve(string name, string directory)
    {
        if (Check(name, directory) != Violation.None) return null;
        try
        {
            return Path.GetFullPath(Path.Combine(directory, Normalized(name)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static Violation Check(string name, string directory)
    {
        var trimmed = Normalized(name);
        if (trimmed.Length == 0) return Violation.Incomplete;
        if (Path.IsPathRooted(trimmed)) return Violation.Rooted;
        if (trimmed[^1] == '/' || trimmed[^1] == Path.DirectorySeparatorChar) return Violation.Incomplete;

        foreach (var segment in trimmed.Split(['/', Path.DirectorySeparatorChar]))
        {
            if (segment.Length == 0) return Violation.Incomplete;
            if (segment is "." or "..") return Violation.Traversal;
            if (segment.AsSpan().IndexOfAny(Invalid) >= 0) return Violation.InvalidCharacter;
        }

        var full = Path.Combine(directory, trimmed);
        return File.Exists(full) || Directory.Exists(full) ? Violation.Exists : Violation.None;
    }

    private static string Normalized(string name) => name.Trim();

    // Both platforms' reserved characters at once, not the running one's: a name typed here ends up
    // in a repository that is cloned on the other, and a file called "a:b" is a file the Windows
    // half of the team cannot check out. The backslash is in the set for the same reason — where it
    // is a separator a name is not allowed to be a path, and where it is not, it reads as one.
    // Anything this accepts that the filesystem still refuses fails at creation, in the OS's own
    // words, which say more than a guess here would.
    private static readonly char[] Invalid = ['\0', '\n', '\r', ':', '*', '?', '"', '<', '>', '|', '\\'];
}
