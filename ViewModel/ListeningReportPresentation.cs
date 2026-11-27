using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model.Stats;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel;

public sealed record ListeningReportTile(string Label, string Description, double BarHeight, double Opacity, bool IsPadding)
{
    public Visibility LabelVisibility => IsPadding ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record ListeningReportRank(string Position, string Name, string Detail, string Count, string CountDescription)
{
    public Visibility DetailVisibility => Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class ListeningReportPresentation
{
    public string Heading { get; }
    public string Coverage { get; }
    public string Zone { get; }
    public string Footnote { get; }
    public string Duration { get; }
    public string PlayCount { get; }
    public string ActiveDays { get; }
    public string TrendHeading { get; }
    public string Highlight { get; }
    public Visibility HighlightVisibility => VisibilityFor(Highlight);
    public ListeningReportTile[] Tiles { get; }
    public ListeningReportTile[] Weekdays { get; }
    public Visibility MonthlyVisibility => Weekdays.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AnnualVisibility => Weekdays.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ListeningReportRank[] Songs { get; }
    public ListeningReportRank[] Artists { get; }
    public Visibility ArtistsVisibility => Artists.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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

        long seconds = Math.Max(0, (long)(report.Milliseconds / 1000d));
        Duration = seconds < 60
            ? Format(culture, "ListeningReportSeconds", seconds)
            : seconds < 3600
                ? Format(culture, "ListeningReportMinutes", seconds / 60)
                : Format(culture, "ListeningReportHoursMinutes", seconds / 3600, seconds / 60 % 60);
        PlayCount = report.PlayCount.ToString("N0", culture);
        ActiveDays = report.ActiveDays.ToString("N0", culture);
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
                SingleLine(song.Artist), song.PlayCount.ToString("N0", culture), Format(culture, "ListeningReportPlayOccurrences", song.PlayCount));
        }
        Artists = new ListeningReportRank[report.Artists.Length];
        for (int index = 0; index < Artists.Length; index++)
        {
            ReportArtist artist = report.Artists[index];
            Artists[index] = new((index + 1).ToString(culture), SingleLine(artist.Name), "", artist.PlayCount.ToString("N0", culture), Format(culture, "ListeningReportPlayOccurrences", artist.PlayCount));
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
