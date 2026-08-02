using System.Text.Json;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// The app a write tool acts through, beyond git itself: the commit box it types into, the bus that
/// tells the rest of the app what changed, the registry that says which repository is on screen, the
/// store the remote operations run through, and the dispatcher those touches run on.
/// </summary>
internal sealed record AssistantWriteSurface(
    IUiDispatcher Dispatcher,
    IMessageBus Bus,
    IRepoRegistry Registry,
    ICommitEditor CommitEditor,
    IRepoOperationsStore Operations)
{
    public bool IsActive(Repo repo) => Registry.Active.Value?.Id == repo.Id;

    /// <summary>Runs <paramref name="action"/> where view models and the bus live, and waits for it
    /// — a tool result reports what happened, so it cannot return before it has.</summary>
    public Task OnUiThreadAsync(Action action, CancellationToken ct) =>
        OnUiThreadAsync<object?>(
            () =>
            {
                action();
                return null;
            },
            ct);

    /// <summary>The same hop for a call that has something to hand back — the in-flight operation a
    /// remote tool then waits on, which only the UI thread may ask the store to start.</summary>
    public Task<T> OnUiThreadAsync<T>(Func<T> work, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Post(() =>
        {
            try
            {
                completion.TrySetResult(work());
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
/// The tools that change one repository: staging, the commit message, the commit itself, and tagging
/// a commit or publishing that tag. Each one pauses the turn for the user's approval before it runs.
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
        new CreateTagTool(git, repo, surface),
        new PushTagTool(git, repo),
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
    private readonly IGitWorkingTreeOperations _git;
    private readonly Repo _repo;
    private readonly AssistantWriteSurface _surface;

    public CommitTool(IGitWorkingTreeOperations git, Repo repo, AssistantWriteSurface surface)
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

/// <summary>
/// Names a commit with a tag, and publishes it when asked.
/// </summary>
/// <remarks>
/// Pushing is the same reach the tag dialog's checkbox has — every configured remote, not just
/// origin — so the two produce the same tag from the same request. It is off unless it is asked for:
/// a tag that only exists locally can be deleted and forgotten, and one that reached a remote is
/// something other people have already fetched.
/// </remarks>
internal sealed class CreateTagTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;
    private readonly AssistantWriteSurface _surface;

    public CreateTagTool(IGitService git, Repo repo, AssistantWriteSurface surface)
    {
        _git = git;
        _repo = repo;
        _surface = surface;
    }

    public string Name => "create_tag";

    public string Description =>
        "Tags a commit — HEAD unless commit_sha names another one. A message makes it an annotated "
        + "tag; without one the tag is lightweight. push=true also pushes the tag to every remote "
        + "the repository has, which publishes it — leave it out unless publishing was asked for.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"name":{"type":"string","description":"The tag name, e.g. v1.4.0."},"commit_sha":{"type":"string","description":"Commit to tag, full or abbreviated. Defaults to HEAD."},"message":{"type":"string","description":"Annotation message. Omit for a lightweight tag."},"push":{"type":"boolean","description":"Push the new tag to every configured remote. Default false."}},"required":["name"],"additionalProperties":false}
        """;

    public bool IsWrite => true;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var name = ToolJson.String(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return ToolInvocation.Error("Argument 'name' is required.");
        if (!RefNameRules.IsValid(name))
            return ToolInvocation.Error(
                $"'{name}' is not a usable tag name: no whitespace, no leading '-', no '..' and no trailing '/'.");

        // A tag that already exists is the common way this call fails, and the answer is almost
        // always to publish the one that is there — which is a different tool.
        if (_git.LoadDetails(_repo, "refs/tags/" + name) is Fetched<CommitDetails>.Ok existing)
            return ToolInvocation.Error(
                $"A tag named '{name}' already exists, on {ReadTools.ShortSha(existing.Value.Sha)}. "
                + "Use push_tag to publish that one, or tag under another name.");

        var target = ToolJson.String(args, "commit_sha")?.Trim();
        // A revision reaches git as a positional argument, so one starting with '-' would be read as
        // an option rather than a commit.
        if (target?.StartsWith('-') == true)
            return ToolInvocation.Error("A commit sha may not begin with '-'.");
        if (string.IsNullOrEmpty(target)) target = "HEAD";

        if (_git.LoadDetails(_repo, target) is not Fetched<CommitDetails>.Ok commit)
            return ToolInvocation.Error($"'{target}' does not resolve to a commit in this repository.");

        var push = ToolJson.Bool(args, "push", false);
        IReadOnlyList<string> remotes = push ? _git.GetRemoteNames(_repo) : [];
        // Asked to publish with nowhere to publish to: creating the local tag anyway would report a
        // failure while leaving the tag behind.
        if (push && remotes.Count == 0)
            return ToolInvocation.Error(
                "This repository has no remotes, so the tag cannot be pushed. Call again without "
                + "'push' to tag locally.");

        var message = ToolJson.String(args, "message") ?? string.Empty;
        if (_git.CreateTag(_repo, name, message, commit.Value.Sha, push) is GitOutcome.Failed failed)
            return ToolInvocation.Error(push
                ? $"{failed.Message} The tag is created before it is pushed, so it may exist locally "
                  + "even though this failed — check before tagging again."
                : failed.Message);

        await _surface.OnUiThreadAsync(
            () => _surface.Bus.Broadcast(new RefsChangedMessage(_repo.Id)), ct).ConfigureAwait(false);

        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteBoolean("ok", true);
            writer.WriteString("name", name);
            writer.WriteString("sha", ReadTools.ShortSha(commit.Value.Sha));
            writer.WriteString("summary", commit.Value.MessageShort);
            writer.WriteBoolean("annotated", message.Length > 0);
            if (!push) return;

            writer.WritePropertyName("pushed_to");
            writer.WriteStartArray();
            foreach (var remote in remotes)
                writer.WriteStringValue(remote);
            writer.WriteEndArray();
        }));
    }
}

/// <summary>
/// Publishes a tag that already exists locally — the second half of <see cref="CreateTagTool"/>, for
/// the tag that was created without it.
/// </summary>
/// <remarks>
/// Tagging and publishing are one call when the person asks for both up front, and two when they
/// decide to publish afterwards; without this tool the only way to reach a remote is to create the
/// tag again, which the tag already existing makes impossible.
/// </remarks>
internal sealed class PushTagTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;

    public PushTagTool(IGitService git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "push_tag";

    public string Description =>
        "Pushes a tag that already exists to a remote — 'origin' unless remote names another one, "
        + "and every configured remote when it is omitted. This is how a tag created without push "
        + "gets published; nothing else about the repository moves.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"name":{"type":"string","description":"The existing tag's name."},"remote":{"type":"string","description":"Remote to push to. Omit to push to every configured remote."}},"required":["name"],"additionalProperties":false}
        """;

    public bool IsWrite => true;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var name = ToolJson.String(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolInvocation.Error("Argument 'name' is required."));

        var configured = _git.GetRemoteNames(_repo);
        if (configured.Count == 0)
            return Task.FromResult(ToolInvocation.Error("This repository has no remotes, so there is nowhere to push."));

        var remote = ToolJson.String(args, "remote")?.Trim();
        if (remote is { Length: > 0 } && !configured.Contains(remote))
            return Task.FromResult(ToolInvocation.Error(
                $"'{remote}' is not a remote of this repository. It has: {string.Join(", ", configured)}."));

        if (_git.PushTag(_repo, name, remote) is GitOutcome.Failed failed)
            return Task.FromResult(ToolInvocation.Error(failed.Message));

        IReadOnlyList<string> reached = remote is { Length: > 0 } named ? [named] : configured;
        return Task.FromResult(ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteBoolean("ok", true);
            writer.WriteString("name", name);
            writer.WritePropertyName("pushed_to");
            writer.WriteStartArray();
            foreach (var pushed in reached)
                writer.WriteStringValue(pushed);
            writer.WriteEndArray();
        })));
    }
}
