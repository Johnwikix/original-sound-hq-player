using System;

namespace WinUIMusicPlayer.Model.Stats;

internal readonly record struct ListeningPeriod(int Year, int Month = 0)
{
    internal bool IsMonthly => Month != 0;
    internal DateOnly Start => new(Year, IsMonthly ? Month : 1, 1);
    internal DateOnly End => IsMonthly ? Start.AddMonths(1) : Start.AddYears(1);
}

internal readonly record struct ListeningReportRequest(ListeningPeriod Period, DateTime UtcNow, TimeZoneInfo TimeZone)
{
    internal DateOnly Today => LocalDate(UtcNow);
    internal DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZone));
}

internal readonly record struct ReportSong(string Title, string Artist, long PlayCount, string? CoverHash);
internal readonly record struct ReportArtist(string Name, long PlayCount);

internal sealed record ListeningReport(
    ListeningReportRequest Request, long PlayCount, double Milliseconds, int ActiveDays, long[] Counts,
    ReportSong[] Songs, ReportArtist[] Artists, DateOnly? FirstRecord, DateOnly? LatestRecord)
{
    internal bool InProgress => Request.Today < Request.Period.End;
}
