using SQLite;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model.Stats;

namespace WinUIMusicPlayer.Services.LastFm;

public sealed class SqliteLastFmHistorySource(Func<SQLiteAsyncConnection> database,
    IReadOnlySet<string>? unknownArtists = null, IReadOnlySet<string>? unknownAlbums = null) : ILastFmHistorySource
{
    public async Task<int> CountOlderAsync(DateTime fromUtc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int count = await database().ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PlaybackHistory WHERE StartedAt < ?", fromUtc.Ticks).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return count;
    }

    public async IAsyncEnumerable<LastFmHistoryEntry> ReadAsync(DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken token)
    {
        int after = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // SQLite-net does not cancel an in-flight query. Await the real bounded query before
            // observing cancellation, so shutdown cannot release its database underneath the import.
            var page = await database().QueryAsync<PlaybackHistory>(
                "SELECT * FROM PlaybackHistory WHERE Id > ? AND StartedAt >= ? AND StartedAt <= ? ORDER BY Id LIMIT 500",
                after, fromUtc.Ticks, toUtc.Ticks).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var row in page)
            {
                token.ThrowIfCancellationRequested();
                after = row.Id;
                string artist = row.Author?.Trim() ?? "";
                string album = row.Album?.Trim() ?? "";
                if (unknownArtists?.Contains(artist) == true) artist = "";
                if (unknownAlbums?.Contains(album) == true) album = "";
                yield return new(row.Id, row.Title, artist, album, row.TotalDurationMs, row.DurationPlayedMs, row.StartedAt, row.EndedAt);
            }
            if (page.Count < 500) yield break;
        }
    }
}
