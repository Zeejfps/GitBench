namespace GitBench.Lsp;

/// <summary>
/// How long a caller keeps asking a server that answered "ask again". Every server measured
/// refuses requests for as long as it is still building its index — rust-analyzer for around
/// thirty seconds — and a single question asked once during that window is a question that
/// silently produced nothing.
/// </summary>
public sealed record AskAgainPolicy
{
    public static readonly AskAgainPolicy Default = new();

    /// <summary>Total questions asked, the first one included.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>The wait after the first refusal.</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Multiplier applied to the wait after each further refusal.</summary>
    public double DelayGrowth { get; init; } = 2.0;

    /// <summary>Ceiling on the growing wait.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(4);

    /// <param name="refusals">How many refusals have been heard, counting from one.</param>
    public TimeSpan DelayAfter(int refusals)
    {
        var delay = FirstDelay * Math.Pow(DelayGrowth, Math.Max(0, refusals - 1));
        return delay > MaxDelay ? MaxDelay : delay;
    }
}

/// <summary>
/// Asking a question again while the server keeps saying it is not ready to answer it.
/// </summary>
public static class AskAgain
{
    /// <summary>
    /// Asks until the server says something other than "ask again", the attempts run out, or the
    /// caller withdraws the question. Every other answer — an error, a timeout, a dropped
    /// connection — comes straight back: those do not improve by being asked twice.
    /// </summary>
    public static async Task<LspResponse<T>> AskAsync<T>(
        Func<CancellationToken, Task<LspResponse<T>>> ask,
        AskAgainPolicy policy,
        Func<TimeSpan, CancellationToken, Task> wait,
        CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (cancel.IsCancellationRequested) return new LspResponse<T>.Cancelled();

            var response = await ask(cancel).ConfigureAwait(false);
            if (response is not LspResponse<T>.Retryable) return response;
            if (attempt >= policy.MaxAttempts) return response;

            try
            {
                await wait(policy.DelayAfter(attempt), cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new LspResponse<T>.Cancelled();
            }
        }
    }
}
