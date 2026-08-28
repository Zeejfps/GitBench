using GitBench.Pty.Platforms.Unix;
using GitBench.Pty.Platforms.Windows;

namespace GitBench.Pty;

/// <summary>
/// Starts pseudo-terminal sessions using the host operating system's facility for them.
/// </summary>
public sealed class PtySessionFactory : IPtySessionFactory
{
    public IPtySession Start(PtySessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Executable))
            throw new ArgumentException("An executable is required.", nameof(options));

        if (options.Arguments.Any(a => a is null))
            throw new ArgumentException("Arguments cannot contain nulls.", nameof(options));

        ValidateEnvironment(options);

        if (!Directory.Exists(options.WorkingDirectory))
            throw new PtySpawnException(
                PtySpawnFailure.WorkingDirectoryNotFound,
                options.Executable,
                $"Working directory not found: {options.WorkingDirectory}");

        if (OperatingSystem.IsWindows())
            return new ConPtySession(options);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return new UnixPtySession(options);

        throw new PlatformNotSupportedException(
            $"No pseudo-terminal implementation for {Environment.OSVersion.Platform}.");
    }

    /// <remarks>
    /// Windows and POSIX agree on what a variable may be called, so the rule belongs here rather than
    /// in either platform's spawn: a name carrying '=' or a null would otherwise fail on one platform
    /// and silently corrupt the environment on the other.
    /// </remarks>
    static void ValidateEnvironment(PtySessionOptions options)
    {
        foreach (var (name, value) in options.Environment)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Contains('\0'))
                throw new ArgumentException(
                    $"'{name}' is not a usable environment variable name.", nameof(options));

            if (value is not null && value.Contains('\0'))
                throw new ArgumentException(
                    $"The value of '{name}' contains a null character.", nameof(options));
        }
    }
}
