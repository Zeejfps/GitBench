using System.Text.RegularExpressions;

namespace GitBench.Git;

/// <summary>
/// A git process that crashed or was force-killed leaves its <c>*.lock</c> file behind, and every
/// later operation on the repo fails with "Unable to create '…lock': File exists" until someone
/// removes it by hand. Git always names the absolute path in that message, so detection is a pure
/// function of the error text — a dialog can offer the recovery without knowing which repo it is
/// talking about.
/// </summary>
internal static partial class GitLockFile
{
    // Matches both the fatal form ("fatal: Unable to create '<path>': File exists.") and the
    // plain one git emits for ref locks; the trailing period is optional across git versions.
    [GeneratedRegex(@"Unable to create '([^']+\.lock)': File exists", RegexOptions.IgnoreCase)]
    private static partial Regex LockMessage();

    /// <summary>The stale lock file named by <paramref name="error"/>, or null if it isn't a lock failure.</summary>
    public static string? Detect(string? error)
    {
        if (string.IsNullOrEmpty(error)) return null;
        var match = LockMessage().Match(error);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Deletes the lock file. Returns null on success (including when it is already gone — another
    /// process may have finished in the meantime) or the failure message. The <c>.lock</c> suffix is
    /// re-checked here so a caller can never route an arbitrary path into a delete.
    /// </summary>
    public static string? Remove(string path)
    {
        if (!path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return $"Refusing to delete '{path}': not a lock file.";

        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
