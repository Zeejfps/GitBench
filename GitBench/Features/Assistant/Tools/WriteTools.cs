using System.Text.Json;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// The app a write tool acts through, beyond git itself: the commit box it types into, the bus that
/// tells the rest of the app what changed, the registry that says which repository is on screen, and
/// the dispatcher those touches run on.
/// </summary>
internal sealed record AssistantWriteSurface(
    IUiDispatcher Dispatcher,
    IMessageBus Bus,
    IRepoRegistry Registry,
    ICommitEditor CommitEditor)
{
    public bool IsActive(Repo repo) => Registry.Active.Value?.Id == repo.Id;

    /// <summary>Runs <paramref name="action"/> where view models and the bus live, and waits for it
    /// — a tool result reports what happened, so it cannot return before it has.</summary>
    public Task OnUiThreadAsync(Action action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task.WaitAsync(ct);
    }

    public Task NotifyAsync(MutationEffects effects, CancellationToken ct) =>
        OnUiThreadAsync(effects.Broadcast, ct);
}

/// <summary>
/// The tools that change one repository: staging, the commit message, and the commit itself. Each
/// one pauses the turn for the user's approval before it runs.
/// </summary>
internal static class WriteTools
{
    public static IReadOnlyList<IAssistantTool> CreateAll(IGitService git, Repo repo, AssistantWriteSurface surface) =>
    [
        new IndexPathsTool(
            "stage_files",
            "Stages the given working-tree paths, the way selecting them and pressing Stage does. "
            + "Paths are repo-relative and must already appear as unstaged changes.",
            git.Stage,
            repo,
            surface),
        new IndexPathsTool(
            "unstage_files",
            "Unstages the given paths, moving them back to the unstaged side. The files on disk are "
            + "left alone — this only takes them out of the index.",
            git.Unstage,
            repo,
            surface),
        new SetCommitMessageTool(repo, surface),
        new CommitTool(git, repo, surface),
    ];

    internal const string PathsSchema =
        """{"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"},"description":"Repo-relative file paths."}},"required":["paths"],"additionalProperties":false}""";

    internal static string WritePaths(IReadOnlyList<string> paths) =>
        ToolJson.Write(writer =>
        {
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("paths");
            writer.WriteStartArray();
            foreach (var path in paths)
                writer.WriteStringValue(path);
            writer.WriteEndArray();
        });
}

/// <summary>Moves paths across the index boundary in one direction — staging or unstaging.</summary>
internal sealed class IndexPathsTool : IAssistantTool
{
    private readonly Func<Repo, IReadOnlyList<string>, GitOutcome> _move;
    private readonly Repo _repo;
    private readonly AssistantWriteSurface _surface;

    public IndexPathsTool(
        string name,
        string description,
        Func<Repo, IReadOnlyList<string>, GitOutcome> move,
        Repo repo,
        AssistantWriteSurface surface)
    {
        Name = name;
        Description = description;
        _move = move;
        _repo = repo;
        _surface = surface;
    }

    public string Name { get; }

    public string Description { get; }

    public string JsonSchema => WriteTools.PathsSchema;

    public bool IsWrite => true;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var paths = ToolJson.Strings(args, "paths");
        if (paths.Count == 0)
            return ToolInvocation.Error("Argument 'paths' must list at least one repo-relative path.");

        if (_move(_repo, paths) is GitOutcome.Failed failed)
            return ToolInvocation.Error(failed.Message);

        await _surface.NotifyAsync(
            MutationEffects.Index(_surface.Bus, _repo.Id, paths.Count == 1 ? paths[0] : null), ct)
            .ConfigureAwait(false);
        return ToolInvocation.Ok(WriteTools.WritePaths(paths));
    }
}

/// <summary>
/// Fills in the commit box. It writes through the box the user is looking at rather than anywhere
/// closer to git, so the text lands in the commit bar and can still be edited or discarded before
/// anything is committed.
/// </summary>
internal sealed class SetCommitMessageTool : IAssistantTool
{
    private readonly Repo _repo;
    private readonly AssistantWriteSurface _surface;

    public SetCommitMessageTool(Repo repo, AssistantWriteSurface surface)
    {
        _repo = repo;
        _surface = surface;
    }

    public const string ToolName = "set_commit_message";

    public string Name => ToolName;

    public string Description =>
        "Writes the commit box's title and body. Nothing is committed — the text appears in the "
        + "commit bar for the person to read, edit or discard. Omitting 'description' clears the "
        + "body rather than leaving whatever was there.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"title":{"type":"string","description":"The subject line."},"description":{"type":"string","description":"The body, blank for none."}},"required":["title"],"additionalProperties":false}
        """;

    public bool IsWrite => true;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var title = ToolJson.String(args, "title");
        if (string.IsNullOrWhiteSpace(title))
            return ToolInvocation.Error("Argument 'title' is required.");

        // The commit box on screen belongs to the active repository; writing into it from any other
        // one would put this repo's message in front of a different checkout.
        if (!_surface.IsActive(_repo))
            return ToolInvocation.Error(
                $"'{_repo.DisplayName}' is not the repository on screen, so its commit box is not visible.");

        var description = ToolJson.String(args, "description") ?? string.Empty;
        await _surface.OnUiThreadAsync(() => WriteEditorText(title, description), ct).ConfigureAwait(false);

        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteBoolean("ok", true);
            writer.WriteString("title", title);
            writer.WriteString("description", description);
        }));
    }

    // The check above ran off the UI thread; the repository on screen can have changed before this
    // lands, and the commit box belongs to whichever one that now is.
    private void WriteEditorText(string title, string description)
    {
        if (!_surface.IsActive(_repo)) return;

        _surface.CommitEditor.SetTitle(title);
        _surface.CommitEditor.SetDescription(description);
    }
}

internal sealed class CommitTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;
    private readonly AssistantWriteSurface _surface;

    public CommitTool(IGitService git, Repo repo, AssistantWriteSurface surface)
    {
        _git = git;
        _repo = repo;
        _surface = surface;
    }

    public string Name => "commit";

    public string Description =>
        "Commits whatever is staged, with the message given here. Nothing else is staged first, so "
        + "check get_local_changes and stage what belongs in the commit before calling this.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"message":{"type":"string","description":"The full commit message: subject, then a blank line, then any body."}},"required":["message"],"additionalProperties":false}
        """;

    public bool IsWrite => true;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var message = ToolJson.String(args, "message");
        if (string.IsNullOrWhiteSpace(message))
            return ToolInvocation.Error("Argument 'message' is required.");

        if (_git.Commit(_repo, message, amend: false) is GitOutcome.Failed failed)
            return ToolInvocation.Error(failed.Message);

        await _surface.OnUiThreadAsync(
            () =>
            {
                ClearCommittedEditorText(message);
                MutationEffects.Commit(_surface.Bus, _repo.Id).Broadcast();
            },
            ct).ConfigureAwait(false);

        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteBoolean("ok", true);
            writer.WriteString("message", message);
        }));
    }

    // The box is emptied only when it held the message that was just committed — anything else in
    // there is something the person wrote for a different commit, and losing it would be theft.
    private void ClearCommittedEditorText(string committed)
    {
        if (!_surface.IsActive(_repo)) return;

        var editor = _surface.CommitEditor;
        var title = editor.Title.Value.Trim();
        var description = editor.Description.Value.Trim();
        var pending = description.Length > 0 ? $"{title}\n\n{description}" : title;
        if (!string.Equals(pending, committed.Trim(), StringComparison.Ordinal)) return;

        editor.SetTitle(string.Empty);
        editor.SetDescription(string.Empty);
    }
}
