namespace GitBench.Localization;

public enum PluralCategory
{
    Zero,
    One,
    Two,
    Few,
    Many,
    Other,
}

public readonly struct PluralForms
{
    private readonly string _other;
    private readonly string? _zero;
    private readonly string? _one;
    private readonly string? _two;
    private readonly string? _few;
    private readonly string? _many;

    public PluralForms(
        string other,
        string? zero = null,
        string? one = null,
        string? two = null,
        string? few = null,
        string? many = null)
    {
        _other = other;
        _zero = zero;
        _one = one;
        _two = two;
        _few = few;
        _many = many;
    }

    public string Get(PluralCategory category) => category switch
    {
        PluralCategory.Zero => _zero ?? _other,
        PluralCategory.One => _one ?? _other,
        PluralCategory.Two => _two ?? _other,
        PluralCategory.Few => _few ?? _other,
        PluralCategory.Many => _many ?? _other,
        _ => _other,
    };
}
