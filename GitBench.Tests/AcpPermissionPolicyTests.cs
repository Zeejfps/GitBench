using System.Text.Json.Nodes;
using GitBench.Features.AgentConnections.Acp;
using Xunit;

namespace GitBench.Tests;

/// <summary>The ACP write guard, over permission requests as the three adapters really send them
/// (captured by the Step 1 spike): file writes put to the user, reads and shell allowed, the app's own MCP tools
/// allowed however each adapter names them, anything else left to the user.</summary>
public sealed class AcpPermissionPolicyTests
{
    private const string Own = "diffdino";
    private static readonly IReadOnlyDictionary<string, string> NoneAnnounced = new Dictionary<string, string>();

    private static AcpPermissionDecision Decide(string json, IReadOnlyDictionary<string, string>? announced = null)
    {
        var request = AcpPermissionPolicy.Parse(JsonNode.Parse(json), announced ?? NoneAnnounced);
        Assert.NotNull(request);
        return AcpPermissionPolicy.Decide(request, Own);
    }

    private static void AssertSelected(AcpPermissionDecision decision, string optionId, AcpPermissionVerdict verdict)
    {
        var select = Assert.IsType<AcpPermissionDecision.Select>(decision);
        Assert.Equal(optionId, select.OptionId);
        Assert.Equal(verdict, select.Verdict);
    }

    private const string ClaudeOptions =
        """[{"optionId":"allow-once","name":"Yes","kind":"allow_once"},{"optionId":"allow-with-updates","name":"Yes, always","kind":"allow_always"},{"optionId":"reject","name":"No","kind":"reject_once"}]""";

    [Fact]
    public void ClaudeWrite_IsPutToTheUser()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","name":"Write","title":"Write plan.md","kind":"edit"},"options":
            """ + ClaudeOptions + "}");
        Assert.IsType<AcpPermissionDecision.AskUser>(decision);
    }

    [Fact]
    public void ClaudeShell_IsAllowedOnce_SoTheAgentRunsTheTests()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","title":"dotnet test","kind":"execute"},"options":
            """ + ClaudeOptions + "}");
        AssertSelected(decision, "allow-once", AcpPermissionVerdict.Allowed);
    }

    [Fact]
    public void ClaudeOwnMcpTool_IsAllowedOnce_SoNothingIsWrittenIntoTheRepository()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","name":"mcp__diffdino__pairing_wait","title":"mcp__diffdino__pairing_wait","kind":"other",
             "_meta":{"claudeCode":{"toolName":"mcp__diffdino__pairing_wait","mcpServer":{"name":"diffdino","source":"dynamic"}}}},"options":
            """ + ClaudeOptions + "}");
        AssertSelected(decision, "allow-once", AcpPermissionVerdict.Allowed);
    }

    [Fact]
    public void ClaudeOtherMcpTool_IsLeftToTheUser()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","name":"mcp__github__create_issue","title":"mcp__github__create_issue","kind":"other"},"options":
            """ + ClaudeOptions + "}");
        Assert.IsType<AcpPermissionDecision.AskUser>(decision);
    }

    [Fact]
    public void CodexMcpApproval_ForTheAnnouncedOwnServer_IsAllowed()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"exec-1","kind":"execute","status":"pending"},"_meta":{"is_mcp_tool_approval":true},
             "options":[{"optionId":"allow_once","name":"Allow","kind":"allow_once"},{"optionId":"allow_session","name":"Allow for this session","kind":"allow_always"},
                        {"optionId":"allow_always","name":"Always allow","kind":"allow_always"},{"optionId":"cancel","name":"Cancel","kind":"reject_once"}]}
            """, new Dictionary<string, string> { ["exec-1"] = "diffdino" });
        AssertSelected(decision, "allow_once", AcpPermissionVerdict.Allowed);
    }

    [Fact]
    public void CodexMcpApproval_ForAnotherServer_IsLeftToTheUser_NotTakenForAShellCommand()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"exec-1","kind":"execute","status":"pending"},"_meta":{"is_mcp_tool_approval":true},
             "options":[{"optionId":"allow_once","name":"Allow","kind":"allow_once"},{"optionId":"cancel","name":"Cancel","kind":"reject_once"}]}
            """, new Dictionary<string, string> { ["exec-1"] = "filesystem" });
        Assert.IsType<AcpPermissionDecision.AskUser>(decision);
    }

    [Fact]
    public void CodexEdit_IsPutToTheUser()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"exec-2","kind":"edit","status":"pending","title":"Edit files"},
             "options":[{"optionId":"allow_once","name":"Yes, proceed","kind":"allow_once"},{"optionId":"cancel","name":"No","kind":"reject_once"}]}
            """);
        Assert.IsType<AcpPermissionDecision.AskUser>(decision);
    }

    [Fact]
    public void CodexRead_IsAllowedOnce()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"exec-3","kind":"read","status":"pending","title":"Read file"},
             "options":[{"optionId":"allow_once","name":"Yes, proceed","kind":"allow_once"},{"optionId":"cancel","name":"No","kind":"reject_once"}]}
            """);
        AssertSelected(decision, "allow_once", AcpPermissionVerdict.Allowed);
    }

    [Fact]
    public void GeminiOwnMcpTool_IsRecognisedByItsServerOption_AndAllowedOnce()
    {
        var decision = Decide("""
            {"sessionId":"s","options":[{"optionId":"proceed_always_server","name":"Always Allow diffdino","kind":"allow_always"},
              {"optionId":"proceed_always_tool","name":"Always Allow pairing_wait","kind":"allow_always"},
              {"optionId":"proceed_once","name":"Allow","kind":"allow_once"},{"optionId":"cancel","name":"Reject","kind":"reject_once"}],
             "toolCall":{"toolCallId":"call_1","status":"pending","title":"{}","content":[],"locations":[],"kind":"other"}}
            """);
        AssertSelected(decision, "proceed_once", AcpPermissionVerdict.Allowed);
    }

    [Fact]
    public void Delete_IsPutToTheUser()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t","kind":"delete"},"options":[{"optionId":"ok","name":"Yes","kind":"allow_once"}]}
            """);
        Assert.IsType<AcpPermissionDecision.AskUser>(decision);
    }

    [Fact]
    public void Formatter_IsAllowedOnce()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","title":"`dotnet format`","kind":"execute","rawInput":{"command":"dotnet format"}},"options":
            """ + ClaudeOptions + "}");
        AssertSelected(decision, "allow-once", AcpPermissionVerdict.Allowed);
    }

    [Fact]
    public void RequestWithoutToolCall_DoesNotParse()
    {
        Assert.Null(AcpPermissionPolicy.Parse(JsonNode.Parse("""{"sessionId":"s","options":[]}"""), NoneAnnounced));
    }

    [Fact]
    public void AnnouncedServer_ReadsCodexAndClaudeMarkers()
    {
        Assert.Equal("diffdino", AcpPermissionPolicy.AnnouncedServer(JsonNode.Parse("""
            {"sessionUpdate":"tool_call","toolCallId":"x","rawInput":{"server":"diffdino","tool":"pairing_wait"},"_meta":{"is_mcp_tool_call":true}}
            """)));
        Assert.Equal("diffdino", AcpPermissionPolicy.AnnouncedServer(JsonNode.Parse("""
            {"sessionUpdate":"tool_call","toolCallId":"x","_meta":{"claudeCode":{"mcpServer":{"name":"diffdino"}}}}
            """)));
        Assert.Null(AcpPermissionPolicy.AnnouncedServer(JsonNode.Parse("""{"sessionUpdate":"tool_call","toolCallId":"x"}""")));
    }

    [Fact]
    public void AnMcpApproval_NamingNoServer_IsAskedOfTheUser()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"exec-9","kind":"execute","status":"pending"},"_meta":{"is_mcp_tool_approval":true},
             "options":[{"optionId":"allow_once","name":"Allow","kind":"allow_once"},{"optionId":"cancel","name":"Cancel","kind":"reject_once"}]}
            """);
        Assert.IsType<AcpPermissionDecision.AskUser>(decision);
    }

    [Fact]
    public void ClaudePush_IsPutToTheUser()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","title":"`git push origin main`","kind":"execute","rawInput":{"command":"git push origin main"}},"options":
            """ + ClaudeOptions + "}");
        Assert.IsType<AcpPermissionDecision.AskUser>(decision);
    }

    [Fact]
    public void CodexPush_InAnArgv_IsPutToTheUser()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","title":"Run command","kind":"execute","rawInput":{"command":["bash","-lc","git add -A && git commit -m wip && git push"]}},"options":
            """ + ClaudeOptions + "}");
        Assert.IsType<AcpPermissionDecision.AskUser>(decision);
    }

    [Fact]
    public void Commit_IsAllowedOnce()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","title":"`git commit -m \"Add retry\"`","kind":"execute","rawInput":{"command":"git commit -m \"Add retry\""}},"options":
            """ + ClaudeOptions + "}");
        AssertSelected(decision, "allow-once", AcpPermissionVerdict.Allowed);
    }

    [Theory]
    [InlineData("git push", true)]
    [InlineData("git -C ../repo push --force-with-lease", true)]
    [InlineData("git --no-pager -c push.default=current push", true)]
    [InlineData("cd src && GIT push", true)]
    [InlineData("git.exe push origin HEAD", true)]
    [InlineData("git status && git log --oneline -5", false)]
    [InlineData("git commit -m \"push the retry\"", false)]
    [InlineData("git stash push -m wip", false)]
    [InlineData("legit push", false)]
    public void Pushes_FindsGitPush(string command, bool pushes) =>
        Assert.Equal(pushes, AcpPermissionPolicy.Pushes(command));

    private static AcpPermissionRequest Parse(string toolCall)
    {
        var request = AcpPermissionPolicy.Parse(JsonNode.Parse("""{"sessionId":"s","toolCall":""" + toolCall + ""","options":[]}"""), NoneAnnounced);
        Assert.NotNull(request);
        return request;
    }

    [Fact]
    public void DiffContent_IsTheEdit()
    {
        var request = Parse("""
            {"toolCallId":"t1","title":"Edit a.md","kind":"edit","locations":[{"path":"C:/r/a.md"}],
             "content":[{"type":"diff","path":"C:/r/a.md","oldText":"x","newText":"y"}]}
            """);
        Assert.Equal([new AcpFileEdit("C:/r/a.md", "x", "y")], request.Edits);
        Assert.Equal(["C:/r/a.md"], request.Paths);
    }

    [Fact]
    public void ClaudeEditInput_IsTheEdit_WhereNoDiffIsSent()
    {
        var request = Parse("""
            {"toolCallId":"t1","title":"Edit a.md","kind":"edit","rawInput":{"file_path":"C:/r/a.md","old_string":"x","new_string":"y"}}
            """);
        Assert.Equal([new AcpFileEdit("C:/r/a.md", "x", "y")], request.Edits);
        Assert.Equal(["C:/r/a.md"], request.Paths);
    }

    [Fact]
    public void ClaudeWriteInput_ReplacesTheWholeFile()
    {
        var request = Parse("""
            {"toolCallId":"t1","title":"Write a.md","kind":"edit","rawInput":{"file_path":"C:/r/a.md","content":"all"}}
            """);
        Assert.Equal([new AcpFileEdit("C:/r/a.md", null, "all")], request.Edits);
    }

    [Fact]
    public void ClaudeMultiEditInput_IsEachEdit()
    {
        var request = Parse("""
            {"toolCallId":"t1","title":"Edit a.md","kind":"edit","rawInput":{"file_path":"a.md",
             "edits":[{"old_string":"1","new_string":"2"},{"old_string":"3","new_string":"4"}]}}
            """);
        Assert.Equal([new AcpFileEdit("a.md", "1", "2"), new AcpFileEdit("a.md", "3", "4")], request.Edits);
    }

    [Fact]
    public void ShellCall_HasNoEdits()
    {
        var request = Parse("""{"toolCallId":"t1","title":"ls","kind":"execute","rawInput":{"command":"ls"}}""");
        Assert.Empty(request.Edits);
        Assert.Empty(request.Paths);
    }
}
