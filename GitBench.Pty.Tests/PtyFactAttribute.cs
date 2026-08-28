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

static class PtyPlatform
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public const string SkipReason = "No pseudo-terminal implementation for this platform yet.";
}
