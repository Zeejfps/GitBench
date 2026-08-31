namespace GitBench.Features.CodeIntel;

internal abstract record CodeIntelAvailability
{
    private CodeIntelAvailability() { }

    public sealed record Ready : CodeIntelAvailability
    {
        public static readonly Ready Instance = new();
    }

    public sealed record Unavailable(string Reason) : CodeIntelAvailability;
}
