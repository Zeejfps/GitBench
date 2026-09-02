using Xunit;

namespace GitBench.Lsp.Tests;

// Which answer belongs to which question. A language server answers when it feels like it, in any
// order, sometimes twice, sometimes for a question nobody asked — so these pin that every caller gets
// its own answer and that nothing arriving out of turn is either misdelivered or silently eaten.
public sealed class LspRequestMatchingTests
{
    [Fact]
    public async Task Answers_arriving_backwards_still_reach_the_caller_that_asked()
    {
        await using var fx = new LspFixture();

        var first = fx.AskHoverAtLine(1);
        var firstAsked = await fx.Server.NextRequest();
        var second = fx.AskHoverAtLine(2);
        var secondAsked = await fx.Server.NextRequest();

        await fx.Server.ReplyOk(secondAsked.Id, Wire.HoverJson("second"));
        await fx.Server.ReplyOk(firstAsked.Id, Wire.HoverJson("first"));

        Assert.Equal("first", Wire.TextOf(await first));
        Assert.Equal("second", Wire.TextOf(await second));
    }

    [Fact]
    public async Task Requests_in_flight_together_carry_distinct_ids()
    {
        await using var fx = new LspFixture();

        _ = fx.AskHoversAtLines(50);
        var asked = await fx.Server.TakeRequests(50);

        Assert.Equal(50, asked.Select(request => request.Id).Distinct().Count());
    }

    [Fact]
    public async Task Fifty_questions_answered_in_reverse_all_land_on_the_right_caller()
    {
        await using var fx = new LspFixture();

        var pending = fx.AskHoversAtLines(50);
        var asked = await fx.Server.TakeRequests(50);
        await fx.Server.ReplyInReverse(asked, request => Wire.HoverJson(Wire.LineOf(request).ToString()));

        var answers = await Task.WhenAll(pending);
        Assert.Equal(Enumerable.Range(0, 50).Select(line => line.ToString()), answers.Select(Wire.TextOf));
    }

    [Fact]
    public async Task An_answer_to_an_id_nobody_asked_for_is_reported()
    {
        await using var fx = new LspFixture();

        await fx.Server.ReplyOk(new RequestId.Number(4242), Wire.HoverJson("ghost"));

        var fault = Assert.IsType<LspFault.UnmatchedResponse>(await fx.Client.NextFault());
        Assert.Equal(new RequestId.Number(4242), fault.Id);
    }

    [Fact]
    public async Task An_answer_to_an_id_nobody_asked_for_does_not_break_the_connection()
    {
        await using var fx = new LspFixture();
        await fx.Server.ReplyOk(new RequestId.Text("who?"), Wire.HoverJson("ghost"));
        await fx.Client.NextFault();

        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();
        await fx.Server.ReplyOk(asked.Id, Wire.HoverJson("real"));

        Assert.Equal("real", Wire.TextOf(await pending));
    }

    [Fact]
    public async Task A_second_answer_to_the_same_question_is_reported_rather_than_delivered()
    {
        await using var fx = new LspFixture();

        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();
        await fx.Server.ReplyOk(asked.Id, Wire.HoverJson("once"));
        Assert.Equal("once", Wire.TextOf(await pending));

        await fx.Server.ReplyOk(asked.Id, Wire.HoverJson("twice"));

        Assert.Equal(asked.Id, Assert.IsType<LspFault.UnmatchedResponse>(await fx.Client.NextFault()).Id);
    }

    [Fact]
    public async Task A_message_that_is_not_json_is_reported_and_the_next_one_is_still_read()
    {
        await using var fx = new LspFixture();

        await fx.Server.Send("this is not json at all");
        Assert.IsType<LspFault.UnreadableMessage>(await fx.Client.NextFault());

        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();
        await fx.Server.ReplyOk(asked.Id, Wire.HoverJson("still here"));
        Assert.Equal("still here", Wire.TextOf(await pending));
    }

    [Fact]
    public async Task A_storm_of_notifications_neither_reorders_them_nor_delays_an_answer()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();

        await fx.Server.NotifyMany(
            LspMethod.PublishDiagnostics,
            i => $$"""{"uri":"file:///repo/f{{i}}.rs","diagnostics":[]}""",
            500);
        await fx.Server.ReplyOk(asked.Id, Wire.HoverJson("answered"));

        var seen = await fx.Client.TakeNotifications(500);
        Assert.Equal("file:///repo/f0.rs", Uri(seen[0]));
        Assert.Equal("file:///repo/f250.rs", Uri(seen[250]));
        Assert.Equal("file:///repo/f499.rs", Uri(seen[499]));
        Assert.Equal("answered", Wire.TextOf(await pending));
    }

    private static string Uri(ServerNotification notification) =>
        Assert.IsType<ServerNotification.Diagnostics>(notification).Uri.Value;
}
