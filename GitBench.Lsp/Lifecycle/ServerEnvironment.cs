namespace GitBench.Lsp.Lifecycle;

/// <summary>
/// Where a server's executable is found, and what environment it starts with.
/// </summary>
/// <remarks>
/// A seam rather than a direct <c>PATH</c> lookup because the app does not always have the user's
/// <c>PATH</c>: a GUI launched from the desktop on macOS inherits a minimal one, so a server the
/// user installed with cargo or Homebrew is present and invisible. The app supplies a resolver that
/// reads the login shell's environment; a test supplies a dictionary.
/// </remarks>
public interface IServerEnvironment
{
    /// <summary>
    /// The executable for a configured <c>command</c>, or null when there is none to run. An
    /// absolute path is taken as given; a bare name is looked up. Never resolved through a shell —
    /// the command is an executable and its arguments are a list, so nothing here can be quoted
    /// into meaning something else.
    /// </summary>
    string? ResolveCommand(string command);

    /// <summary>Variables the server process starts with, before its own <c>env</c> block is
    /// applied over the top.</summary>
    IReadOnlyDictionary<string, string> Variables { get; }
}

/// <summary>
/// An environment given as a map: the variables a server starts with, and the <c>PATH</c> its
/// command is looked up on. The one implementation with a lookup in it, so wherever the variables
/// came from — this process, a login shell, or a test — finding the executable works the same way.
/// </summary>
public sealed class MapServerEnvironment(IReadOnlyDictionary<string, string> variables) : IServerEnvironment
{
    public IReadOnlyDictionary<string, string> Variables { get; } = variables;

    public string? ResolveCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;

        Variables.TryGetValue("PATH", out var path);
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in Candidates(directory, command))
                if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    // On Windows the configured name is normally bare and the executable is not.
    private static IEnumerable<string> Candidates(string directory, string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return Path.Combine(directory, command);
            yield break;
        }

        yield return Path.Combine(directory, command + ".exe");
        yield return Path.Combine(directory, command + ".cmd");
        yield return Path.Combine(directory, command + ".bat");
        yield return Path.Combine(directory, command);
        yield break;
    }
}

/// <summary>The environment this process is already running in. The fallback when the app has
/// nothing better to offer, and correct everywhere except a macOS desktop launch.</summary>
public sealed class CurrentProcessEnvironment : IServerEnvironment
{
    public static readonly CurrentProcessEnvironment Instance = new();

    private readonly MapServerEnvironment _environment = new(Read());

    private CurrentProcessEnvironment() { }

    public IReadOnlyDictionary<string, string> Variables => _environment.Variables;

    public string? ResolveCommand(string command) => _environment.ResolveCommand(command);

    private static Dictionary<string, string> Read()
    {
        var variables = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value)
                variables[key] = value;

        return variables;
    }
}
