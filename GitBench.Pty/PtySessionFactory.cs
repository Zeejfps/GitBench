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

        if (!Directory.Exists(options.WorkingDirectory))
            throw new DirectoryNotFoundException($"Working directory not found: {options.WorkingDirectory}");

        if (options.Arguments.Any(a => a is null))
            throw new ArgumentException("Arguments cannot contain nulls.", nameof(options));

        if (OperatingSystem.IsWindows())
            return new ConPtySession(options);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return new UnixPtySession(options);

        throw new PlatformNotSupportedException(
            $"No pseudo-terminal implementation for {Environment.OSVersion.Platform}.");
    }
}
