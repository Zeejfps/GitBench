namespace GitBench.Features.LocalChanges;

internal abstract record EditorMode
{
    private EditorMode() { }

    public static readonly EditorMode Idle = new Normal();

    public sealed record Normal : EditorMode;
    public sealed record Amending(AmendSession Session) : EditorMode;
    public sealed record Merging : EditorMode;
}
