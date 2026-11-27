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
        DateOnly end = period.End;
        long startTicks = StartOfDayUtc(start, request.TimeZone).Ticks;
        long nowTicks = request.UtcNow.Ticks;
        long endTicks = Math.Min(StartOfDayUtc(end, request.TimeZone).Ticks, nowTicks);
        long[] counts = new long[period.IsMonthly ? end.DayNumber - start.DayNumber : 12];
        long playCount = 0;
        double milliseconds = 0;
        int activeDays = 0;

        // 逐个自然日用同一条索引范围语句聚合。每条会话最多读入一次，日期转换不逐行执行。
        const string totalsSql = """
            SELECT COUNT(*), SUM(MAX(0, MIN(DurationPlayedMs, TotalDurationMs)))
            FROM PlaybackHistory WHERE StartedAt >= ?1 AND StartedAt < ?2
            """;
        const string firstSql = "SELECT StartedAt FROM PlaybackHistory WHERE StartedAt >= 1 AND StartedAt < ?1 ORDER BY StartedAt LIMIT 1";
        const string latestSql = "SELECT StartedAt FROM PlaybackHistory WHERE StartedAt >= 1 AND StartedAt < ?1 ORDER BY StartedAt DESC LIMIT 1";
        long firstTicks = connection.ExecuteScalar<long>(firstSql, nowTicks);
        long latestTicks = connection.ExecuteScalar<long>(latestSql, nowTicks);
        token.ThrowIfCancellationRequested();

        if (latestTicks >= startTicks && firstTicks < endTicks && endTicks > startTicks)
        {
            var handle = connection.Handle;
            using var statement = Prepare(handle, totalsSql, token);
            long dayStart = startTicks;
            for (DateOnly day = start; day < end && dayStart < endTicks; day = day.AddDays(1))
            {
                token.ThrowIfCancellationRequested();
                long dayEnd = Math.Min(StartOfDayUtc(day.AddDays(1), request.TimeZone).Ticks, endTicks);
                if (dayEnd > dayStart)
                {
                    BindRange(handle, statement, dayStart, dayEnd, token);
                    if (Step(handle, statement, token))
                    {
                        long count = raw.sqlite3_column_int64(statement, 0);
                        if (count > 0)
                        {
                            counts[period.IsMonthly ? day.Day - 1 : day.Month - 1] += count;
                            playCount += count;
                            milliseconds += raw.sqlite3_column_double(statement, 1);
                            activeDays++;
                        }
                    }
                    Check(handle, raw.sqlite3_reset(statement), token);
                }
                dayStart = dayEnd;
            }
        }

        int limit = period.IsMonthly ? 3 : 5;
        ReportSong[] songs = playCount > 0 ? ReadSongs(connection.Handle, startTicks, endTicks, limit, token) : [];
        ReportArtist[] artists = playCount > 0 ? ReadArtists(connection.Handle, startTicks, endTicks, limit, token) : [];
        return new(request, playCount, milliseconds, activeDays, counts, songs, artists,
            firstTicks > 0 ? request.LocalDate(new(firstTicks, DateTimeKind.Utc)) : null,
            latestTicks > 0 ? request.LocalDate(new(latestTicks, DateTimeKind.Utc)) : null);
    }

    // MAX 为分组选择有标题的最近一行，附带同一行的作者和可选封面。无 ID 旧数据按标签归组。
    private const string SongsSql = """
        SELECT substr(h.Title, 1, 256), substr(h.Author, 1, 256), COUNT(*) AS Plays,
               substr(m.ImageHash, 1, 128), MAX(CASE WHEN trim(h.Title) <> '' THEN h.StartedAt ELSE 0 END)
        FROM PlaybackHistory AS h LEFT JOIN Music AS m ON m.Id=h.MusicId
        WHERE h.StartedAt >= ?1 AND h.StartedAt < ?2
        GROUP BY COALESCE(NULLIF(m.SongId,0),NULLIF(h.SongIdAtPlay,0),-h.MusicId),
                 CASE WHEN h.MusicId=0 THEN h.Title END,
                 CASE WHEN h.MusicId=0 THEN h.Author END,
                 CASE WHEN h.MusicId=0 THEN h.Album END
        ORDER BY Plays DESC, SUM(MAX(0,MIN(h.DurationPlayedMs,h.TotalDurationMs))) DESC,
                 COALESCE(NULLIF(m.SongId,0),NULLIF(h.SongIdAtPlay,0),-h.MusicId),
                 CASE WHEN h.MusicId=0 THEN h.Title END,
                 CASE WHEN h.MusicId=0 THEN h.Author END,
                 CASE WHEN h.MusicId=0 THEN h.Album END
        LIMIT ?3
        """;

    private static ReportSong[] ReadSongs(sqlite3 handle, long start, long end, int limit, CancellationToken token)
    {
        using var statement = Prepare(handle, SongsSql, token);
        BindRange(handle, statement, start, end, token);
        Check(handle, raw.sqlite3_bind_int(statement, 3, limit), token);
        var songs = new List<ReportSong>(limit);
        while (Step(handle, statement, token))
        {
            songs.Add(new(ReadText(statement, 0), ReadText(statement, 1), raw.sqlite3_column_int64(statement, 2),
                raw.sqlite3_column_text(statement, 3).utf8_to_string()));
        }
        return songs.ToArray();
    }

    private static ReportArtist[] ReadArtists(sqlite3 handle, long start, long end, int limit, CancellationToken token)
    {
        const string sql = """
            SELECT substr(Author, 1, 256), COUNT(*) AS Plays
            FROM PlaybackHistory WHERE StartedAt >= ?1 AND StartedAt < ?2 AND trim(Author, char(9,10,13,32)) <> ''
            GROUP BY Author
            ORDER BY Plays DESC, SUM(MAX(0,MIN(DurationPlayedMs,TotalDurationMs))) DESC, Author
            LIMIT ?3
            """;
        using var statement = Prepare(handle, sql, token);
        BindRange(handle, statement, start, end, token);
        Check(handle, raw.sqlite3_bind_int(statement, 3, limit), token);
        var artists = new List<ReportArtist>(limit);
        while (Step(handle, statement, token))
            artists.Add(new(ReadText(statement, 0), raw.sqlite3_column_int64(statement, 1)));
        return artists.ToArray();
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
