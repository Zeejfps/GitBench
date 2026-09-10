namespace GitBench.Localization;

public static class LocaleOptions
{
    public readonly record struct Option(Locale Locale, string Endonym);

    public static IReadOnlyList<Option> All { get; } =
    [
        new(Locale.En, "English"),
        new(Locale.Es, "Español"),
        new(Locale.Ja, "日本語"),
        new(Locale.ZhHans, "简体中文"),
        new(Locale.Ko, "한국어"),
        new(Locale.Ar, "العربية"),
        new(Locale.Ru, "Русский"),
        new(Locale.Pseudo, "Pseudo"),
    ];

    public static string Endonym(Locale locale)
    {
        foreach (var option in All)
            if (option.Locale == locale) return option.Endonym;
        throw new ArgumentOutOfRangeException(nameof(locale), locale, "No endonym is defined for this locale.");
    }
}
