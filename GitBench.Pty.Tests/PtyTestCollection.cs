namespace GitBench.Pty.Tests;

/// <summary>
/// Tests that spawn real children run one at a time: they read and write this process's environment
/// and they each hold a console of their own.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PtyTestCollection
{
    public const string Name = "pseudo-terminal";
}
