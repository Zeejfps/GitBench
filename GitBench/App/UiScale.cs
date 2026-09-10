using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// How much the UI is magnified on top of the monitor's own content scale. Always one of the offered
/// rungs: the only constructor snaps to the nearest one, so a hand-edited preference, a NaN or a
/// value from a build with a different ladder resolves to something usable at the boundary rather
/// than reaching the window layer as an arbitrary float.
/// </summary>
/// <remarks>A fixed ladder rather than a free value: anything between the rungs buys nothing a rung
/// doesn't, and costs a font atlas baked for a size nobody asked for.</remarks>
public readonly record struct UiScale
{
    private static readonly float[] Rungs = [0.8f, 0.9f, 1f, 1.1f, 1.25f, 1.5f, 1.75f, 2f];

    public UiScale(float factor)
    {
        Factor = Snap(factor);
    }

    public float Factor { get; }

    public static UiScale Default => new(1f);

    public static IReadOnlyList<UiScale> All { get; } = Rungs.Select(r => new UiScale(r)).ToArray();

    /// <summary>How a rung is written in the picker. Percentages, not multipliers: that is what every
    /// OS display setting calls this.</summary>
    public string Label => $"{MathF.Round(Factor * 100f)}%";

    private static float Snap(float factor)
    {
        if (float.IsNaN(factor)) return 1f;
        // Short-circuited before the search so an infinity, whose distance to every rung is the same
        // infinity, still lands on the end of the ladder rather than on whichever rung came first.
        if (factor <= Rungs[0]) return Rungs[0];
        if (factor >= Rungs[^1]) return Rungs[^1];

        var best = Rungs[0];
        var bestDistance = MathF.Abs(factor - best);
        foreach (var rung in Rungs)
        {
            var distance = MathF.Abs(factor - rung);
            if (distance >= bestDistance) continue;
            best = rung;
            bestDistance = distance;
        }
        return best;
    }
}

/// <summary>Publishes the chosen UI scale to the window layer, which wants one live float and knows
/// nothing about preferences.</summary>
internal sealed class PreferredUiScale : IUiScale
{
    private readonly State<UiScale> _state;
    private Action<float>? _changed;

    public PreferredUiScale(State<UiScale> state)
    {
        _state = state;
        _state.Changed += scale => _changed?.Invoke(scale.Factor);
    }

    public float Value => _state.Value.Factor;

    public event Action<float> Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }
}
