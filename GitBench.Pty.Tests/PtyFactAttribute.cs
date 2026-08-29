namespace GitBench.Pty.Tests;

/// <summary>
/// A fact that spawns a real pseudo-terminal, and so only runs where one is implemented.
/// </summary>
public sealed class PtyFactAttribute : FactAttribute
{
    public PtyFactAttribute()
    {
        if (!PtyPlatform.IsSupported)
            Skip = PtyPlatform.SkipReason;
    }
}

/// <summary>
/// A theory that spawns a real pseudo-terminal, and so only runs where one is implemented.
/// </summary>
public sealed class PtyTheoryAttribute : TheoryAttribute
{
    public PtyTheoryAttribute()
    {
        if (!PtyPlatform.IsSupported)
            Skip = PtyPlatform.SkipReason;
    }
}

/// <summary>
/// A fact for behaviour the two platforms genuinely owe different answers on, asserted on Windows.
/// </summary>
/// <remarks>
/// Not an escape hatch for anything awkward to express portably: a contract that holds everywhere
/// belongs behind <see cref="PtyFactAttribute"/>, and gating it here would quietly stop proving it on
/// the other host. The only thing that belongs here is a difference the platforms really have, such
/// as how environment variable names collate.
/// </remarks>
public sealed class WindowsPtyFactAttribute : FactAttribute
{
    public WindowsPtyFactAttribute()
    {
        if (!PtyPlatform.IsSupported || !OperatingSystem.IsWindows())
            Skip = PtyPlatform.WindowsOnlyReason;
    }
}

/// <summary>
/// A fact for behaviour the two platforms genuinely owe different answers on, asserted on Unix.
/// </summary>
/// <remarks>See <see cref="WindowsPtyFactAttribute"/> for what does and does not belong behind a gate.</remarks>
public sealed class UnixPtyFactAttribute : FactAttribute
{
    public UnixPtyFactAttribute()
    {
        if (!PtyPlatform.IsUnix)
            Skip = PtyPlatform.UnixOnlyReason;
    }
}

/// <summary>
/// A theory for behaviour the two platforms genuinely owe different answers on, asserted on Unix.
/// </summary>
public sealed class UnixPtyTheoryAttribute : TheoryAttribute
{
    public UnixPtyTheoryAttribute()
    {
        if (!PtyPlatform.IsUnix)
            Skip = PtyPlatform.UnixOnlyReason;
    }
}

static class PtyPlatform
{
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public static bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public const string SkipReason = "No pseudo-terminal implementation for this platform yet.";

    public const string WindowsOnlyReason = "Windows environment name collation is a Windows contract.";

    public const string UnixOnlyReason = "POSIX process groups, signals and byte-string names are a Unix contract.";

    /// <summary>
    /// What the name of a platform-gated test has to end with, so that its gate and its name cannot
    /// drift apart unnoticed. Enforced by <c>PtyPlatformTests</c>.
    /// </summary>
    public const string WindowsSuffix = "OnWindows";

    public const string UnixSuffix = "OnUnix";
}
