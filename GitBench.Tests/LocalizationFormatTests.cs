using System;
using System.Globalization;
using GitBench.Localization;
using Xunit;
using ZGF.Gui.Localization;

namespace GitBench.Tests;

public class LocalizationFormatTests
{
    [Fact]
    public void PluralPicksOneOrOtherByCount()
    {
        Assert.Equal("Stage", Strings.En.FilesStage(1));
        Assert.Equal("Stage 3 Files", Strings.En.FilesStage(3));
        Assert.Equal("Mark as Resolved", Strings.En.FilesMarkResolved(1));
        Assert.Equal("Mark 2 as Resolved", Strings.En.FilesMarkResolved(2));
    }

    [Fact]
    public void PluralUsesSpanishForms()
    {
        Assert.Equal("Preparar", Strings.Es.FilesStage(1));
        Assert.Equal("Preparar 3 archivos", Strings.Es.FilesStage(3));
    }

    [Fact]
    public void ParameterizedStringsSubstituteTheCount()
    {
        Assert.Equal("5m ago", Strings.En.TimeMinutesAgo(5));
        Assert.Equal("hace 5 min", Strings.Es.TimeMinutesAgo(5));
        Assert.Equal("5分前", Strings.Ja.TimeMinutesAgo(5));
    }

    [Fact]
    public void JapaneseHasNoPluralDistinctionButHonorsBareSingularForm()
    {
        // The generic rule maps count==1 to the "one" form (bare verb) and the rest to
        // "other" (counted) — Japanese itself has no grammatical plural, so the counted
        // "other" form reads naturally for every count > 1.
        Assert.Equal("ステージ", Strings.Ja.FilesStage(1));
        Assert.Equal("3個のファイルをステージ", Strings.Ja.FilesStage(3));
    }

    [Fact]
    public void RelativeTimeChoosesUnitAndLocalizes()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("just now", Format.RelativeTime(Strings.En, now.AddSeconds(-10), now));
        Assert.Equal("5m ago", Format.RelativeTime(Strings.En, now.AddMinutes(-5), now));
        Assert.Equal("2h ago", Format.RelativeTime(Strings.En, now.AddHours(-2), now));
        Assert.Equal("3d ago", Format.RelativeTime(Strings.En, now.AddDays(-3), now));

        Assert.Equal("ahora mismo", Format.RelativeTime(Strings.Es, now.AddSeconds(-10), now));
        Assert.Equal("hace 2 h", Format.RelativeTime(Strings.Es, now.AddHours(-2), now));
    }

    [Fact]
    public void RelativeTimeUsesSpanishSingularForMonthsAndYears()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("hace 1 mes", Format.RelativeTime(Strings.Es, now.AddDays(-40), now));
        Assert.Equal("hace 2 meses", Format.RelativeTime(Strings.Es, now.AddDays(-70), now));
        Assert.Equal("hace 1 año", Format.RelativeTime(Strings.Es, now.AddDays(-400), now));
        Assert.Equal("hace 2 años", Format.RelativeTime(Strings.Es, now.AddDays(-800), now));
    }

    [Fact]
    public void PluralRuleCategoryMatchesLanguage()
    {
        Assert.Equal(PluralCategory.One, PluralRules.Category("en", 1));
        Assert.Equal(PluralCategory.Other, PluralRules.Category("en", 2));
        Assert.Equal(PluralCategory.Other, PluralRules.Category("en", 0));
        Assert.Equal(PluralCategory.One, PluralRules.Category("fr", 0));
        Assert.Equal(PluralCategory.One, PluralRules.Category("fr", 1));
    }

    [Fact]
    public void EveryCatalogExposesItsCulture()
    {
        Assert.Equal("en", Strings.En.Culture.TwoLetterISOLanguageName);
        Assert.Equal("es", Strings.Es.Culture.TwoLetterISOLanguageName);
        Assert.Equal("ja", Strings.Ja.Culture.TwoLetterISOLanguageName);
    }
}
