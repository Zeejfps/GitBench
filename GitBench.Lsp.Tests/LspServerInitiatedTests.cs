using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

// The other direction. A server asks the client for configuration before it has finished starting, and
// it publishes diagnostics for the rest of the session. The rules here: a server request is always
// answered — with its own id, in its own form — a notification never is, and a client-side handler
// that misbehaves costs one message rather than the connection.
public sealed class LspServerInitiatedTests
{
    private static readonly string SomeConfigRequest = """{"items":[{"section":"rust-analyzer"}]}""";

    private static InboundReply Configured() =>
        new InboundReply.Ok(writer =>
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteBoolean("checkOnSave", true);
            writer.WriteEndObject();
            writer.WriteEndArray();
        });

    private static async Task<ClientMessage.Response> AnswerTo(RequestId id)
    {
        await using var fx = new LspFixture();
        fx.Client.Answer = (_, _) => Task.FromResult(Configured());

        await fx.Server.Ask(id, LspMethod.Configuration, SomeConfigRequest);
        return await fx.Server.NextResponse();
    }

    [Fact]
    public async Task A_server_request_is_answered_with_what_the_client_produced()
    {
        var response = await AnswerTo(new RequestId.Number(7));

        Assert.True(response.Result!.Value[0].GetProperty("checkOnSave").GetBoolean());
    }

    [Fact]
    public async Task A_numeric_id_is_answered_as_a_number()
    {
        var response = await AnswerTo(new RequestId.Number(7));

        Assert.Equal(new RequestId.Number(7), response.Id);
    }

    [Fact]
    public async Task A_string_id_is_answered_as_the_same_string()
    {
        // A server that asks with "cfg-1" and is answered with 0 waits forever.
        var response = await AnswerTo(new RequestId.Text("cfg-1"));

        Assert.Equal(new RequestId.Text("cfg-1"), response.Id);
    }

    [Fact]
    public async Task A_method_this_client_does_not_implement_is_still_answered()
    {
        await using var fx = new LspFixture();

        await fx.Server.Ask(new RequestId.Number(1), new LspMethod("window/showMessageRequest"), "{}");

        var response = await fx.Server.NextResponse();
        Assert.Equal(LspErrorCode.MethodNotFound.Value, response.ErrorCode);
    }

    [Fact]
    public async Task A_handler_that_throws_answers_with_an_error_and_reports_the_fault()
    {
        await using var fx = new LspFixture();
        fx.Client.Answer = (_, _) => throw new InvalidOperationException("the config file is gone");

        await fx.Server.Ask(new RequestId.Number(1), LspMethod.Configuration, SomeConfigRequest);

        Assert.Equal(LspErrorCode.InternalError.Value, (await fx.Server.NextResponse()).ErrorCode);
        Assert.IsType<LspFault.HandlerFailed>(await fx.Client.NextFault());
    }

    [Fact]
    public async Task A_notification_is_never_answered()
    {
        await using var fx = new LspFixture();

        await fx.Server.Notify(LspMethod.LogMessage, """{"type":3,"message":"indexing"}""");
        await fx.Server.Ask(new RequestId.Text("probe"), LspMethod.Configuration, SomeConfigRequest);

        // If the notification had drawn a reply, it would be sitting in front of this one.
        Assert.Equal(new RequestId.Text("probe"), (await fx.Server.NextResponse()).Id);
    }

    [Fact]
    public async Task A_server_request_the_client_is_slow_to_answer_does_not_hold_up_the_answers_behind_it()
    {
        await using var fx = new LspFixture();
        var stuck = new TaskCompletionSource();
        fx.Client.Answer = async (_, _) =>
        {
            await stuck.Task;
            return Configured();
        };

        await fx.Server.Ask(new RequestId.Number(1), LspMethod.Configuration, SomeConfigRequest);
        await fx.Client.NextServerRequest();

        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();
        await fx.Server.ReplyOk(asked.Id, Wire.HoverJson("through"));

        Assert.Equal("through", Wire.TextOf(await pending));
        stuck.SetResult();
    }

    [Fact]
    public async Task A_notification_this_client_does_not_model_arrives_whole()
    {
        await using var fx = new LspFixture();

        await fx.Server.Notify(new LspMethod("$/progress"), """{"token":"idx","value":{"kind":"begin"}}""");

        var other = Assert.IsType<ServerNotification.Other>(await fx.Client.NextNotification());
        Assert.Equal("$/progress", other.Method.Name);
        Assert.Equal("idx", other.Params.GetProperty("token").GetString());
    }

    [Fact]
    public async Task A_notification_that_does_not_parse_is_reported_and_the_next_one_still_arrives()
    {
        await using var fx = new LspFixture();

        await fx.Server.Notify(LspMethod.PublishDiagnostics, """{"diagnostics":[]}""");
        Assert.IsType<LspFault.UnreadableMessage>(await fx.Client.NextFault());

        await fx.Server.Notify(LspMethod.PublishDiagnostics, """{"uri":"file:///repo/a.rs","diagnostics":[]}""");
        Assert.IsType<ServerNotification.Diagnostics>(await fx.Client.NextNotification());
    }

    [Fact]
    public async Task A_request_this_client_sends_is_a_well_formed_jsonrpc_request()
    {
        await using var fx = new LspFixture();

        _ = fx.Connection.Send(LspRequests.Hover(LspFixture.SomeFile, LspPosition.At(12, 4)), LspFixture.Budget);

        var asked = await fx.Server.NextRequest();
        Assert.Equal(LspMethod.Hover.Name, asked.Method);
        Assert.Equal(LspFixture.SomeFile.Value, asked.Params.GetProperty("textDocument").GetProperty("uri").GetString());
        Assert.Equal(12, asked.Params.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(4, asked.Params.GetProperty("position").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task Document_text_survives_the_trip_whatever_is_in_it()
    {
        await using var fx = new LspFixture();
        const string source = "fn main() { /* héllo 世界 🚀 */ }\r\nlet x = \"\t\";\n";

        await fx.Connection.Notify(LspNotices.DidOpen(
            LspFixture.SomeFile, LanguageId.Of("rust"), new DocumentVersion(1), source));

        var sent = await fx.Server.NextNotification();
        Assert.Equal(source, sent.Params.GetProperty("textDocument").GetProperty("text").GetString());
    }
}
