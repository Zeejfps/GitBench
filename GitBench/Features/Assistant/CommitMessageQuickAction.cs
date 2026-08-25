using System.Text;
using System.Text.Json;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// One repository's "Generate commit message": runs the commit-message agent and types what comes
/// back into the commit box.
/// </summary>
/// <remarks>
/// The chat's own pipeline runs this — same <see cref="AssistantAgentLoop"/>, same backend, same
/// toolset — with only the agent swapped, so the model tier and the tools it may reach come from the
/// agent file rather than from a second code path here.
///
/// The message arrives as a <c>set_commit_message</c> call, not as the turn's reply: a subject and a
/// body are two fields, and reading them back out of prose put whatever the model wrote first — a
/// preamble, a fence, "there is nothing to commit" — in the subject line. So a turn that ends without
/// that call has produced no message, and says so. The one exception is an endpoint that cannot call
/// tools at all, where the reply is still parsed as the message it was asked for.
///
/// The tool writes through <see cref="LocalChanges.ICommitEditor"/>, which is a view-model edit the
/// person watches land; nothing is staged or committed, so no approval stands in front of it — the
/// button press was the approval. It overwrites a subject and a body the person may have written
/// themselves, so what the box held is captured before the run and offered back as an undo. A run
/// already in flight is not queued behind: a second request while one is going is dropped.
/// </remarks>
internal sealed class CommitMessageQuickAction : IDisposable
{
    private const string Instruction =
        "Write the commit message for what is about to be committed here and set it with "
        + "set_commit_message.";

    private readonly Repo _repo;
    private readonly IGitService _git;
    private readonly AssistantAgentLoop _loop;
    private readonly AssistantWriteSurface _writes;
    private readonly ILocalizationService _loc;
    private readonly IUiDispatcher _dispatcher;
    private readonly State<bool> _busy = new(false);

    private CancellationTokenSource? _run;
    // What the commit box held when this run started, captured before the tool can overwrite it.
    private (string Title, string Description) _replaced;
    private bool _disposed;

    public CommitMessageQuickAction(
        Repo repo,
        IGitService git,
        AssistantAgentLoop loop,
        AssistantWriteSurface writes,
        ILocalizationService loc,
        IUiDispatcher dispatcher)
    {
        _repo = repo;
        _git = git;
        _loop = loop;
        _writes = writes;
        _loc = loc;
        _dispatcher = dispatcher;
    }

    public IReadable<bool> IsBusy => _busy;

    public void Run()
    {
        if (_busy.Value || _disposed) return;

        _busy.Value = true;
        _replaced = (_writes.CommitEditor.Title.Value, _writes.CommitEditor.Description.Value);

        var cts = new CancellationTokenSource();
        _run = cts;
        _ = Task.Run(() => RunAsync(cts));
    }

    /// <summary>Abandons a run in flight. What it was going to write is dropped, not reported.</summary>
    public void Cancel() => _run?.Cancel();

    private async Task RunAsync(CancellationTokenSource cts)
    {
        // A fresh conversation every time: this is a one-shot, and nothing about the last generated
        // message should steer the next one.
        var conversation = new List<AssistantMessage> { new AssistantMessage.User(Instruction) };
        var answer = new StringBuilder();
        var wrote = false;
        var toolsWereNeverPossible = false;
        string? failure = null;
        var cancelled = false;

        try
        {
            await foreach (var e in _loop
                               .RunAsync(conversation, AssistantAgentLoop.RepoStateBlock(_git, _repo, _loc), ApproveTheCommitBox.Instance, cts.Token)
                               .ConfigureAwait(false))
            {
                switch (e)
                {
                    case AssistantEvent.TextDelta delta:
                        answer.Append(delta.Text);
                        break;
                    case AssistantEvent.ToolFinished finished
                        when finished.Name == SetCommitMessageTool.ToolName:
                        wrote = !finished.IsError;
                        break;
                    case AssistantEvent.NoToolSupport:
                        toolsWereNeverPossible = true;
                        break;
                    case AssistantEvent.Refused refused:
                        failure = refused.Explanation ?? Strings.AssistantGenerateMessageEmpty;
                        break;
                    case AssistantEvent.Failed failed:
                        failure = failed.Message;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        var outcome = new Outcome(wrote, toolsWereNeverPossible, answer.ToString(), failure, cancelled);
        _dispatcher.Post(() => Complete(cts, outcome));
    }

    /// <summary>How one run ended, as the UI thread needs to read it: whether the tool wrote the
    /// message, whether tool calling was available at all, whatever the model said in prose, and
    /// whether the run failed or was stopped.</summary>
    private readonly record struct Outcome(
        bool Wrote, bool ToolsWereNeverPossible, string Reply, string? Failure, bool Cancelled);

    // Everything the agent needs beyond its tools. The reply language rides here rather than in the
    // system prompt for the same reason it does in the chat session: the system block is the cached
    // prefix, and templating a user setting into it would re-bill the whole prefix whenever the
    // language changed. Read per turn, so a switch takes effect on the next generation.
    //
    // The Quick tier does not accept mid-conversation system entries, so the request writer renders
    // this block as a user turn instead. That branch is the reason this is passed to the loop rather
    // than folded into the instruction above.
    // A stopped run reports nothing — the person stopped it.
    private void Complete(CancellationTokenSource cts, Outcome outcome)
    {
        if (!_disposed)
        {
            if (!outcome.Cancelled) Apply(outcome);
            _busy.Value = false;
        }

        if (ReferenceEquals(_run, cts)) _run = null;
        cts.Dispose();
    }

    private void Apply(Outcome outcome)
    {
        if (outcome.Failure is { } failure)
        {
            Fail(failure);
            return;
        }

        // The tool has already typed into the box; what is left is the way back out of it.
        if (outcome.Wrote)
        {
            OfferUndo();
            return;
        }

        // An endpoint that cannot call tools at all still gets to write a message: its reply is read
        // as one, the way every reply was before there was a call to make.
        if (outcome.ToolsWereNeverPossible && Parse(outcome.Reply) is { } message)
        {
            // A run whose repository is no longer the one on screen writes nothing and reports
            // nothing: the message would land in front of a different checkout, and the person moved
            // on rather than hit a failure.
            if (_writes.IsActive(_repo)) Write(message);
            return;
        }

        // The model answered in prose where a call was asked for, so there is no message — only
        // whatever it said instead, which is usually the reason (nothing staged, nothing to say).
        Fail(FirstLine(outcome.Reply) ?? Strings.AssistantGenerateMessageEmpty);
    }

    // Failures go where every other failure in the app goes — the operation-error dialog — rather
    // than a banner wedged above the commit box.
    private void Fail(string reason) => _writes.Bus.Broadcast(
        new ShowOperationErrorMessage(Strings.AssistantGenerateMessageFailed, reason));

    // Both fields go through the commit box's own setters, so the bar's bindings update the way they
    // do when the text is typed. A message with no body leaves the description alone — the model
    // deciding there was nothing to add is not a reason to erase it.
    private void Write(CommitMessage message)
    {
        _writes.CommitEditor.SetTitle(message.Title);
        if (message.Body is { } body) _writes.CommitEditor.SetDescription(body);
        OfferUndo();
    }

    // The generation replaces work the person may have done themselves, and an undo is cheaper than a
    // dialog standing in front of every one of them. What it restores was captured before the run, so
    // it puts back what was there whether the tool wrote the text or the fallback did.
    private void OfferUndo()
    {
        var editor = _writes.CommitEditor;
        var (previousTitle, previousDescription) = _replaced;
        var strings = Strings;

        _writes.Bus.Broadcast(new ShowToastMessage(ToastIntent.Success(
            strings.AssistantGenerateMessageWritten,
            new ToastAction(strings.AssistantGenerateMessageUndo, () =>
            {
                editor.SetTitle(previousTitle);
                editor.SetDescription(previousDescription);
            }))));
    }

    private static string? FirstLine(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }

    private Strings Strings => _loc.Strings.Value;

    /// <summary>A generated commit message as it reaches the commit box: a subject line, and a body
    /// only where the model wrote one.</summary>
    internal readonly record struct CommitMessage(string Title, string? Body);

    // Git's own shape: the subject is the first line, and a blank line separates it from the body.
    // Everything before that blank line beyond the first line is dropped rather than folded into the
    // subject — the model was told to write one line there, and a runaway subject in the commit box
    // is worse than a lost fragment. A reply with no blank line at all is a subject and no body.
    internal static CommitMessage? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) index++;
        if (index >= lines.Length) return null;

        var title = CleanTitle(lines[index]);
        if (title is null) return null;

        var blank = Array.FindIndex(lines, index + 1, line => line.Trim().Length == 0);
        if (blank < 0) return new CommitMessage(title, null);

        var body = string.Join('\n', lines[(blank + 1)..]).Trim();
        return new CommitMessage(title, body.Length == 0 ? null : body);
    }

    // The model was told to answer with the subject alone on that line; this is the guard for when it
    // labels or quotes it anyway, so a wrapper never reaches the commit box.
    private static string? CleanTitle(string raw)
    {
        var line = raw.Trim();
        if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
            line = line["Title:".Length..].Trim();

        line = line.Trim('`').Trim();
        if (line.Length >= 2 && line[0] == '"' && line[^1] == '"') line = line[1..^1].Trim();

        return line.Length == 0 ? null : line;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _busy.Dispose();
    }
}

/// <summary>
/// The gate for the commit-message run: filling the commit box is the thing the person pressed the
/// button for, so that one call goes through unasked. Anything else is told no rather than silently
/// allowed — there is nobody watching this run to ask, and the agent's allowed list should have kept
/// it from being offered in the first place.
/// </summary>
internal sealed class ApproveTheCommitBox : IToolApprovalGate
{
    public static readonly ApproveTheCommitBox Instance = new();

    private ApproveTheCommitBox() { }

    public Task<bool> RequestAsync(string toolName, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(toolName == SetCommitMessageTool.ToolName);
}
