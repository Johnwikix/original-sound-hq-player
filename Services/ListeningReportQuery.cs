using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using SQLitePCL;
using WinUIMusicPlayer.Model.Stats;

namespace WinUIMusicPlayer.Services;

internal sealed class ListeningReportQuery(MusicDatabaseService library)
{
    internal async Task<ListeningReport> ReadAsync(ListeningReportRequest request, CancellationToken token)
    {
        await library.WhenInitialized.WaitAsync(token).ConfigureAwait(false);
        string path = library.GetDbConnection().DatabasePath;
        return await Task.Run(() => Read(path, request, token), token).ConfigureAwait(false);
    }

    internal static ListeningReport Read(string path, ListeningReportRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var connection = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        // 排行分组按曲库规模增长。私有只读连接使用小页面缓存，排序可溢写到临时文件。
        connection.Execute("PRAGMA temp_store=FILE");
        connection.Execute("PRAGMA temp.cache_size=-512");
        connection.Execute("PRAGMA cache_size=-2048");
        connection.ExecuteScalar<long>("PRAGMA mmap_size=0");
        connection.BusyTimeout = TimeSpan.FromMilliseconds(400);
        var handle = connection.Handle;
        raw.sqlite3_progress_handler(handle, 4096, static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, token);
        try
        {
            ListeningReport? report = null;
            connection.RunInTransaction(() => report = ReadSnapshot(connection, request, token));
            return report!;
        }
        finally
        {
            raw.sqlite3_progress_handler(handle, 0, null, null);
        }
    }

    private static ListeningReport ReadSnapshot(SQLiteConnection connection, ListeningReportRequest request, CancellationToken token)
    {
        ListeningPeriod period = request.Period;
        DateOnly start = period.Start;
        int dayCount = period.End.DayNumber - start.DayNumber;
        long startTicks = StartOfDayUtc(start, request.TimeZone).Ticks;
        long nowTicks = request.UtcNow.Ticks;
        long endTicks = Math.Min(StartOfDayUtc(period.End, request.TimeZone).Ticks, nowTicks);
        long[] counts = new long[period.IsMonthly ? dayCount : 12];
        long[] weekdayCounts = new long[7];
        long[] hourCounts = new long[24];
        long playCount = 0;
        double milliseconds = 0;

        const string totalsSql = """
            SELECT StartedAt, MAX(0, MIN(DurationPlayedMs, TotalDurationMs))
            FROM PlaybackHistory WHERE StartedAt >= ?1 AND StartedAt < ?2 ORDER BY StartedAt
            """;
        const string firstSql = "SELECT StartedAt FROM PlaybackHistory WHERE StartedAt >= 1 AND StartedAt < ?1 ORDER BY StartedAt LIMIT 1";
        const string latestSql = "SELECT StartedAt FROM PlaybackHistory WHERE StartedAt >= 1 AND StartedAt < ?1 ORDER BY StartedAt DESC LIMIT 1";
        long firstTicks = connection.ExecuteScalar<long>(firstSql, nowTicks);
        long latestTicks = connection.ExecuteScalar<long>(latestSql, nowTicks);
        token.ThrowIfCancellationRequested();

        var handle = connection.Handle;
        int activeDays = 0;
        int streak = 0;
        int longestStreak = 0;
        Span<long> dayStarts = stackalloc long[dayCount];
        if (latestTicks >= startTicks && firstTicks < endTicks && endTicks > startTicks)
        {
            for (int index = 0; index < dayStarts.Length; index++)
                dayStarts[index] = StartOfDayUtc(start.AddDays(index), request.TimeZone).Ticks;

            // 沿用自然日 UTC 边界。小时按真实本地时间分桶，夏令时重复的小时归入同一桶。
            int dayIndex = 0;
            int previousDay = -1;
            using var statement = Prepare(handle, totalsSql, token);
            BindRange(handle, statement, startTicks, endTicks, token);
            while (Step(handle, statement, token))
            {
                long ticks = raw.sqlite3_column_int64(statement, 0);
                dayIndex = FindDay(dayStarts, dayIndex, ticks);
                if (dayIndex != previousDay)
                {
                    activeDays++;
                    streak = dayIndex == previousDay + 1 ? streak + 1 : 1;
                    longestStreak = Math.Max(longestStreak, streak);
                    previousDay = dayIndex;
                }
                DateOnly day = start.AddDays(dayIndex);
                counts[period.IsMonthly ? dayIndex : day.Month - 1]++;
                weekdayCounts[(int)day.DayOfWeek]++;
                DateTime local = TimeZoneInfo.ConvertTimeFromUtc(new(ticks, DateTimeKind.Utc), request.TimeZone);
                hourCounts[local.Hour]++;
                playCount++;
                milliseconds += raw.sqlite3_column_double(statement, 1);
            }
        }

        int limit = period.IsMonthly ? 3 : 5;
        (ReportSong[] songs, long distinctSongs, ReportSongHistory? topSongHistory) = playCount > 0
            ? ReadSongs(handle, start, dayStarts, endTicks, limit, token)
            : ([], 0L, null);
        ReportRank[] artists = playCount > 0 ? ReadRanks(handle, ArtistsSql, startTicks, endTicks, limit, token) : [];
        ReportRank[] albums = playCount > 0 ? ReadRanks(handle, AlbumsSql, startTicks, endTicks, limit, token) : [];
        ReportMonthlyArtist[] monthlyArtists = !period.IsMonthly && artists.Length > 0
            ? ReadMonthlyArtists(handle, request, counts, endTicks, token)
            : [];
        return new(request, playCount, milliseconds, activeDays, counts,
            new(distinctSongs, longestStreak, weekdayCounts, hourCounts), songs, topSongHistory, artists, albums, monthlyArtists,
            firstTicks > 0 ? request.LocalDate(new(firstTicks, DateTimeKind.Utc)) : null,
            latestTicks > 0 ? request.LocalDate(new(latestTicks, DateTimeKind.Utc)) : null);
    }

    private static int FindDay(ReadOnlySpan<long> dayStarts, int index, long ticks)
    {
        while (index + 1 < dayStarts.Length && dayStarts[index + 1] <= ticks) index++;
        return index;
    }

    private const string SongIdentity = """
        COALESCE(NULLIF(m.SongId,0),NULLIF(h.SongIdAtPlay,0),-h.MusicId)
        """;

    // MAX 为分组选择有标题的最近一行，附带同一行的可选封面。无 ID 旧数据按标签归组。
    private const string SongsSql = $"""
        SELECT substr(h.Title, 1, 256), COUNT(*) AS Plays,
               substr(m.ImageHash, 1, 128), COUNT(*) OVER (), h.Id,
               MAX(CASE WHEN trim(h.Title) <> '' THEN h.StartedAt ELSE 0 END)
        FROM PlaybackHistory AS h LEFT JOIN Music AS m ON m.Id=h.MusicId
        WHERE h.StartedAt >= ?1 AND h.StartedAt < ?2
        GROUP BY {SongIdentity},
                 CASE WHEN h.MusicId=0 THEN h.Title END,
                 CASE WHEN h.MusicId=0 THEN h.Author END,
                 CASE WHEN h.MusicId=0 THEN h.Album END
        ORDER BY Plays DESC, SUM(MAX(0,MIN(h.DurationPlayedMs,h.TotalDurationMs))) DESC,
                 {SongIdentity},
                 CASE WHEN h.MusicId=0 THEN h.Title END,
                 CASE WHEN h.MusicId=0 THEN h.Author END,
                 CASE WHEN h.MusicId=0 THEN h.Album END
        LIMIT ?3
        """;

    private static (ReportSong[] Songs, long DistinctSongs, ReportSongHistory? TopSongHistory) ReadSongs(
        sqlite3 handle, DateOnly startDate, ReadOnlySpan<long> dayStarts, long end, int limit, CancellationToken token)
    {
        var songs = new List<ReportSong>(limit);
        long distinctSongs = 0;
        long topRow = 0;
        using (var statement = Prepare(handle, SongsSql, token))
        {
            BindRange(handle, statement, dayStarts[0], end, token);
            Check(handle, raw.sqlite3_bind_int(statement, 3, limit), token);
            while (Step(handle, statement, token))
            {
                if (songs.Count == 0)
                {
                    distinctSongs = raw.sqlite3_column_int64(statement, 3);
                    topRow = raw.sqlite3_column_int64(statement, 4);
                }
                songs.Add(new(ReadText(statement, 0), raw.sqlite3_column_int64(statement, 1),
                    raw.sqlite3_column_text(statement, 2).utf8_to_string()));
            }
        }
        var history = songs.Count > 0 ? ReadSongHistory(handle, startDate, dayStarts, end, topRow, token) : null;
        return (songs.ToArray(), distinctSongs, history);
    }

    private static ReportSongHistory? ReadSongHistory(sqlite3 handle, DateOnly startDate, ReadOnlySpan<long> dayStarts,
        long end, long topRow, CancellationToken token)
    {
        // 用榜首行的完整分组键回查，避免展示用的截断标签合并不同歌曲。
        const string sql = $"""
            WITH winner AS (
                SELECT {SongIdentity} AS SongKey,
                       CASE WHEN h.MusicId=0 THEN h.Title END AS Title,
                       CASE WHEN h.MusicId=0 THEN h.Author END AS Author,
                       CASE WHEN h.MusicId=0 THEN h.Album END AS Album
                FROM PlaybackHistory AS h LEFT JOIN Music AS m ON m.Id=h.MusicId
                WHERE h.Id = ?3
            )
            SELECT h.StartedAt
            FROM PlaybackHistory AS h LEFT JOIN Music AS m ON m.Id=h.MusicId CROSS JOIN winner AS w
            WHERE h.StartedAt >= ?1 AND h.StartedAt < ?2
              AND ({SongIdentity}) IS w.SongKey
              AND (CASE WHEN h.MusicId=0 THEN h.Title END) IS w.Title
              AND (CASE WHEN h.MusicId=0 THEN h.Author END) IS w.Author
              AND (CASE WHEN h.MusicId=0 THEN h.Album END) IS w.Album
            ORDER BY h.StartedAt
            """;
        using var statement = Prepare(handle, sql, token);
        BindRange(handle, statement, dayStarts[0], end, token);
        Check(handle, raw.sqlite3_bind_int64(statement, 3, topRow), token);
        int first = -1;
        int previousDay = -1;
        int dayIndex = 0;
        int count = 0;
        while (Step(handle, statement, token))
        {
            dayIndex = FindDay(dayStarts, dayIndex, raw.sqlite3_column_int64(statement, 0));
            if (dayIndex == previousDay) continue;
            if (first < 0) first = dayIndex;
            previousDay = dayIndex;
            count++;
        }
        return count > 0 ? new(startDate.AddDays(first), count) : null;
    }

    private const string ArtistsSql = """
        SELECT substr(Author, 1, 256), COUNT(*) AS Plays
        FROM PlaybackHistory WHERE StartedAt >= ?1 AND StartedAt < ?2 AND trim(Author, char(9,10,13,32)) <> ''
        GROUP BY Author
        ORDER BY Plays DESC, SUM(MAX(0,MIN(DurationPlayedMs,TotalDurationMs))) DESC, Author
        LIMIT ?3
        """;

    private const string AlbumsSql = """
        SELECT substr(Album, 1, 256), COUNT(*) AS Plays
        FROM PlaybackHistory WHERE StartedAt >= ?1 AND StartedAt < ?2 AND trim(Album, char(9,10,13,32)) <> ''
        GROUP BY Album
        ORDER BY Plays DESC, SUM(MAX(0,MIN(DurationPlayedMs,TotalDurationMs))) DESC, Album
        LIMIT ?3
        """;

    private static ReportRank[] ReadRanks(sqlite3 handle, string sql, long start, long end, int limit, CancellationToken token)
    {
        using var statement = Prepare(handle, sql, token);
        BindRange(handle, statement, start, end, token);
        Check(handle, raw.sqlite3_bind_int(statement, 3, limit), token);
        var ranks = new List<ReportRank>(limit);
        while (Step(handle, statement, token))
            ranks.Add(new(ReadText(statement, 0), raw.sqlite3_column_int64(statement, 1)));
        return ranks.ToArray();
    }

    private static ReportMonthlyArtist[] ReadMonthlyArtists(sqlite3 handle, ListeningReportRequest request,
        long[] counts, long endTicks, CancellationToken token)
    {
        const string sql = """
            SELECT substr(Author, 1, 256), SUM(MAX(0,MIN(DurationPlayedMs,TotalDurationMs))) AS Ms
            FROM PlaybackHistory WHERE StartedAt >= ?1 AND StartedAt < ?2 AND trim(Author, char(9,10,13,32)) <> ''
            GROUP BY Author
            ORDER BY Ms DESC, COUNT(*) DESC, Author
            LIMIT 1
            """;
        using var statement = Prepare(handle, sql, token);
        var leaders = new List<ReportMonthlyArtist>(12);
        DateOnly start = request.Period.Start;
        for (int index = 0; index < counts.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (counts[index] == 0) continue;
            DateOnly month = start.AddMonths(index);
            long from = StartOfDayUtc(month, request.TimeZone).Ticks;
            long to = Math.Min(StartOfDayUtc(month.AddMonths(1), request.TimeZone).Ticks, endTicks);
            BindRange(handle, statement, from, to, token);
            if (Step(handle, statement, token))
                leaders.Add(new(month.Month, ReadText(statement, 0), raw.sqlite3_column_double(statement, 1)));
            Check(handle, raw.sqlite3_reset(statement), token);
        }
        return leaders.ToArray();
    }

    private static string ReadText(sqlite3_stmt statement, int column)
        => raw.sqlite3_column_text(statement, column).utf8_to_string() ?? string.Empty;

    private static sqlite3_stmt Prepare(sqlite3 handle, string sql, CancellationToken token)
    {
        int result = raw.sqlite3_prepare_v2(handle, sql, out var statement);
        if (result == raw.SQLITE_OK) return statement;
        statement?.Dispose();
        Check(handle, result, token);
        throw new InvalidOperationException();
    }

    private static void BindRange(sqlite3 handle, sqlite3_stmt statement, long start, long end, CancellationToken token)
    {
        Check(handle, raw.sqlite3_bind_int64(statement, 1, start), token);
        Check(handle, raw.sqlite3_bind_int64(statement, 2, end), token);
    }

    private static bool Step(sqlite3 handle, sqlite3_stmt statement, CancellationToken token)
    {
        int result = raw.sqlite3_step(statement);
        if (result == raw.SQLITE_ROW) return true;
        if (result != raw.SQLITE_DONE) Check(handle, result, token);
        return false;
    }

    private static void Check(sqlite3 handle, int result, CancellationToken token)
    {
        if (result == raw.SQLITE_OK) return;
        token.ThrowIfCancellationRequested();
        throw new IOException(raw.sqlite3_errmsg(handle).utf8_to_string());
    }

    internal static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        DateTime local = date.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(local))
        {
            // 如本地零点没有对应的时刻，使用间隙结束时的第一刻。
            DateTime valid = local.AddHours(1);
            while (zone.IsInvalidTime(valid)) valid = valid.AddHours(1);
            long low = local.Ticks;
            long high = valid.Ticks;
            while (high - low > 1)
            {
                long middle = low + (high - low) / 2;
                if (zone.IsInvalidTime(new(middle))) low = middle;
                else high = middle;
            }
            local = new(high);
        }

        if (!zone.IsAmbiguousTime(local)) return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        TimeSpan[] offsets = zone.GetAmbiguousTimeOffsets(local);
        TimeSpan earlier = offsets[0] > offsets[1] ? offsets[0] : offsets[1];
        return new DateTimeOffset(local, earlier).UtcDateTime;
    }
}
