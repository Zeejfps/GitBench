using GitBench.Features.CodeIntel;

using TreeSitter;

using Xunit;

namespace GitBench.Tests;

[Collection(nameof(CodeIntelCollection))]
public class ParseSessionPoolTests
{
    private static Language CSharp() => Language.Load("tree-sitter-grammars", "c_sharp");

    [Fact]
    public void ASessionIsReusedAcrossCalls()
    {
        using var pool = new ParseSessionPool(CSharp(), 1);

        var first = pool.Use(0, static (session, _) => session);
        var second = pool.Use(0, static (session, _) => session);

        Assert.Same(first, second);
    }

    [Fact]
    public void ASessionWhoseWorkThrewIsNotHandedOutAgain()
    {
        using var pool = new ParseSessionPool(CSharp(), 1);

        var poisoned = pool.Use(0, static (session, _) => session);

        Assert.Throws<InvalidOperationException>(() =>
            pool.Use<ParseSession, ParseSession>(poisoned, static (session, expected) =>
            {
                Assert.Same(expected, session);
                throw new InvalidOperationException("boom");
            }));

        Assert.NotSame(poisoned, pool.Use(0, static (session, _) => session));
    }

    [Fact]
    public void ThePoolIsBoundedAndTheOverflowWaits()
    {
        using var pool = new ParseSessionPool(CSharp(), 1);
        using var occupied = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();

        var holder = Task.Run(() => pool.Use(0, (_, _) =>
        {
            occupied.Set();
            release.Wait();
            return 0;
        }));

        Assert.True(occupied.Wait(TimeSpan.FromSeconds(5)));

        var waiter = Task.Run(() => pool.Use(0, (_, _) =>
        {
            entered.Set();
            return 0;
        }));

        Assert.False(entered.Wait(TimeSpan.FromMilliseconds(200)));

        release.Set();
        Assert.True(Task.WhenAll(holder, waiter).Wait(TimeSpan.FromSeconds(5)));
        Assert.True(entered.IsSet);
    }

    [Fact]
    public void NoMoreSessionsThanTheCapacityAreEverLive()
    {
        const int capacity = 3;
        using var pool = new ParseSessionPool(CSharp(), capacity);
        var sessions = new HashSet<ParseSession>();

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
        {
            var session = pool.Use(0, static (s, _) => s);
            lock (sessions) sessions.Add(session);
        });

        Assert.InRange(sessions.Count, 1, capacity);
    }

    [Fact]
    public void ACapacityBelowOneIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParseSessionPool(CSharp(), 0));
    }
}
