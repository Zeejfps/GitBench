using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The commit bar's "Review changes": the agent the catalog picks up, and where its answer lands.
/// </summary>
public sealed class AssistantReviewActionTests
{
    // The review covers the uncommitted work as well as the branch's commits, so the working-tree
    // reads are part of the list rather than an afterthought: on the default branch with nothing
    // ahead of the base, they are the only thing there is to read.
    private static readonly string[] ReviewTools =
    [
        "find_files", "get_commit_details", "get_diff", "get_file_at_base", "get_local_changes",
        "get_review_diff", "get_review_stack", "get_status", "read_file",
    ];

    // Adding an agent is adding a file, so what matters is that the shipped .md is picked up with the
    // tool list it declares — and that a review, which is asked for on someone else's work in
    // progress, cannot change anything.
    [Fact]
    public void ReviewAgent_LoadsFromTheEmbeddedPromptAndReadsOnly()
    {
        var agent = AgentCatalog.LoadEmbedded().Get(AgentCatalog.ReviewBranchAgent);

        Assert.Equal(ModelTier.Chat, agent.Tier);
        Assert.NotEmpty(agent.SystemPrompt);
        Assert.DoesNotContain("---", agent.SystemPrompt);
        Assert.Equal(ReviewTools, agent.AllowedTools.OrderBy(t => t, StringComparer.Ordinal));
    }

    // The allowed list is only half of it: the toolset built for the preset is what the model is
    // actually offered, and it is built from the reads alone.
    [Fact]
    public void ReviewToolset_CarriesTheReviewReadsAndNoWrites()
    {
        using var dir = new TempDir("gitbench-review-action-");
        var agent = AgentCatalog.LoadEmbedded().Get(AgentCatalog.ReviewBranchAgent);
        var toolset = AssistantToolset.ForRepo(
            new GitService(new NullActivityTracker()), new Repo(Guid.NewGuid(), dir.Path, "repo"), agent);

        Assert.Equal(ReviewTools, toolset.Tools.Select(t => t.Name));
        Assert.DoesNotContain(toolset.Tools, t => t.IsWrite);
    }

    [Fact]
    public void TheReviewItem_OpensTheOverlayAndAnswersInTheTranscript()
    {
        var backend = Answering("The branch renames the runner and nothing looks wrong.");
        using var fixture = new AssistantViewFixture(backend);
        Assert.False(fixture.Vm.IsOpen.Value);

        Review(fixture);

        Assert.True(fixture.Vm.IsOpen.Value);
        var rows = fixture.Vm.Session.Value!.Rows;
        Assert.Equal(AssistantRowKind.User, rows[0].Kind);
        Assert.Contains("uncommitted", rows[0].Text.Value, StringComparison.Ordinal);
        Assert.Contains("checked-out branch", rows[0].Text.Value, StringComparison.Ordinal);
        Assert.Equal(AssistantRowKind.Reply, rows[1].Kind);
        Assert.Equal("The branch renames the runner and nothing looks wrong.", rows[1].Text.Value);

        var expected = AgentCatalog.LoadEmbedded().Get(AgentCatalog.ReviewBranchAgent).SystemPrompt;
        Assert.Equal(expected, Assert.Single(backend.Requests).SystemPrompt);
    }

    // A review is a one-shot like the diff's presets: the next thing typed is not a follow-up to a
    // whole branch read of the model's own choosing.
    [Fact]
    public void TheReviewItem_DoesNotJoinTheRepositorysThread()
    {
        var backend = Answering("Nothing looks wrong.", "It is on main.");
        using var fixture = new AssistantViewFixture(backend);

        Review(fixture);
        fixture.Ask("which branch am I on?");

        var second = backend.Requests[1];
        var user = Assert.Single(second.Messages.OfType<AssistantMessage.User>());
        Assert.Equal("which branch am I on?", user.Text);
    }

    private static void Review(AssistantViewFixture fixture)
    {
        var items = fixture.Vm.BuildCommitMenu();
        Assert.Equal("Review changes", items[1].Label);
        items[1].OnSelected();
        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the review turn to finish");
        fixture.Frames();
    }

    private static FakeAssistantBackend Answering(params string[] answers) =>
        new(answers.Select(text => new BackendEvent[]
        {
            new BackendEvent.TextDelta(text),
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        }).ToArray());
}
