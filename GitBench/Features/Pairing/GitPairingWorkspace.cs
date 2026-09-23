using System.Text;
using GitBench.App;
using GitBench.Infrastructure;

namespace GitBench.Features.Pairing;

/// <summary>Each repository's test command, kept in the preferences by repository path. UI thread.</summary>
internal sealed class PairingTestCommands
{
    private readonly PreferencesService _preferences;

    public PairingTestCommands(PreferencesService preferences) => _preferences = preferences;

    public TestCommand? For(string repoPath)
    {
        var key = PathKey.Normalize(repoPath);
        foreach (var entry in _preferences.Current.PairingTestCommands)
            if (PathKey.Comparer.Equals(PathKey.Normalize(entry.RepoPath), key))
                return new TestCommand(entry.Command);
        return null;
    }

    public void Save(string repoPath, TestCommand command)
    {
        var key = PathKey.Normalize(repoPath);
        _preferences.Update(p => p with
        {
            PairingTestCommands =
            [
                .. p.PairingTestCommands.Where(e => !PathKey.Comparer.Equals(PathKey.Normalize(e.RepoPath), key)),
                new PairingTestCommandPreference(key, command.Template),
            ],
        });
    }
}

/// <summary>A repository's working tree for the loop: snapshots through git, test files written
/// and put back, and the repository's test command run through the shell. The file and process
/// work happens off the UI thread.</summary>
internal sealed class GitPairingWorkspace : IPairingWorkspace
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _repoPath;
    private readonly WorkingTreeSnapshots _snapshots;
    private readonly PairingTestCommands _commands;

    public GitPairingWorkspace(string repoPath, WorkingTreeSnapshots snapshots, PairingTestCommands commands)
    {
        _repoPath = repoPath;
        _snapshots = snapshots;
        _commands = commands;
    }

    public Task<SnapshotResult<TreeSnapshot>> CaptureAsync(CancellationToken ct) =>
        Task.Run(() => _snapshots.Capture(_repoPath), ct);

    public Task<SnapshotResult<string>> DiffAsync(TreeSnapshot from, TreeSnapshot to, CancellationToken ct) =>
        Task.Run(() => _snapshots.Diff(_repoPath, from, to), ct);

    public TestCommand? TestCommand => _commands.For(_repoPath);

    public void SaveTestCommand(TestCommand command) => _commands.Save(_repoPath, command);

    public Task<TestWrite> WriteTestAsync(string relativePath, string content, CancellationToken ct) => Task.Run<TestWrite>(() =>
    {
        if (Absolute(relativePath) is not { } path) return new TestWrite.Refused($"{relativePath} is outside the repository.");
        try
        {
            var prior = File.Exists(path) ? File.ReadAllBytes(path) : null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Utf8);
            return new TestWrite.Written(new TestFileUndo(relativePath, prior));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TestWrite.Refused($"{relativePath} could not be written: {e.Message}");
        }
    }, ct);

    public Task RestoreAsync(TestFileUndo undo, CancellationToken ct) => Task.Run(() =>
    {
        if (Absolute(undo.RelativePath) is not { } path) return;
        try
        {
            if (undo.PriorContent is { } prior) File.WriteAllBytes(path, prior);
            else File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }, ct);

    public async Task<TestRun> RunTestAsync(TestCommand command, string name, CancellationToken ct)
    {
        if (command.For(name) is not { } line) return new TestRun.Unrunnable($"'{name}' can't be passed to the test command.");
        return await TestCommandRunner.RunAsync(_repoPath, line, ct).ConfigureAwait(false);
    }

    private string? Absolute(string relative)
    {
        var root = PathKey.Normalize(_repoPath);
        var full = PathKey.Normalize(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : null;
    }
}
