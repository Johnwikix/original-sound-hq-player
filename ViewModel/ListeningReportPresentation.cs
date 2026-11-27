using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model.Stats;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel;

public sealed record ListeningReportTile(string Label, string Description, double BarHeight, double Opacity, bool IsPadding)
{
    public Visibility LabelVisibility => IsPadding ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record ListeningReportRank(string Position, string Name, string Count, string CountDescription);

public sealed record ListeningReportMetric(string Label, string Value)
{
    public Visibility Visibility => Value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record ListeningReportMonthlyLeader(string Month, string Name, string Duration);

public sealed class ListeningReportPresentation
{
    public string Heading { get; }
    public string Coverage { get; }
    public string Zone { get; }
    public string Footnote { get; }
    public string Duration { get; }
    public string PlayCount { get; }
    public string ActiveDays { get; }
    public ListeningReportMetric TrackVariety { get; }
    public ListeningReportMetric PeakHour { get; }
    public ListeningReportMetric[] Habits { get; }
    public string TrendHeading { get; }
    public string Highlight { get; }
    public Visibility HighlightVisibility => VisibilityFor(Highlight);
    public ListeningReportTile[] Tiles { get; }
    public ListeningReportTile[] Weekdays { get; }
    public Visibility MonthlyVisibility => Weekdays.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AnnualVisibility => Weekdays.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ListeningReportRank[] Songs { get; }
    public string TopSongNote { get; }
    public Visibility TopSongNoteVisibility => VisibilityFor(TopSongNote);
    public ListeningReportRank[] Artists { get; }
    public Visibility ArtistsVisibility => Artists.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public ListeningReportRank[] Albums { get; }
    public Visibility AlbumsVisibility => Albums.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public ListeningReportMonthlyLeader[] MonthlyArtists { get; }
    public Visibility MonthlyArtistsVisibility => MonthlyArtists.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string PosterSongs { get; }
    public string PosterArtist { get; }
    public Visibility PosterArtistVisibility => VisibilityFor(PosterArtist);

    internal ListeningReportPresentation(ListeningReport report, CultureInfo culture)
    {
        var request = report.Request;
        var period = request.Period;
        DateOnly start = period.Start;
        DateOnly today = request.Today;
        DateOnly end = period.End.AddDays(-1);
        string periodLabel = start.ToString(period.IsMonthly ? "Y" : "yyyy", culture);
        Heading = Format(culture, period.IsMonthly ? "ListeningReportMonthTitle" : "ListeningReportYearTitle", periodLabel);
        Coverage = report.InProgress
            ? Format(culture, "ListeningReportInProgress", start.ToString("d", culture),
                TimeZoneInfo.ConvertTimeFromUtc(request.UtcNow, request.TimeZone).ToString("g", culture))
            : string.Concat(start.ToString("d", culture), " – ", end.ToString("d", culture));
        Zone = request.TimeZone.DisplayName;
        Footnote = report.FirstRecord is { } first && first >= start && first <= end
            ? Format(culture, "ListeningReportFirstRecord", first.ToString("d", culture))
            : GetString("ListeningReportHistoryOnly");

        Duration = FormatDuration(report.Milliseconds, culture);
        PlayCount = report.PlayCount.ToString("N0", culture);
        ActiveDays = report.ActiveDays.ToString("N0", culture);
        TrackVariety = new(GetString("ListeningReportDistinctSongs"), report.Habits.DistinctSongs.ToString("N0", culture));
        int peakHour = UniquePeak(report.Habits.HourCounts);
        PeakHour = new(GetString("ListeningReportPeakHour"), peakHour >= 0 ? FormatHour(peakHour, culture) : string.Empty);
        Habits = BuildHabits(report, culture);
        TrendHeading = GetString(period.IsMonthly ? "ListeningReportDaily" : "ListeningReportMonthlyTrend");

        int padding = period.IsMonthly ? (7 + (int)start.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek) % 7 : 0;
        long maximum = 0;
        int peak = -1;
        int populated = 0;
        for (int index = 0; index < report.Counts.Length; index++)
        {
            long count = report.Counts[index];
            if (count <= 0) continue;
            populated++;
            if (count > maximum) { maximum = count; peak = index; }
            else if (count == maximum) peak = -1;
        }
        Highlight = peak >= 0 && populated > 1
            ? Format(culture, period.IsMonthly ? "ListeningReportPeakDay" : "ListeningReportPeakMonth",
                BucketName(start, period.IsMonthly, peak, culture), maximum)
            : string.Empty;
        Weekdays = period.IsMonthly ? new ListeningReportTile[7] : [];
        int firstWeekday = (int)culture.DateTimeFormat.FirstDayOfWeek;
        for (int index = 0; index < Weekdays.Length; index++)
            Weekdays[index] = new(culture.DateTimeFormat.GetShortestDayName((DayOfWeek)((firstWeekday + index) % 7)), "", 0, 0, false);

        Tiles = new ListeningReportTile[padding + report.Counts.Length];
        for (int index = 0; index < padding; index++) Tiles[index] = new("", "", 0, 0, true);
        for (int index = 0; index < report.Counts.Length; index++)
        {
            DateOnly date = period.IsMonthly ? start.AddDays(index) : start.AddMonths(index);
            bool future = period.IsMonthly ? date > today : date.Year == today.Year && date.Month > today.Month;
            bool beforeRecords = report.FirstRecord is { } earliest &&
                (period.IsMonthly ? date < earliest : date.AddMonths(1) <= earliest);
            long count = report.Counts[index];
            string label = period.IsMonthly ? date.Day.ToString(culture) : culture.DateTimeFormat.GetAbbreviatedMonthName(date.Month);
            string bucket = BucketName(start, period.IsMonthly, index, culture);
            string desc = future ? Format(culture, "ListeningReportFuture", bucket) :
                beforeRecords ? Format(culture, "ListeningReportNoRecords", bucket) : Format(culture, "ListeningReportTileCount", bucket, count);
            double scale = maximum > 0 ? (double)count / maximum : 0;
            Tiles[padding + index] = new(label, desc, count > 0 ? Math.Max(4, scale * 82) : 0,
                future || beforeRecords ? 0 : count == 0 ? 0.10 : 0.20 + 0.60 * scale, false);
        }

        Songs = new ListeningReportRank[report.Songs.Length];
        for (int index = 0; index < Songs.Length; index++)
        {
            ReportSong song = report.Songs[index];
            Songs[index] = new((index + 1).ToString(culture),
                string.IsNullOrWhiteSpace(song.Title) ? GetString("ListeningReportUnknownSong") : SingleLine(song.Title),
                song.PlayCount.ToString("N0", culture), Format(culture, "ListeningReportPlayOccurrences", song.PlayCount));
        }
        TopSongNote = report.TopSongHistory is { } songHistory
            ? Format(culture, "ListeningReportTopSongHistory", songHistory.FirstDay.ToString("M", culture), songHistory.ActiveDays)
            : string.Empty;
        Artists = BuildRanks(report.Artists, culture);
        Albums = BuildRanks(report.Albums, culture);
        MonthlyArtists = new ListeningReportMonthlyLeader[report.MonthlyArtists.Length];
        for (int index = 0; index < MonthlyArtists.Length; index++)
        {
            ReportMonthlyArtist leader = report.MonthlyArtists[index];
            MonthlyArtists[index] = new(culture.DateTimeFormat.GetAbbreviatedMonthName(leader.Month),
                SingleLine(leader.Name), FormatDuration(leader.Milliseconds, culture));
        }
        PosterSongs = Songs.Length switch
        {
            >= 3 => string.Concat(Songs[0].Name, "\n", Songs[1].Name, "\n", Songs[2].Name),
            2 => string.Concat(Songs[0].Name, "\n", Songs[1].Name),
            1 => Songs[0].Name,
            _ => string.Empty
        };
        PosterArtist = Artists.Length == 0 ? string.Empty : Artists[0].Name;
    }

    private ListeningReportMetric[] BuildHabits(ListeningReport report, CultureInfo culture)
    {
        if (report.PlayCount == 0) return [];
        double replayShare = (double)(report.PlayCount - report.Habits.DistinctSongs) / report.PlayCount;
        var metrics = new List<ListeningReportMetric>(5)
        {
            TrackVariety,
            new(GetString("ListeningReportReplayShare"), replayShare.ToString("P0", culture)),
            new(GetString("ListeningReportLongestStreak"), report.Habits.LongestStreak.ToString("N0", culture))
        };
        int weekday = UniquePeak(report.Habits.WeekdayCounts);
        if (weekday >= 0)
            metrics.Add(new(GetString("ListeningReportPeakWeekday"), culture.DateTimeFormat.GetDayName((DayOfWeek)weekday)));
        if (PeakHour.Value.Length > 0) metrics.Add(PeakHour);
        return metrics.ToArray();
    }

    private static int UniquePeak(long[] counts)
    {
        long max = 0;
        int peak = -1;
        for (int index = 0; index < counts.Length; index++)
        {
            if (counts[index] > max)
            {
                max = counts[index];
                peak = index;
            }
            else if (counts[index] == max) peak = -1;
        }
        return peak;
    }

    private static string FormatHour(int hour, CultureInfo culture)
        => string.Concat(new TimeOnly(hour, 0).ToString("t", culture), " – ",
            new TimeOnly((hour + 1) % 24, 0).ToString("t", culture));

    private static string FormatDuration(double milliseconds, CultureInfo culture)
    {
        long seconds = Math.Max(0, (long)(milliseconds / 1000d));
        return seconds < 60
            ? Format(culture, "ListeningReportSeconds", seconds)
            : seconds < 3600
                ? Format(culture, "ListeningReportMinutes", seconds / 60)
                : Format(culture, "ListeningReportHoursMinutes", seconds / 3600, seconds / 60 % 60);
    }

    private static ListeningReportRank[] BuildRanks(ReportRank[] entries, CultureInfo culture)
    {
        var ranks = new ListeningReportRank[entries.Length];
        for (int index = 0; index < ranks.Length; index++)
        {
            ReportRank entry = entries[index];
            ranks[index] = new((index + 1).ToString(culture), SingleLine(entry.Name),
                entry.PlayCount.ToString("N0", culture), Format(culture, "ListeningReportPlayOccurrences", entry.PlayCount));
        }
        return ranks;
    }

    private static string SingleLine(string text)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0) return text;
        return string.Create(text.Length, text, static (target, source) =>
        {
            for (int index = 0; index < target.Length; index++)
                target[index] = source[index] is '\r' or '\n' ? ' ' : source[index];
        });
    }

    private static string BucketName(DateOnly start, bool monthly, int index, CultureInfo culture)
        => monthly ? start.AddDays(index).ToString("M", culture) : culture.DateTimeFormat.GetMonthName(index + 1);

    private static string Format(CultureInfo culture, string resource, object argument)
        => string.Format(culture, GetString(resource), argument);

    private static string Format(CultureInfo culture, string resource, object first, object second)
        => string.Format(culture, GetString(resource), first, second);

    private static Visibility VisibilityFor(string text) => text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}
