namespace GitBench.Pty.Tests;

/// <summary>
/// Sets a variable on this process for the duration of a test, so a child can be observed
/// inheriting it — or not inheriting it.
/// </summary>
sealed class EnvironmentVariable : IDisposable
{
    readonly string _name;
    readonly string? _previous;

    public EnvironmentVariable(string name, string? value)
    {
        _name = name;
        _previous = System.Environment.GetEnvironmentVariable(name);
        System.Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => System.Environment.SetEnvironmentVariable(_name, _previous);
}
