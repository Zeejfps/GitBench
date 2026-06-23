using System.Globalization;

namespace GitBench.Localization;

public static class PluralRules
{
    public static string Select(CultureInfo culture, in PluralForms forms, long n) =>
        forms.Get(Category(culture.TwoLetterISOLanguageName, n));

    public static PluralCategory Category(string language, long n) => language switch
    {
        "fr" or "pt" => n is 0 or 1 ? PluralCategory.One : PluralCategory.Other,
        _ => n == 1 ? PluralCategory.One : PluralCategory.Other,
    };
}
