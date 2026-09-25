using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.Assistant;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>One line of what an edit the user is asked about would change.</summary>
internal abstract record EditPreviewLine
{
    /// <summary>The file the lines after it are in, where the edit spans more than one.</summary>
    public sealed record File(string Path) : EditPreviewLine;

    public sealed record Context(string Text) : EditPreviewLine;

    public sealed record Removed(string Text) : EditPreviewLine;

    public sealed record Added(string Text) : EditPreviewLine;

    /// <summary>The rest of the change, past what the card shows.</summary>
    public sealed record Elided(int Lines) : EditPreviewLine;
}

internal static class EditPreview
{
    public const int MaxLines = 16;

    /// <summary>The lines each edit changes, with one line around them, cut off at
    /// <see cref="MaxLines"/>.</summary>
    public static IReadOnlyList<EditPreviewLine> Of(IReadOnlyList<AcpFileEdit> edits, string repoPath)
    {
        var lines = new List<EditPreviewLine>();
        var headed = edits.Select(e => e.Path).Distinct().Count() > 1;
        string? file = null;
        foreach (var edit in edits)
        {
            if (headed && edit.Path != file)
            {
                file = edit.Path;
                lines.Add(new EditPreviewLine.File(Path.IsPathRooted(edit.Path) ? AgentPrompt.RepoRelative(repoPath, edit.Path) : edit.Path));
            }

            Changed(edit, lines);
        }

        if (lines.Count <= MaxLines) return lines;
        var shown = lines.Take(MaxLines).ToList();
        shown.Add(new EditPreviewLine.Elided(lines.Skip(MaxLines).Count(l => l is not EditPreviewLine.File)));
        return shown;
    }

    private static void Changed(AcpFileEdit edit, List<EditPreviewLine> lines)
    {
        var before = edit.OldText is { } old ? Split(old) : [];
        var after = Split(edit.NewText);
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix
               && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;

        if (prefix > 0) lines.Add(new EditPreviewLine.Context(before[prefix - 1]));
        for (var i = prefix; i < before.Length - suffix; i++) lines.Add(new EditPreviewLine.Removed(before[i]));
        for (var i = prefix; i < after.Length - suffix; i++) lines.Add(new EditPreviewLine.Added(after[i]));
        if (suffix > 0) lines.Add(new EditPreviewLine.Context(after[^suffix]));
    }

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
}

/// <summary>The files the user let the agent edit without asking, for as long as it runs. UI thread only.</summary>
internal sealed class EditAllowances(string repoPath)
{
    private readonly HashSet<string> _files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public bool Cover(IReadOnlyList<string> paths) => paths.Count > 0 && paths.All(p => _files.Contains(Full(p)));

    public void Grant(IReadOnlyList<string> paths)
    {
        foreach (var path in paths) _files.Add(Full(path));
    }

    private string Full(string path) => Path.GetFullPath(Path.Combine(repoPath, path));
}

/// <summary>The "this file" answer to an edit: approves it, and every later edit to the same files
/// while the agent runs.</summary>
internal sealed class FileAllowance
{
    private readonly State<bool> _granted = new(false);

    public FileAllowance(PendingToolApproval pending, Action grant)
    {
        Allow = new Command(() =>
        {
            grant();
            _granted.Value = true;
            pending.Approve.Execute();
        }, pending.IsPending);
    }

    public IReadable<bool> Granted => _granted;

    public ICommand Allow { get; }
}
