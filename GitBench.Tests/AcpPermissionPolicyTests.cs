using System.Text.Json.Nodes;
using GitBench.Features.AgentConnections.Acp;
using Xunit;

namespace GitBench.Tests;

/// <summary>The ACP write guard, over permission requests as the three adapters really send them
/// (captured by the Step 1 spike): writes and shell refused, reads allowed, the app's own MCP tools
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
    public void ClaudeWrite_IsRejected()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","name":"Write","title":"Write created.txt","kind":"edit"},"options":
            """ + ClaudeOptions + "}");
        AssertSelected(decision, "reject", AcpPermissionVerdict.Rejected);
    }

    [Fact]
    public void ClaudeShell_IsRejected()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t1","title":"git commit","kind":"execute"},"options":
            """ + ClaudeOptions + "}");
        AssertSelected(decision, "reject", AcpPermissionVerdict.Rejected);
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
    public void CodexMcpApproval_ForAnotherServer_IsJudgedByKind()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"exec-1","kind":"execute","status":"pending"},"_meta":{"is_mcp_tool_approval":true},
             "options":[{"optionId":"allow_once","name":"Allow","kind":"allow_once"},{"optionId":"cancel","name":"Cancel","kind":"reject_once"}]}
            """, new Dictionary<string, string> { ["exec-1"] = "filesystem" });
        AssertSelected(decision, "cancel", AcpPermissionVerdict.Rejected);
    }

    [Fact]
    public void CodexEdit_IsRejected()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"exec-2","kind":"edit","status":"pending","title":"Edit files"},
             "options":[{"optionId":"allow_once","name":"Yes, proceed","kind":"allow_once"},{"optionId":"cancel","name":"No","kind":"reject_once"}]}
            """);
        AssertSelected(decision, "cancel", AcpPermissionVerdict.Rejected);
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
    public void RejectionWithNoRejectOption_Cancels()
    {
        var decision = Decide("""
            {"sessionId":"s","toolCall":{"toolCallId":"t","kind":"delete"},"options":[{"optionId":"ok","name":"Yes","kind":"allow_once"}]}
            """);
        Assert.IsType<AcpPermissionDecision.Cancel>(decision);
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
}
