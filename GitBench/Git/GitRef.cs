namespace GitBench.Git;

/// <summary>
/// A ref passed to a git command as an argument.
///
/// <see cref="Head"/> is deliberately symbolic: it reaches git as the literal <c>HEAD</c> and is
/// resolved inside the command, under the repo lock — so "wherever we are now" can never be a branch
/// name the UI read at some earlier moment. That distinction is the whole type: a caller that means
/// the current branch says <see cref="Head"/>, and a caller that means one specific branch, tag, or
/// commit says <see cref="Named"/>. Neither can be mistaken for the other, which is what stops a
/// name captured while HEAD was moving from being handed to git as the branch to work from.
/// </summary>
public abstract record GitRef
{
    private GitRef() { }

    /// <summary>Wherever HEAD is when the command runs.</summary>
    public static GitRef Head { get; } = new HeadRef();

    /// <summary>One specific ref or SHA the caller genuinely means.</summary>
    public static GitRef Named(string value) => new NamedRef(value);

    /// <summary>The token to hand git.</summary>
    public abstract string Argument { get; }

    private sealed record HeadRef : GitRef
    {
        public override string Argument => "HEAD";
    }

    private sealed record NamedRef(string Value) : GitRef
    {
        public override string Argument => Value;
    }
}
