using GitBench.Widgets;

namespace GitBench.App;

/// <summary>
/// The size code is drawn at in file previews, editors and diffs, apart from the rest of the UI.
/// Always one of the offered sizes: the only constructor snaps to the nearest one, so a hand-edited
/// preference or a NaN resolves to something usable at the boundary.
/// </summary>
/// <remarks>In logical points, so it multiplies with the UI scale rather than replacing it.</remarks>
public readonly record struct EditorFontSize
{
    private static readonly float[] Rungs = [10f, 11f, 12f, 13f, 14f, 15f, 16f, 18f, 20f, 22f, 24f];

    public EditorFontSize(float points)
    {
        Points = Snap(points);
    }

    public float Points { get; }

    public static EditorFontSize Default => new(FontSize.Body);

    public static IReadOnlyList<EditorFontSize> All { get; } = Rungs.Select(r => new EditorFontSize(r)).ToArray();

    public string Label => $"{Points:0} px";

    private static float Snap(float points)
    {
        if (float.IsNaN(points)) return FontSize.Body;
        if (points <= Rungs[0]) return Rungs[0];
        if (points >= Rungs[^1]) return Rungs[^1];

        var best = Rungs[0];
        var bestDistance = MathF.Abs(points - best);
        foreach (var rung in Rungs)
        {
            var distance = MathF.Abs(points - rung);
            if (distance >= bestDistance) continue;
            best = rung;
            bestDistance = distance;
        }
        return best;
    }
}
