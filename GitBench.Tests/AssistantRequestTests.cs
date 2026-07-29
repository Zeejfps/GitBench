using System.Text;
using System.Text.Json;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using Xunit;

namespace GitBench.Tests;

public sealed class AssistantRequestTests
{
    private static readonly string[] ThreeTools = { "alpha", "mid", "zeta" };

    private static readonly AssistantConnection Anthropic = AssistantConnection.Default;

    private static string Body(
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        AssistantConnection? connection = null) =>
        Encoding.UTF8.GetString(AnthropicRequestWriter.Write(turn, tools, connection ?? Anthropic));

    private static AssistantTurn Turn(ModelTier tier, params AssistantMessage[] messages) =>
        new(tier, "system prompt", messages);

    [Fact]
    public void Toolset_SortsByNameAndFiltersToTheAgentsAllowedList()
    {
        var toolset = AssistantToolset.Create(
            new IAssistantTool[] { new StubTool("zeta"), new StubTool("alpha"), new StubTool("denied"), new StubTool("mid") },
            ThreeTools);

        Assert.Equal(ThreeTools, toolset.Tools.Select(t => t.Name));
        Assert.Null(toolset.Find("denied"));
        Assert.NotNull(toolset.Find("mid"));
    }

    [Fact]
    public void ToolListSerialization_IsByteStableAcrossInputOrderings()
    {
        var turn = Turn(ModelTier.Chat, new AssistantMessage.User("hi"));

        var forward = AssistantToolset.Create(
            new IAssistantTool[] { new StubTool("alpha"), new StubTool("mid"), new StubTool("zeta") }, ThreeTools);
        var reversed = AssistantToolset.Create(
            new IAssistantTool[] { new StubTool("zeta"), new StubTool("mid"), new StubTool("alpha") }, ThreeTools);

        Assert.Equal(
            AnthropicRequestWriter.Write(turn, forward.Tools, Anthropic),
            AnthropicRequestWriter.Write(turn, reversed.Tools, Anthropic));

        // The writer sorts too, so an unsorted list can't silently invalidate the cache prefix.
        Assert.Equal(
            AnthropicRequestWriter.Write(turn, new IAssistantTool[] { new StubTool("zeta"), new StubTool("alpha") }, Anthropic),
            AnthropicRequestWriter.Write(turn, new IAssistantTool[] { new StubTool("alpha"), new StubTool("zeta") }, Anthropic));
    }

    [Fact]
    public void Request_CachesTheLastSystemBlockAndSendsNoSamplingParameters()
    {
        var body = Body(Turn(ModelTier.Chat, new AssistantMessage.User("hi")), Array.Empty<IAssistantTool>());
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("claude-opus-5", root.GetProperty("model").GetString());
        Assert.Equal(AssistantTurn.DefaultMaxTokens, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("top_p", out _));
        Assert.False(root.TryGetProperty("top_k", out _));
        Assert.False(root.TryGetProperty("thinking", out _));

        var system = root.GetProperty("system");
        var last = system[system.GetArrayLength() - 1];
        Assert.Equal("ephemeral", last.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public void RepoContext_IsAMidConversationSystemEntryOnChatAndAUserTurnOnQuick()
    {
        var messages = new AssistantMessage[]
        {
            new AssistantMessage.User("hi"),
            new AssistantMessage.RepoContext("branch: main"),
        };

        using var chat = JsonDocument.Parse(Body(Turn(ModelTier.Chat, messages), Array.Empty<IAssistantTool>()));
        var chatContext = chat.RootElement.GetProperty("messages")[1];
        Assert.Equal("system", chatContext.GetProperty("role").GetString());
        Assert.Equal("branch: main", chatContext.GetProperty("content").GetString());

        using var quick = JsonDocument.Parse(Body(Turn(ModelTier.Quick, messages), Array.Empty<IAssistantTool>()));
        var quickContext = quick.RootElement.GetProperty("messages")[1];
        Assert.Equal("user", quickContext.GetProperty("role").GetString());
        Assert.Equal("claude-haiku-4-5-20251001", quick.RootElement.GetProperty("model").GetString());
        Assert.False(quick.RootElement.TryGetProperty("fallbacks", out _));
    }

    // A chosen model answers the chat tier too, and Sonnet 5 rejects both of the parameters the
    // frontier models take. Sending them because the *tier* was Chat is what 400'd the turn.
    [Fact]
    public void AChatModelThatTakesNeitherOptionalParameter_IsSentNeither()
    {
        var sonnet = AssistantSettings
            .For(AssistantProviders.Anthropic.Id, "claude-sonnet-5")
            .Connect("sk-ant");

        var messages = new AssistantMessage[]
        {
            new AssistantMessage.User("hi"),
            new AssistantMessage.RepoContext("branch: main"),
        };

        using var body = JsonDocument.Parse(
            Body(Turn(ModelTier.Chat, messages), Array.Empty<IAssistantTool>(), sonnet));

        Assert.Equal("claude-sonnet-5", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("fallbacks", out _));

        // And the repo block falls back to the user turn rather than a system entry it would refuse.
        var context = body.RootElement.GetProperty("messages")[1];
        Assert.Equal("user", context.GetProperty("role").GetString());
        Assert.Equal("branch: main", context.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ToolResults_TravelAsOneUserMessageAndFlagErrors()
    {
        var messages = new AssistantMessage[]
        {
            new AssistantMessage.User("hi"),
            new AssistantMessage.Assistant(new[]
            {
                new AssistantContent.ToolUse("call_1", "alpha", AssistantTestJson.Element("""{"a":1}""")),
                new AssistantContent.ToolUse("call_2", "zeta", AssistantTestJson.Empty),
            }),
            new AssistantMessage.ToolResults(new[]
            {
                new AssistantToolResult("call_1", "ok", false),
                new AssistantToolResult("call_2", "boom", true),
            }),
        };

        using var document = JsonDocument.Parse(Body(Turn(ModelTier.Chat, messages), Array.Empty<IAssistantTool>()));
        var wire = document.RootElement.GetProperty("messages");

        Assert.Equal(3, wire.GetArrayLength());
        var results = wire[2];
        Assert.Equal("user", results.GetProperty("role").GetString());
        var content = results.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.False(content[0].TryGetProperty("is_error", out _));
        Assert.True(content[1].GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public void GeneralAgent_LoadsFromTheEmbeddedPrompt()
    {
        var catalog = AgentCatalog.LoadEmbedded();
        var agent = catalog.Get(AgentCatalog.GeneralAgent);

        Assert.Equal(ModelTier.Chat, agent.Tier);
        Assert.NotEmpty(agent.SystemPrompt);
        Assert.DoesNotContain("---", agent.SystemPrompt);
        Assert.Equal(
            new[]
            {
                "commit", "create_tag", "get_branches", "get_commit_details", "get_commit_history",
                "get_diff", "get_file_at_base", "get_local_changes", "get_review_diff",
                "get_review_stack", "get_status", "mark_viewed", "push_tag", "read_file",
                "set_commit_message", "stage_files", "unstage_files",
            },
            agent.AllowedTools.OrderBy(t => t, StringComparer.Ordinal));
    }
}
