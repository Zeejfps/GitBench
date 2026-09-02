using Xunit;

namespace GitBench.Lsp.Tests;

// What an error answer means. The one that matters is the server that is still building its index and
// says so with an error code: treated as failure, the feature looks broken for the first half minute
// of every Rust project, so "ask again" is a case of its own and not a flavour of Failed.
public sealed class LspErrorResponseTests
{
    private static async Task<LspResponse<Hover>> AnsweredWith(LspErrorCode code, string message = "no")
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();
        await fx.Server.ReplyError(asked.Id, code, message);
        return await pending;
    }

    [Theory]
    [InlineData(-32801, true)]  // ContentModified — what rust-analyzer answers while it indexes
    [InlineData(-32002, true)]  // ServerNotInitialized — asked before the handshake settled
    [InlineData(-32802, true)]  // ServerCancelled — the server dropped it for its own reasons
    [InlineData(-32603, false)] // InternalError
    [InlineData(-32601, false)] // MethodNotFound — this server does not do hover
    [InlineData(-32602, false)] // InvalidParams
    [InlineData(-32803, false)] // RequestFailed
    [InlineData(-1, false)]     // something this client has never heard of
    public async Task An_error_is_retryable_only_when_asking_again_could_work(int code, bool retryable)
    {
        var response = await AnsweredWith(new LspErrorCode(code));

        Assert.Equal(retryable, response is LspResponse<Hover>.Retryable);
    }

    [Fact]
    public async Task A_retryable_error_carries_the_code_and_message_the_server_gave()
    {
        var response = await AnsweredWith(LspErrorCode.ContentModified, "still indexing");

        var retryable = Assert.IsType<LspResponse<Hover>.Retryable>(response);
        Assert.Equal(LspErrorCode.ContentModified, retryable.Code);
        Assert.Equal("still indexing", retryable.Message);
    }

    [Fact]
    public async Task A_failure_carries_the_code_and_message_the_server_gave()
    {
        var response = await AnsweredWith(LspErrorCode.RequestFailed, "no such file");

        var failed = Assert.IsType<LspResponse<Hover>.Failed>(response);
        Assert.Equal(LspErrorCode.RequestFailed, failed.Code);
        Assert.Equal("no such file", failed.Message);
    }

    [Fact]
    public async Task An_answer_this_client_cannot_read_is_malformed_rather_than_a_failure()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();

        await fx.Server.ReplyOk(asked.Id, "42");

        var malformed = Assert.IsType<LspResponse<Hover>.Malformed>(await pending);
        Assert.False(string.IsNullOrWhiteSpace(malformed.Detail), "a malformed answer must say what was wrong with it");
    }

    [Fact]
    public async Task An_answer_this_client_cannot_read_costs_one_request_and_not_the_connection()
    {
        await using var fx = new LspFixture();
        var broken = fx.AskHover();
        var first = await fx.Server.NextRequest();
        await fx.Server.ReplyOk(first.Id, """{"contents":{"kind":"markdown"}}""");
        Assert.IsType<LspResponse<Hover>.Malformed>(await broken);

        var pending = fx.AskHover();
        var second = await fx.Server.NextRequest();
        await fx.Server.ReplyOk(second.Id, Wire.HoverJson("fine"));

        Assert.Equal("fine", Wire.TextOf(await pending));
    }

    [Fact]
    public async Task A_reply_with_no_result_at_all_reads_as_nothing_to_show()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();

        await fx.Server.Send($$"""{"jsonrpc":"2.0","id":{{ScriptedLspServer.IdJson(asked.Id)}}}""");

        Assert.IsType<Hover.None>(Wire.HoverOf(await pending));
    }
}
