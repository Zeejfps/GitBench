using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// Asking again while the server says it is not ready. Observed live: the first hover after a cold
/// start returned nothing at all, because a refusal that means "ask again" was read as "no answer"
/// and nobody ever asked again.
/// </summary>
public sealed class AskAgainTests
{
    readonly List<TimeSpan> _waited = [];

    Task Wait(TimeSpan delay, CancellationToken cancel)
    {
        _waited.Add(delay);
        return cancel.IsCancellationRequested ? Task.FromCanceled(cancel) : Task.CompletedTask;
    }

    static LspResponse<string> Ok(string value) => new LspResponse<string>.Ok(value);

    static LspResponse<string> NotReady() =>
        new LspResponse<string>.Retryable(LspErrorCode.ServerNotInitialized, "still indexing");

    Task<LspResponse<string>> AskAsync(
        IEnumerable<LspResponse<string>> answers,
        AskAgainPolicy? policy = null,
        CancellationToken cancel = default)
    {
        var queue = new Queue<LspResponse<string>>(answers);
        Asked = 0;
        return AskAgain.AskAsync<string>(
            _ =>
            {
                Asked++;
                return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : NotReady());
            },
            policy ?? AskAgainPolicy.Default,
            Wait,
            cancel);
    }

    int Asked { get; set; }

    [Fact]
    public async Task AnAnswerOnTheFirstAskIsTheAnswer()
    {
        var response = await AskAsync([Ok("hover")]);

        Assert.Equal("hover", Assert.IsType<LspResponse<string>.Ok>(response).Value);
        Assert.Equal(1, Asked);
        Assert.Empty(_waited);
    }

    // The bug this whole class exists for: one refusal, then an answer.
    [Fact]
    public async Task ARefusalIsAskedAgainAndTheSecondAnswerIsReturned()
    {
        var response = await AskAsync([NotReady(), Ok("hover")]);

        Assert.Equal("hover", Assert.IsType<LspResponse<string>.Ok>(response).Value);
        Assert.Equal(2, Asked);
        Assert.Single(_waited);
    }

    [Fact]
    public async Task TheWaitGrowsWithEachRefusal()
    {
        var policy = new AskAgainPolicy
        {
            MaxAttempts = 4,
            FirstDelay = TimeSpan.FromMilliseconds(100),
            DelayGrowth = 2,
            MaxDelay = TimeSpan.FromSeconds(10),
        };

        await AskAsync([NotReady(), NotReady(), NotReady(), Ok("hover")], policy);

        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)],
            _waited);
    }

    [Fact]
    public async Task TheWaitStopsGrowingAtItsCeiling()
    {
        var policy = new AskAgainPolicy
        {
            MaxAttempts = 4,
            FirstDelay = TimeSpan.FromMilliseconds(100),
            DelayGrowth = 10,
            MaxDelay = TimeSpan.FromMilliseconds(500),
        };

        await AskAsync([NotReady(), NotReady(), NotReady(), Ok("hover")], policy);

        Assert.All(_waited, delay => Assert.True(delay <= TimeSpan.FromMilliseconds(500), delay.ToString()));
        Assert.Equal(TimeSpan.FromMilliseconds(500), _waited[^1]);
    }

    // A server that is still indexing after the last attempt is not a failure to report as one:
    // the refusal comes back as itself, and the pane already says the server is not ready.
    [Fact]
    public async Task AskingStopsAtTheAttemptLimitAndTheLastRefusalComesBack()
    {
        var policy = AskAgainPolicy.Default with { MaxAttempts = 3 };

        var response = await AskAsync([NotReady(), NotReady(), NotReady(), Ok("never reached")], policy);

        Assert.IsType<LspResponse<string>.Retryable>(response);
        Assert.Equal(3, Asked);
        Assert.Equal(2, _waited.Count);
    }

    [Fact]
    public async Task OneAttemptMeansNoRetryAtAll()
    {
        var response = await AskAsync([NotReady(), Ok("never reached")], AskAgainPolicy.Default with { MaxAttempts = 1 });

        Assert.IsType<LspResponse<string>.Retryable>(response);
        Assert.Equal(1, Asked);
        Assert.Empty(_waited);
    }

    public static TheoryData<LspResponse<string>> NotWorthAskingTwice() =>
    [
        new LspResponse<string>.Failed(LspErrorCode.MethodNotFound, "no such method"),
        new LspResponse<string>.Malformed("not a hover"),
        new LspResponse<string>.TimedOut(TimeSpan.FromSeconds(5)),
        new LspResponse<string>.Cancelled(),
        new LspResponse<string>.Disconnected("the server ended"),
    ];

    [Theory]
    [MemberData(nameof(NotWorthAskingTwice))]
    public async Task EverythingThatIsNotARefusalComesStraightBack(LspResponse<string> answer)
    {
        var response = await AskAsync([answer, Ok("never reached")]);

        Assert.Same(answer, response);
        Assert.Equal(1, Asked);
        Assert.Empty(_waited);
    }

    [Fact]
    public async Task AQuestionWithdrawnBeforeItIsAskedIsNeverAsked()
    {
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        var response = await AskAsync([Ok("hover")], cancel: withdrawn.Token);

        Assert.IsType<LspResponse<string>.Cancelled>(response);
        Assert.Equal(0, Asked);
    }

    // The pointer moving on is the common case, and it happens between asks more often than during
    // one: nothing may be shown for a symbol the reader has already left.
    [Fact]
    public async Task AQuestionWithdrawnWhileWaitingIsNotAskedAgain()
    {
        using var withdrawn = new CancellationTokenSource();
        var asked = 0;

        var response = await AskAgain.AskAsync<string>(
            _ =>
            {
                asked++;
                withdrawn.Cancel();
                return Task.FromResult<LspResponse<string>>(NotReady());
            },
            AskAgainPolicy.Default,
            Wait,
            withdrawn.Token);

        Assert.IsType<LspResponse<string>.Cancelled>(response);
        Assert.Equal(1, asked);
    }
}
