using SQLite;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

/// <summary>An immutable UI snapshot; later clicks must not mutate an earlier database write.</summary>
public sealed record LastFmFavoriteChange(int MusicId, LastFmTrack Track, bool Loved, int Order);
public sealed record LastFmFavoriteImportResult(int Queued, int Ineligible);
public sealed record LastFmFavoriteIntent(string Key, string Version, LastFmTrack Track, bool Loved, bool Attempted);

/// <summary>Borrows the library connection. Local changes and their outbox entry share one transaction.</summary>
public sealed class LastFmFavoriteStore(Func<SQLiteAsyncConnection> connection, HashSet<string>? unknownArtists = null)
{
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
                Put(tx, apiKey, account, change.Track, change.Loved);
        }).ConfigureAwait(false);
        return await CountAsync().ConfigureAwait(false);
    }

    public async Task<(int Pending, LastFmFavoriteImportResult Result)> ImportAsync(string apiKey, string account)
    {
        await InitializeAsync().ConfigureAwait(false);
        var db = connection();
        int queued = 0, ineligible = 0;
        // The transaction reads the current library, not an earlier UI snapshot. An unlove cannot
        // interleave halfway through an import and be overwritten by stale favorite data.
        await db.RunInTransactionAsync(tx =>
        {
            int afterId = 0;
            while (true)
            {
                var page = tx.Query<FavoriteMusicRow>("SELECT Id, Title, Author FROM Music WHERE IsFavorite = 1 AND Id > ? ORDER BY Id LIMIT 512", afterId);
                foreach (var music in page)
                {
                    var track = new LastFmTrack(music.Title?.Trim() ?? "", music.Author?.Trim() ?? "", "", 0);
                    if (!Eligible(track)) { ineligible++; continue; }
                    // Duplicate files representing one artist/title share the same desired state.
                    if (Put(tx, apiKey, account, track, true)) queued++;
                }
                if (page.Count < 512) break;
                afterId = page[^1].Id;
            }
        }).ConfigureAwait(false);
        return (await CountAsync().ConfigureAwait(false), new(queued, ineligible));
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
    private static string AccountKey(string? account) => account?.ToUpperInvariant() ?? "";
    private static string Key(string apiKey, string account, LastFmTrack track)
        => apiKey + ":" + AccountKey(account) + ":" + LastFmHistory.TrackKey(track.Title, track.Artist);
    private static bool Put(SQLiteConnection tx, string apiKey, string account, LastFmTrack track, bool loved)
    {
        string key = Key(apiKey, account, track);
        if (tx.Find<FavoriteRow>(key)?.Loved == loved) return false;
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
