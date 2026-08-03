using System.Security.Cryptography;
using System.Text;
using GitBench.Features.Assistant.Tools;
using GitBench.Git;

namespace GitBench.Features.Diff.Reading;

/// <summary>
/// A content hash of everything that decides what a plan will look like: the rubric, the edit
/// protocol, the tool descriptions and schemas, and the wording of the feedback the model reads.
/// </summary>
/// <remarks>
/// Cached plans are keyed on this. Hashing the surface as it is actually rendered — rather than a
/// list of the pieces that go into it — means rewording a tool description or recomposing the
/// feedback invalidates old plans too, so a cache hit is only ever a plan the current rules would
/// have produced.
/// </remarks>
internal static class ReadingSurface
{
    public static string Hash(string systemPrompt)
    {
        var rendered = Render(systemPrompt);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rendered)))[..16];
    }

    private static string Render(string systemPrompt)
    {
        var b = new StringBuilder();
        b.Append(ReadingPlanToolProtocol.Version).Append('\n');
        b.Append(systemPrompt).Append('\n');

        var abridgement = new ReadingAbridgement(ReadingRowIndex.Build([FixtureDiff()]));
        foreach (var tool in abridgement.Tools)
            b.Append(tool.Name).Append('\n').Append(tool.Description).Append('\n').Append(tool.JsonSchema).Append('\n');

        // Both feedback branches, rendered through the real builder over real plans, so a change to
        // either the wording or the numbers it reports moves the hash.
        b.Append(ReadingPlanToolProtocol.Feedback(ReadingPlanCompiler.Mechanical(abridgement.Index))).Append('\n');
        b.Append(ReadingPlanToolProtocol.Rejection(["fixture problem"])).Append('\n');
        return b.ToString();
    }

    // A fixture, never sent to a model: it exists so every branch of the surface renders.
    private static DiffResult FixtureDiff()
    {
        var lines = new List<DiffLine>
        {
            new(DiffLineKind.Removed, 1, null, "using System;"),
            new(DiffLineKind.Context, 2, 1, "void Run()"),
            new(DiffLineKind.Added, null, 2, "    var x = compute(1);"),
            new(DiffLineKind.Added, null, 3, "    var y = compute(2);"),
        };
        return new DiffResult(
            Guid.Empty, "fixture.cs", null, DiffSide.Commit,
            IsBinary: false, IsModeOnly: false, OldMode: null, NewMode: null,
            Hunks: [new DiffHunk(1, 2, 1, 3, null, lines)],
            Truncated: false, ErrorMessage: null);
    }
}
