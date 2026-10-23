using SQLite;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

/// <summary>An immutable UI snapshot; later clicks must not mutate an earlier database write.</summary>
public sealed record LastFmFavoriteChange(int MusicId, LastFmTrack Track, bool Loved, int Order);
public sealed record LastFmFavoriteImportResult(int Queued, int Ineligible, bool Canceled = false);
public sealed record LastFmFavoriteIntent(string Key, string Version, LastFmTrack Track, bool Loved, bool Attempted);
public readonly record struct LastFmFavoriteOrder(int MusicId, int Order);
public sealed record LastFmFavoriteImportBatch(int LastId, int Scanned, int Queued, int Ineligible, int Added);

/// <summary>Borrows the library connection. Local changes and their outbox entry share one transaction.</summary>
public sealed class LastFmFavoriteStore(Func<SQLiteAsyncConnection> connection, HashSet<string>? unknownArtists = null)
{
    internal const int ImportBatchSize = 512;
    // Schema initialization is lazy: the library must already be initialized. Never close its connection.
    private Task? _schema;
    private readonly object _schemaGate = new();
    private Task InitializeAsync()
    {
        lock (_schemaGate)
        {
            if (_schema?.IsFaulted == true) _schema = null;
            return _schema ??= connection().CreateTableAsync<FavoriteRow>();
        }
    }

    public async Task<int> RetainAsync(string? apiKey, string? account)
    {
        await InitializeAsync().ConfigureAwait(false);
        var db = connection();
        await db.ExecuteAsync("DELETE FROM LastFmFavoriteOutbox WHERE ApiKey <> ? OR Account <> ?",
            apiKey ?? "", AccountKey(account)).ConfigureAwait(false);
        return await CountAsync().ConfigureAwait(false);
    }

    public async Task<int> SaveLocalAsync(LastFmFavoriteChange change, string? apiKey, string? account)
    {
        await InitializeAsync().ConfigureAwait(false);
        var db = connection();
        await db.RunInTransactionAsync(tx =>
        {
            int changed = tx.Execute("UPDATE Music SET IsFavorite = ?, [Order] = ? WHERE Id = ?", change.Loved, change.Order, change.MusicId);
            if (changed != 0 && !string.IsNullOrEmpty(account) && !string.IsNullOrEmpty(apiKey) && Eligible(change.Track))
                Put(tx, apiKey, account, change.Track, change.Loved, out _);
        }).ConfigureAwait(false);
        return await CountAsync().ConfigureAwait(false);
    }

    public async Task SaveOrderAsync(IReadOnlyList<LastFmFavoriteOrder> orders)
    {
        await InitializeAsync().ConfigureAwait(false);
        await connection().RunInTransactionAsync(tx =>
        {
            foreach (var item in orders)
                tx.Execute("UPDATE Music SET [Order] = ? WHERE Id = ? AND IsFavorite = 1", item.Order, item.MusicId);
        }).ConfigureAwait(false);
    }

    public async Task<int> ReadImportEndAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        return await connection().ExecuteScalarAsync<int>("SELECT COALESCE(MAX(Id), 0) FROM Music").ConfigureAwait(false);
    }

    public async Task<LastFmFavoriteImportBatch> ImportBatchAsync(string apiKey, string account, int afterId, int endId,
        IReadOnlySet<string>? changedTracks = null, CancellationToken cancellation = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        int queued = 0, ineligible = 0, added = 0, lastId = afterId, scanned = 0;
        // Read membership and stage this bounded page in the same transaction. The service yields
        // its gate between pages; tracks edited since import began must also stay protected after
        // an unlove has been acknowledged/deleted, including duplicate files on later pages.
        await connection().RunInTransactionAsync(tx =>
        {
            cancellation.ThrowIfCancellationRequested();
            var page = tx.Query<FavoriteMusicRow>(
                "SELECT Id, Title, Author FROM Music WHERE IsFavorite = 1 AND Id > ? AND Id <= ? ORDER BY Id LIMIT ?",
                afterId, endId, ImportBatchSize);
            foreach (var music in page)
            {
                cancellation.ThrowIfCancellationRequested();
                var track = new LastFmTrack(music.Title?.Trim() ?? "", music.Author?.Trim() ?? "", "", 0);
                if (!Eligible(track)) { ineligible++; continue; }
                string? trackKey = changedTracks is { Count: > 0 } ? LastFmHistory.TrackKey(track.Title, track.Artist) : null;
                if (trackKey is not null && changedTracks!.Contains(trackKey)) continue;
                if (Put(tx, apiKey, account, track, true, out bool newEntry, trackKey)) queued++;
                if (newEntry) added++;
            }
            scanned = page.Count;
            if (scanned != 0) lastId = page[^1].Id;
            cancellation.ThrowIfCancellationRequested();
        }).ConfigureAwait(false);
        return new(lastId, scanned, queued, ineligible, added);
    }

    public async Task<(int Pending, LastFmFavoriteImportResult Result)> ImportAsync(string apiKey, string account,
        CancellationToken cancellation = default)
    {
        int endId = await ReadImportEndAsync().ConfigureAwait(false);
        int afterId = 0, queued = 0, ineligible = 0;
        while (true)
        {
            var batch = await ImportBatchAsync(apiKey, account, afterId, endId, cancellation: cancellation).ConfigureAwait(false);
            queued += batch.Queued;
            ineligible += batch.Ineligible;
            if (batch.Scanned < ImportBatchSize || batch.LastId >= endId)
                return (await CountAsync().ConfigureAwait(false), new(queued, ineligible));
            afterId = batch.LastId;
            await Task.Yield();
        }
    }

    public async Task<LastFmFavoriteIntent?> PeekAsync(string apiKey, string account)
    {
        await InitializeAsync().ConfigureAwait(false);
        var rows = await connection().QueryAsync<FavoriteRow>(
            "SELECT * FROM LastFmFavoriteOutbox WHERE ApiKey = ? AND Account = ? ORDER BY rowid LIMIT 1", apiKey, AccountKey(account)).ConfigureAwait(false);
        if (rows.Count == 0) return null;
        var row = rows[0];
        var track = JsonSerializer.Deserialize(row.TrackJson, LastFmJsonContext.Default.LastFmTrack) ?? throw new JsonException();
        return new(row.Key, row.Version, track, row.Loved, row.Attempted);
    }

    public async Task<bool> MarkAttemptedAsync(LastFmFavoriteIntent intent)
        => await connection().ExecuteAsync("UPDATE LastFmFavoriteOutbox SET Attempted = 1 WHERE [Key] = ? AND Version = ?",
            intent.Key, intent.Version).ConfigureAwait(false) != 0;

    public async Task<int> CompleteAsync(LastFmFavoriteIntent intent)
    {
        await connection().ExecuteAsync("DELETE FROM LastFmFavoriteOutbox WHERE [Key] = ? AND Version = ?", intent.Key, intent.Version).ConfigureAwait(false);
        return await CountAsync().ConfigureAwait(false);
    }

    private Task<int> CountAsync() => connection().ExecuteScalarAsync<int>("SELECT COUNT(*) FROM LastFmFavoriteOutbox");
    public async Task<int> ReadPendingCountAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        return await CountAsync().ConfigureAwait(false);
    }
    private bool Eligible(LastFmTrack track) => track.Title.Length is > 0 and <= 512 && track.Artist.Length is > 0 and <= 512
        && !(unknownArtists?.Contains(track.Artist) ?? false);
    internal string? EligibleTrackKey(LastFmTrack track) => Eligible(track) ? LastFmHistory.TrackKey(track.Title, track.Artist) : null;
    private static string AccountKey(string? account) => account?.ToUpperInvariant() ?? "";
    private static bool Put(SQLiteConnection tx, string apiKey, string account, LastFmTrack track, bool loved,
        out bool added, string? trackKey = null)
    {
        string key = apiKey + ":" + AccountKey(account) + ":" + (trackKey ?? LastFmHistory.TrackKey(track.Title, track.Artist));
        var existing = tx.Find<FavoriteRow>(key);
        added = existing is null;
        if (existing?.Loved == loved) return false;
        tx.InsertOrReplace(new FavoriteRow
        {
            Key = key, ApiKey = apiKey, Account = AccountKey(account),
            Version = Guid.NewGuid().ToString("N"), TrackJson = JsonSerializer.Serialize(track, LastFmJsonContext.Default.LastFmTrack), Loved = loved
        }, typeof(FavoriteRow));
        return true;
    }

    [Table("LastFmFavoriteOutbox")]
    public sealed class FavoriteRow
    {
        public FavoriteRow() { }
        [PrimaryKey] public string Key { get; set; } = "";
        [Indexed] public string ApiKey { get; set; } = "";
        public string Account { get; set; } = "";
        public string Version { get; set; } = "";
        public string TrackJson { get; set; } = "";
        public bool Loved { get; set; }
        public bool Attempted { get; set; }
    }

    public sealed class FavoriteMusicRow
    {
        public FavoriteMusicRow() { }
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
    }
}
