using Xunit;

namespace GitBench.Lsp.Tests;

// Every way a question stops being worth waiting for: the caller withdrew it, the budget ran out, or
// the server went away. The rule underneath all of them is that a caller always gets an answer object
// — never an exception, and never a task that waits forever — and that a server left holding a
// request we no longer want is told to stop working on it.
public sealed class LspWithdrawalTests
{
    [Fact]
    public async Task A_request_inside_its_budget_is_still_waiting()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        await fx.Server.NextRequest();

        fx.Clock.Advance(LspFixture.Budget - TimeSpan.FromMilliseconds(1));

        Assert.False(pending.IsCompleted, "a request must not time out before its budget is spent");
    }

    [Fact]
    public async Task A_request_that_outlives_its_budget_times_out()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        await fx.Server.NextRequest();

        fx.Clock.Advance(LspFixture.Budget);

        Assert.Equal(LspFixture.Budget, Assert.IsType<LspResponse<Hover>.TimedOut>(await Wire.Answered(pending)).After);
    }

    [Fact]
    public async Task A_request_that_times_out_tells_the_server_to_stop_working_on_it()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();

        fx.Clock.Advance(LspFixture.Budget);
        await pending;

        var cancel = await fx.Server.NextNotification();
        Assert.Equal(LspMethod.CancelRequest.Name, cancel.Method);
        Assert.Equal(asked.Id, RequestId.Read(cancel.Params.GetProperty("id")));
    }

    [Fact]
    public async Task A_cancelled_request_ends_as_cancelled_rather_than_throwing()
    {
        await using var fx = new LspFixture();
        using var withdrawn = new CancellationTokenSource();
        var pending = fx.AskHoverAtLine(3, ct: withdrawn.Token);
        await fx.Server.NextRequest();

        await withdrawn.CancelAsync();

        Assert.IsType<LspResponse<Hover>.Cancelled>(await Wire.Answered(pending));
    }

    [Fact]
    public async Task A_cancelled_request_tells_the_server_to_stop_working_on_it()
    {
        await using var fx = new LspFixture();
        using var withdrawn = new CancellationTokenSource();
        var pending = fx.AskHoverAtLine(3, ct: withdrawn.Token);
        var asked = await fx.Server.NextRequest();

        await withdrawn.CancelAsync();
        await pending;

        var cancel = await fx.Server.NextNotification();
        Assert.Equal(LspMethod.CancelRequest.Name, cancel.Method);
        Assert.Equal(asked.Id, RequestId.Read(cancel.Params.GetProperty("id")));
    }

    [Fact]
    public async Task A_request_withdrawn_before_it_is_sent_never_reaches_the_server()
    {
        await using var fx = new LspFixture();
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        Assert.IsType<LspResponse<Hover>.Cancelled>(await fx.AskHoverAtLine(3, ct: withdrawn.Token));

        await fx.Connection.Notify(LspNotices.DidClose(LspFixture.SomeFile));
        Assert.Equal(LspMethod.DidClose.Name, (await fx.Server.NextNotification()).Method);
    }

    [Fact]
    public async Task A_late_answer_to_a_request_we_gave_up_on_is_dropped_quietly()
    {
        await using var fx = new LspFixture();
        var abandoned = fx.AskHover();
        var asked = await fx.Server.NextRequest();
        fx.Clock.Advance(LspFixture.Budget);
        await Wire.Answered(abandoned);
        await fx.Server.NextNotification();

        await fx.Server.ReplyOk(asked.Id, Wire.HoverJson("too late"));

        var pending = fx.AskHover();
        var second = await fx.Server.NextRequest();
        await fx.Server.ReplyOk(second.Id, Wire.HoverJson("fresh"));
        Assert.Equal("fresh", Wire.TextOf(await Wire.Answered(pending)));
        Assert.False(fx.Client.AnyFaultSeen, "a reply to a request we withdrew is expected, not a fault");
    }

    [Fact]
    public async Task A_server_that_says_it_cancelled_the_request_is_a_cancellation_not_a_failure()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        var asked = await fx.Server.NextRequest();

        await fx.Server.ReplyError(asked.Id, LspErrorCode.RequestCancelled, "cancelled");

        Assert.IsType<LspResponse<Hover>.Cancelled>(await Wire.Answered(pending));
    }

    // ---- the far end goes away ----

    [Fact]
    public async Task A_server_that_exits_ends_every_request_in_flight()
    {
        await using var fx = new LspFixture();
        var first = fx.AskHoverAtLine(1);
        var second = fx.AskHoverAtLine(2);
        await fx.Server.TakeRequests(2);

        fx.Server.Exit();

        Assert.IsType<LspResponse<Hover>.Disconnected>(await Wire.Answered(first));
        Assert.IsType<LspResponse<Hover>.Disconnected>(await Wire.Answered(second));
    }

    [Fact]
    public async Task A_server_that_dies_mid_message_reports_an_unclean_end()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        await fx.Server.NextRequest();

        await fx.Server.SendRaw("Content-Length: 200\r\n\r\n{\"partial\":");
        fx.Server.Exit();

        Assert.IsType<LspResponse<Hover>.Disconnected>(await Wire.Answered(pending));
        var ended = Assert.IsType<LspFault.ConnectionEnded>(await fx.Client.NextFault());
        Assert.False(ended.Clean, "an end inside a message is not a clean shutdown");
    }

    [Fact]
    public async Task A_server_that_cannot_be_framed_ends_the_connection_rather_than_hanging_a_request()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        await fx.Server.NextRequest();

        await fx.Server.SendRaw("Content-Length: not-a-number\r\n\r\n{}");

        Assert.IsType<LspResponse<Hover>.Disconnected>(await Wire.Answered(pending));
        Assert.Equal(FrameFault.UnreadableContentLength, Assert.IsType<LspFault.FramingFailed>(await fx.Client.NextFault()).Fault);
    }

    [Fact]
    public async Task A_payload_beyond_the_limit_ends_the_connection_rather_than_being_buffered()
    {
        await using var fx = new LspFixture(new LspFrameLimits(MaxPayloadBytes: 4096));
        var pending = fx.AskHover();
        await fx.Server.NextRequest();

        await fx.Server.SendRaw("Content-Length: 999999999\r\n\r\n");

        Assert.IsType<LspResponse<Hover>.Disconnected>(await Wire.Answered(pending));
        Assert.Equal(FrameFault.PayloadTooLarge, Assert.IsType<LspFault.FramingFailed>(await fx.Client.NextFault()).Fault);
    }

    [Fact]
    public async Task Closing_the_connection_ends_every_request_in_flight()
    {
        await using var fx = new LspFixture();
        var pending = fx.AskHover();
        await fx.Server.NextRequest();

        await fx.Connection.DisposeAsync();

        Assert.IsType<LspResponse<Hover>.Disconnected>(await Wire.Answered(pending));
    }

    [Fact]
    public async Task A_request_made_after_closing_is_refused_rather_than_queued()
    {
        await using var fx = new LspFixture();
        await fx.Connection.DisposeAsync();

        Assert.IsType<LspResponse<Hover>.Disconnected>(await Wire.Answered(fx.AskHover()));
    }
}
