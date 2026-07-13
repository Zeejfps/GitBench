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
        Assert.Equal("zh", Strings.ZhHans.Culture.TwoLetterISOLanguageName);
        Assert.Equal("ko", Strings.Ko.Culture.TwoLetterISOLanguageName);
        Assert.Equal("ar", Strings.Ar.Culture.TwoLetterISOLanguageName);
        Assert.Equal("ru", Strings.Ru.Culture.TwoLetterISOLanguageName);
    }

    [Fact]
    public void ArabicPluralRuleCoversAllSixCategories()
    {
        Assert.Equal(PluralCategory.Zero, PluralRules.Category("ar", 0));
        Assert.Equal(PluralCategory.One, PluralRules.Category("ar", 1));
        Assert.Equal(PluralCategory.Two, PluralRules.Category("ar", 2));
        Assert.Equal(PluralCategory.Few, PluralRules.Category("ar", 3));
        Assert.Equal(PluralCategory.Few, PluralRules.Category("ar", 10));
        Assert.Equal(PluralCategory.Few, PluralRules.Category("ar", 103)); // n % 100 = 3
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ar", 11));
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ar", 99));
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ar", 111)); // n % 100 = 11
        Assert.Equal(PluralCategory.Other, PluralRules.Category("ar", 100));
        Assert.Equal(PluralCategory.Other, PluralRules.Category("ar", 101));
    }

    [Fact]
    public void RussianPluralRuleCoversOneFewMany()
    {
        Assert.Equal(PluralCategory.One, PluralRules.Category("ru", 1));
        Assert.Equal(PluralCategory.Few, PluralRules.Category("ru", 2));
        Assert.Equal(PluralCategory.Few, PluralRules.Category("ru", 4));
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ru", 5));
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ru", 0));

        // The teens are "many" even though their last digit says otherwise.
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ru", 11));
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ru", 12));
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ru", 14));
        Assert.Equal(PluralCategory.Many, PluralRules.Category("ru", 111));

        // "one" is every number ending in 1 except the teens — 21, not just 1.
        Assert.Equal(PluralCategory.One, PluralRules.Category("ru", 21));
        Assert.Equal(PluralCategory.One, PluralRules.Category("ru", 101));
        Assert.Equal(PluralCategory.Few, PluralRules.Category("ru", 22));
    }

    [Fact]
    public void RussianCatalogSelectsTheFormForEachCategory()
    {
        Assert.Equal("Индексировать 1 файл", Strings.Ru.FilesStage(1));
        Assert.Equal("Индексировать 3 файла", Strings.Ru.FilesStage(3));
        Assert.Equal("Индексировать 5 файлов", Strings.Ru.FilesStage(5));
        Assert.Equal("Индексировать 12 файлов", Strings.Ru.FilesStage(12));

        // Russian "one" covers 21, so the form has to carry the count rather than read as a
        // bare singular the way English's "Stage" does.
        Assert.Equal("Индексировать 21 файл", Strings.Ru.FilesStage(21));
    }

    [Fact]
    public void ArabicCatalogSelectsTheFormForEachCategory()
    {
        // one/two are countless bare forms (digit-shape-independent); few/many carry distinct
        // noun inflections, so a substring check proves the right form was selected.
        Assert.Equal("تجهيز", Strings.Ar.FilesStage(1));            // one
        Assert.Equal("تجهيز ملفين", Strings.Ar.FilesStage(2));      // two (dual)
        Assert.Contains("ملفات", Strings.Ar.FilesStage(3));         // few
        Assert.Contains("ملفًا", Strings.Ar.FilesStage(11));        // many
    }

    [Fact]
    public void ArabicCountedKeysInflectBeyondTheOtherForm()
    {
        // These keys carried only one/other, so every count from 2 up fell through to "other"
        // and read in the wrong case. LOC008 now requires the full set.
        Assert.Equal("حذف فرعين", Strings.Ar.BranchesCleanAction(2));
        Assert.Contains("فروع", Strings.Ar.BranchesCleanAction(3));
        Assert.Contains("فرعًا", Strings.Ar.BranchesCleanAction(11));

        Assert.Contains("تغييران", Strings.Ar.CommitsResetDirtyStaged(2));
        Assert.Contains("تغييرات", Strings.Ar.CommitsResetDirtyStaged(3));
        Assert.Contains("تغييرًا", Strings.Ar.CommitsResetDirtyStaged(11));
    }
}
