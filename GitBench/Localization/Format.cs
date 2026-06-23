using System;

namespace GitBench.Localization;

public static class Format
{
    public static string RelativeTime(Strings s, DateTimeOffset when) =>
        RelativeTime(s, when, DateTimeOffset.UtcNow);

    public static string RelativeTime(Strings s, DateTimeOffset when, DateTimeOffset now)
    {
        var delta = now - when;
        if (delta.TotalSeconds < 0) return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", s.Culture);
        if (delta.TotalMinutes < 1) return s.TimeJustNow;
        if (delta.TotalMinutes < 60) return s.TimeMinutesAgo((int)delta.TotalMinutes);
        if (delta.TotalHours < 24) return s.TimeHoursAgo((int)delta.TotalHours);
        if (delta.TotalDays < 7) return s.TimeDaysAgo((int)delta.TotalDays);
        if (delta.TotalDays < 30) return s.TimeWeeksAgo((int)(delta.TotalDays / 7));
        if (delta.TotalDays < 365) return s.TimeMonthsAgo((int)(delta.TotalDays / 30));
        return s.TimeYearsAgo((int)(delta.TotalDays / 365));
    }
}
