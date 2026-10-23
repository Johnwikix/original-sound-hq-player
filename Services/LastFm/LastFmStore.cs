using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

public interface ILastFmStore
{
    Task<LastFmSavedState> LoadAsync(CancellationToken token);
    Task SaveAsync(LastFmSavedState state, CancellationToken token);
    Task CommitAsync(LastFmSavedState state, LastFmAcknowledgement? acknowledgement,
        IReadOnlyCollection<LastFmHistoryReceipt> receipts, CancellationToken token);
    Task<Dictionary<string, LastFmHistoryMapping>> ReadMappingsAsync(string account, IReadOnlyList<string> keys, CancellationToken token);
    Task<LastFmAliasMatches> ReadAliasesAsync(string account, IReadOnlyList<LastFmAlias> aliases, CancellationToken token);
    Task<List<LastFmKnownPlay>> ReadKnownAsync(string account, string apiKey, CancellationToken token);
    Task<List<long>> ReadReservedTimesAsync(string account, long from, long to, CancellationToken token);
    Task<int> CountHistoryAsync(bool acceptedOnly, CancellationToken token);
    Task CloseAsync();
    Task<LastFmIdentity?> ReadIdentityAsync(CancellationToken token);
    Task SaveIdentityAsync(LastFmIdentity? identity, CancellationToken token);
}

public interface ILastFmCredentialVault
{
    Task<string?> ReadAsync(CancellationToken token);
    Task WriteAsync(string? value, CancellationToken token);
}

/// <summary>SQLite transactions own queue state, upload reservations and acknowledgements. Secrets stay in PasswordVault.</summary>
public sealed class LastFmStore(ILastFmCredentialVault vault, string path) : ILastFmStore, IAsyncDisposable
{
    private readonly string _legacyPath = path;
    public string DatabasePath { get; } = Path.ChangeExtension(path, ".db");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SQLiteAsyncConnection? _database;
    private bool _initialized;
    private Dictionary<Guid, LastFmScrobble> _pending = [];
    private DateTimeOffset _lastPrune;

    public LastFmStore(ILastFmCredentialVault vault) : this(vault,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Settings", "LastFm.json")) { }

    public async Task<LastFmSavedState> LoadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureAsync(token).ConfigureAwait(false);
            var rows = await _database!.QueryAsync<StateRow>("SELECT Json FROM LastFmState WHERE Id=1").ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var state = JsonSerializer.Deserialize(rows[0].Json, LastFmJsonContext.Default.LastFmSavedState) ?? throw new JsonException();
            Validate(state);
            return state;
        }
        finally { _gate.Release(); }
    }

    public Task SaveAsync(LastFmSavedState state, CancellationToken token) => CommitAsync(state, null, Array.Empty<LastFmHistoryReceipt>(), token);

    public async Task CommitAsync(LastFmSavedState state, LastFmAcknowledgement? acknowledgement,
        IReadOnlyCollection<LastFmHistoryReceipt> receipts, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureAsync(token).ConfigureAwait(false);
            // Warm commits can start on the WinUI sampling thread. Keep work proportional to
            // the sending queue off that thread, even when both semaphore waits complete inline.
            var prepared = await Task.Run(() => Prepare(state), token).ConfigureAwait(false);
            bool prune = DateTimeOffset.UtcNow - _lastPrune > TimeSpan.FromMinutes(1);
            token.ThrowIfCancellationRequested();
            // Cancellation never abandons the real SQLite transaction or rolls memory forward before commit.
            await _database!.RunInTransactionAsync(connection =>
            {
                int ledgerCount = -1;
                foreach (var play in prepared.Added) Reserve(connection, play, ref ledgerCount);
                foreach (var receipt in state.HistoryReceipts) Accept(connection, receipt, ref ledgerCount);
                foreach (var receipt in receipts) Accept(connection, receipt, ref ledgerCount);
                foreach (var known in state.Known) SaveKnown(connection, known);
                if (acknowledgement is { } ack)
                {
                    var play = ack.Play;
                    if (ack.Accepted)
                    {
                        bool retimed = play.HistoryKey is not null && play.OriginalStartedAt != play.StartedAt;
                        if (play.HistoryKey is not null) Accept(connection, Receipt(play), ref ledgerCount);
                        if (play.StartedAt >= LastFmHistory.OldestTimestamp)
                            SaveKnown(connection, new(play.ApiKey, play.Account, LastFmHistory.TrackKey(play.Track.Title, play.Track.Artist), play.StartedAt, retimed));
                    }
                    else if (play.HistoryKey is not null)
                        connection.Execute("DELETE FROM LastFmHistoryMap WHERE Key=? AND Accepted=0", MapKey(play.Account, play.HistoryKey));
                }
                if (prune)
                {
                    connection.Execute("DELETE FROM LastFmKnown WHERE StartedAt < ?", LastFmHistory.OldestTimestamp);
                    connection.Execute("DELETE FROM LastFmKnown WHERE rowid IN (SELECT rowid FROM LastFmKnown ORDER BY StartedAt DESC LIMIT -1 OFFSET 100000)");
                }
                connection.Execute("INSERT OR REPLACE INTO LastFmState(Id,Json) VALUES(1,?)", prepared.Json);
            }).ConfigureAwait(false);
            _pending = prepared.Pending;
            if (prune) _lastPrune = DateTimeOffset.UtcNow;
        }
        finally { _gate.Release(); }
    }

    private (string Json, List<LastFmScrobble> Added, Dictionary<Guid, LastFmScrobble> Pending) Prepare(LastFmSavedState state)
    {
        Validate(state);
        var persisted = state with { Version = 2, Known = [], HistoryReceipts = [] };
        string json = JsonSerializer.Serialize(persisted, LastFmJsonContext.Default.LastFmSavedState);
        if (json.Length > 160 * 1024 * 1024) throw new IOException("Last.fm queue is too large.");
        var added = new List<LastFmScrobble>();
        var pending = new Dictionary<Guid, LastFmScrobble>(state.Pending.Count);
        foreach (var play in state.Pending)
        {
            pending.Add(play.Id, play);
            if (play.HistoryKey is not null && (!_pending.TryGetValue(play.Id, out var previous) || previous != play)) added.Add(play);
        }
        return (json, added, pending);
    }

    public async Task<Dictionary<string, LastFmHistoryMapping>> ReadMappingsAsync(string account, IReadOnlyList<string> keys, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureAsync(token).ConfigureAwait(false);
            var result = new Dictionary<string, LastFmHistoryMapping>(StringComparer.Ordinal);
            for (int offset = 0; offset < keys.Count; offset += 500)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(500, keys.Count - offset);
                var values = new object[count];
                for (int i = 0; i < count; i++) values[i] = MapKey(account, keys[offset + i]);
                var rows = await _database!.QueryAsync<MappingRow>("SELECT * FROM LastFmHistoryMap WHERE Key IN (" + Parameters(count) + ")", values).ConfigureAwait(false);
                foreach (var row in rows)
                    result[row.HistoryKey] = new(row.Account, row.HistoryKey, row.TrackKey, row.OriginalStartedAt, row.SubmittedAt, row.Accepted != 0, row.Retimed != 0,
                        row.TrackJson is null ? null : JsonSerializer.Deserialize(row.TrackJson, LastFmJsonContext.Default.LastFmTrack));
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<LastFmAliasMatches> ReadAliasesAsync(string account, IReadOnlyList<LastFmAlias> aliases, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureAsync(token).ConfigureAwait(false);
            var retimed = new HashSet<LastFmAlias>();
            var uncertain = new HashSet<LastFmAlias>();
            for (int offset = 0; offset < aliases.Count; offset += 500)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(500, aliases.Count - offset);
                var values = new object[count];
                for (int i = 0; i < count; i++) values[i] = AliasKey(account, aliases[offset + i].TrackKey, aliases[offset + i].SubmittedAt);
                string parameters = Parameters(count);
                var mappings = await _database!.QueryAsync<AliasRow>("SELECT TrackKey,SubmittedAt FROM LastFmHistoryMap WHERE Retimed=1 AND AliasKey IN (" + parameters + ")", values).ConfigureAwait(false);
                foreach (var row in mappings) retimed.Add(new(row.TrackKey, row.SubmittedAt));
                var legacy = await _database.QueryAsync<AliasRow>("SELECT TrackKey,StartedAt AS SubmittedAt FROM LastFmKnown WHERE Uncertain=1 AND AliasKey IN (" + parameters + ")", values).ConfigureAwait(false);
                foreach (var row in legacy) uncertain.Add(new(row.TrackKey, row.SubmittedAt));
            }
            token.ThrowIfCancellationRequested();
            return new(retimed, uncertain);
        }
        finally { _gate.Release(); }
    }

    public async Task<List<LastFmKnownPlay>> ReadKnownAsync(string account, string apiKey, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureAsync(token).ConfigureAwait(false);
            var rows = await _database!.QueryAsync<KnownRow>("SELECT * FROM LastFmKnown WHERE Account=? AND ApiKey=? AND StartedAt>=? ORDER BY StartedAt LIMIT 100000",
                account.ToUpperInvariant(), apiKey, LastFmHistory.OldestTimestamp).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = new List<LastFmKnownPlay>(rows.Count);
            foreach (var row in rows) result.Add(new(row.ApiKey, row.Account, row.TrackKey, row.StartedAt, row.Retimed != 0, row.Uncertain != 0));
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> CountHistoryAsync(bool acceptedOnly, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureAsync(token).ConfigureAwait(false);
            int count = await _database!.ExecuteScalarAsync<int>(acceptedOnly ? "SELECT COUNT(*) FROM LastFmHistoryMap WHERE Accepted=1" : "SELECT COUNT(*) FROM LastFmHistoryMap").ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return count;
        }
        finally { _gate.Release(); }
    }

    public async Task<List<long>> ReadReservedTimesAsync(string account, long from, long to, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureAsync(token).ConfigureAwait(false);
            var rows = await _database!.QueryAsync<AliasRow>(
                "SELECT SubmittedAt FROM LastFmHistoryMap WHERE Account=? AND SubmittedAt BETWEEN ? AND ?",
                account.ToUpperInvariant(), from, to).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var times = new List<long>(rows.Count);
            foreach (var row in rows) times.Add(row.SubmittedAt);
            return times;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureAsync(CancellationToken token)
    {
        if (_initialized) return;
        token.ThrowIfCancellationRequested();
        await Task.Run(() => Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!), token).ConfigureAwait(false);
        _database ??= new SQLiteAsyncConnection(DatabasePath);
        await _database.ExecuteAsync("CREATE TABLE IF NOT EXISTS LastFmState(Id INTEGER PRIMARY KEY,Json TEXT NOT NULL)").ConfigureAwait(false);
        await _database.ExecuteAsync("CREATE TABLE IF NOT EXISTS LastFmHistoryMap(Key TEXT PRIMARY KEY,Account TEXT NOT NULL,HistoryKey TEXT NOT NULL,TrackKey TEXT NOT NULL,OriginalStartedAt INTEGER NOT NULL,SubmittedAt INTEGER NOT NULL,Accepted INTEGER NOT NULL,Retimed INTEGER NOT NULL,AliasKey TEXT NOT NULL,TrackJson TEXT)").ConfigureAwait(false);
        await _database.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_LastFmMap_Alias ON LastFmHistoryMap(AliasKey)").ConfigureAwait(false);
        await _database.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_LastFmMap_Time ON LastFmHistoryMap(Account,SubmittedAt)").ConfigureAwait(false);
        await _database.ExecuteAsync("CREATE TABLE IF NOT EXISTS LastFmKnown(Key TEXT PRIMARY KEY,ApiKey TEXT NOT NULL,Account TEXT NOT NULL,TrackKey TEXT NOT NULL,StartedAt INTEGER NOT NULL,Retimed INTEGER NOT NULL,Uncertain INTEGER NOT NULL,AliasKey TEXT NOT NULL)").ConfigureAwait(false);
        await _database.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_LastFmKnown_Time ON LastFmKnown(StartedAt)").ConfigureAwait(false);
        await _database.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_LastFmKnown_Alias ON LastFmKnown(AliasKey)").ConfigureAwait(false);
        await _database.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_LastFmKnown_Account ON LastFmKnown(Account,ApiKey,StartedAt)").ConfigureAwait(false);
        var rows = await _database.QueryAsync<StateRow>("SELECT Json FROM LastFmState WHERE Id=1").ConfigureAwait(false);
        LastFmSavedState state;
        if (rows.Count != 0) state = JsonSerializer.Deserialize(rows[0].Json, LastFmJsonContext.Default.LastFmSavedState) ?? throw new JsonException();
        else
        {
            state = new();
            if (File.Exists(_legacyPath))
            {
                if (new FileInfo(_legacyPath).Length > 160 * 1024 * 1024) throw new IOException("Last.fm legacy state is too large.");
                state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_legacyPath, token).ConfigureAwait(false), LastFmJsonContext.Default.LastFmSavedState) ?? throw new JsonException();
            }
            Validate(state);
            var legacyAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var receipt in state.HistoryReceipts)
                if (receipt.SubmittedAt == 0) legacyAccounts.Add(receipt.Account);
            string json = JsonSerializer.Serialize(state with { Version = 2, Known = [], HistoryReceipts = [] }, LastFmJsonContext.Default.LastFmSavedState);
            token.ThrowIfCancellationRequested();
            await _database.RunInTransactionAsync(connection =>
            {
                int ledgerCount = -1;
                foreach (var receipt in state.HistoryReceipts) Accept(connection, receipt, ref ledgerCount);
                foreach (var play in state.Pending) if (play.HistoryKey is not null) Reserve(connection, play, ref ledgerCount);
                foreach (var known in state.Known) SaveKnown(connection, known with { Uncertain = known.Uncertain || legacyAccounts.Contains(known.Account) });
                connection.Execute("INSERT INTO LastFmState(Id,Json) VALUES(1,?)", json);
            }).ConfigureAwait(false);
        }
        Validate(state);
        var pending = new Dictionary<Guid, LastFmScrobble>(state.Pending.Count);
        foreach (var play in state.Pending) pending.Add(play.Id, play);
        _pending = pending;
        _initialized = true;
    }

    private static void Reserve(SQLiteConnection connection, LastFmScrobble play, ref int ledgerCount)
    {
        string key = MapKey(play.Account, play.HistoryKey!);
        bool exists = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM LastFmHistoryMap WHERE Key=?", key) != 0;
        if (!exists) CheckCapacity(connection, ref ledgerCount);
        string trackKey = LastFmHistory.TrackKey(play.Track.Title, play.Track.Artist);
        connection.Execute("INSERT INTO LastFmHistoryMap(Key,Account,HistoryKey,TrackKey,OriginalStartedAt,SubmittedAt,Accepted,Retimed,AliasKey,TrackJson) VALUES(?,?,?,?,?,?,0,?,?,?) ON CONFLICT(Key) DO UPDATE SET TrackKey=excluded.TrackKey,OriginalStartedAt=excluded.OriginalStartedAt,SubmittedAt=excluded.SubmittedAt,Retimed=excluded.Retimed,AliasKey=excluded.AliasKey,TrackJson=excluded.TrackJson WHERE Accepted=0",
            key, play.Account.ToUpperInvariant(), play.HistoryKey!, trackKey, play.OriginalStartedAt, play.StartedAt,
            play.OriginalStartedAt != play.StartedAt ? 1 : 0, AliasKey(play.Account, trackKey, play.StartedAt),
            JsonSerializer.Serialize(play.Track, LastFmJsonContext.Default.LastFmTrack));
    }

    private static void Accept(SQLiteConnection connection, LastFmHistoryReceipt receipt, ref int ledgerCount)
    {
        string key = MapKey(receipt.Account, receipt.HistoryKey);
        bool exists = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM LastFmHistoryMap WHERE Key=?", key) != 0;
        if (!exists) CheckCapacity(connection, ref ledgerCount);
        connection.Execute("INSERT INTO LastFmHistoryMap(Key,Account,HistoryKey,TrackKey,OriginalStartedAt,SubmittedAt,Accepted,Retimed,AliasKey,TrackJson) VALUES(?,?,?,?,?,?,1,?,?,NULL) ON CONFLICT(Key) DO UPDATE SET Accepted=1,TrackJson=NULL,TrackKey=CASE WHEN excluded.TrackKey='' THEN TrackKey ELSE excluded.TrackKey END,OriginalStartedAt=CASE WHEN excluded.OriginalStartedAt=0 THEN OriginalStartedAt ELSE excluded.OriginalStartedAt END,SubmittedAt=CASE WHEN excluded.SubmittedAt=0 THEN SubmittedAt ELSE excluded.SubmittedAt END,Retimed=CASE WHEN excluded.SubmittedAt=0 THEN Retimed ELSE excluded.Retimed END,AliasKey=CASE WHEN excluded.SubmittedAt=0 THEN AliasKey ELSE excluded.AliasKey END",
            key, receipt.Account.ToUpperInvariant(), receipt.HistoryKey, receipt.TrackKey, receipt.OriginalStartedAt, receipt.SubmittedAt,
            receipt.Retimed ? 1 : 0, AliasKey(receipt.Account, receipt.TrackKey, receipt.SubmittedAt));
    }

    private static void CheckCapacity(SQLiteConnection connection, ref int ledgerCount)
    {
        if (ledgerCount < 0) ledgerCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM LastFmHistoryMap");
        if (++ledgerCount > LastFmHistory.ReceiptLimit)
            throw new IOException("Last.fm import ledger is full.");
    }

    private static void SaveKnown(SQLiteConnection connection, LastFmKnownPlay known)
        => connection.Execute("INSERT INTO LastFmKnown(Key,ApiKey,Account,TrackKey,StartedAt,Retimed,Uncertain,AliasKey) VALUES(?,?,?,?,?,?,?,?) ON CONFLICT(Key) DO UPDATE SET Retimed=excluded.Retimed,Uncertain=excluded.Uncertain WHERE excluded.Uncertain=0",
            known.ApiKey + ":" + AliasKey(known.Account, known.TrackKey, known.StartedAt), known.ApiKey, known.Account.ToUpperInvariant(), known.TrackKey,
            known.StartedAt, known.Retimed ? 1 : 0, known.Uncertain ? 1 : 0, AliasKey(known.Account, known.TrackKey, known.StartedAt));

    internal static LastFmHistoryReceipt Receipt(LastFmScrobble play) => new(play.Account.ToUpperInvariant(), play.HistoryKey!,
        LastFmHistory.TrackKey(play.Track.Title, play.Track.Artist), play.OriginalStartedAt, play.StartedAt, play.Retimed || play.OriginalStartedAt != play.StartedAt);
    private static string MapKey(string account, string key) => account.ToUpperInvariant() + ":" + key;
    private static string AliasKey(string account, string trackKey, long time) => account.ToUpperInvariant() + ":" + trackKey + ":" + time.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Parameters(int count) => string.Join(",", new string('?', count).ToCharArray());

    private static void Validate(LastFmSavedState state)
    {
        if (state.Version is not (1 or 2) || state.Pending is null || state.Pending.Count > 10000 || state.Known is null || state.Known.Count > 100000
            || state.HistoryReceipts is null || state.HistoryReceipts.Count > LastFmHistory.ReceiptLimit) throw new JsonException();
        foreach (var receipt in state.HistoryReceipts)
            if (receipt is null || string.IsNullOrWhiteSpace(receipt.Account) || receipt.HistoryKey is not { Length: 64 }) throw new JsonException();
        foreach (var known in state.Known)
            if (known is null || string.IsNullOrEmpty(known.ApiKey) || string.IsNullOrEmpty(known.Account) || known.TrackKey is not { Length: 64 } || known.StartedAt <= 0) throw new JsonException();
        foreach (var play in state.Pending)
            if (play is null || play.Id == Guid.Empty || play.Track is null || string.IsNullOrEmpty(play.ApiKey) || string.IsNullOrEmpty(play.Account)
                || string.IsNullOrWhiteSpace(play.Track.Title) || string.IsNullOrWhiteSpace(play.Track.Artist) || play.Track.Album is null || play.StartedAt <= 0
                || (play.HistoryKey is not null && (play.HistoryKey.Length != 64 || play.OriginalStartedAt <= 0))) throw new JsonException();
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_database is not null) await _database.CloseAsync().ConfigureAwait(false);
            _database = null;
            _initialized = false;
            _pending.Clear();
        }
        finally { _gate.Release(); }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());

    public async Task<LastFmIdentity?> ReadIdentityAsync(CancellationToken token)
    {
        string? value = await vault.ReadAsync(token).ConfigureAwait(false);
        if (value is null) return null;
        var identity = JsonSerializer.Deserialize(value, LastFmJsonContext.Default.LastFmIdentity);
        if (identity is not null && (identity.Credentials is not { ApiKey.Length: 32, SharedSecret.Length: 32 }
            || identity.Account is { Name: null } or { SessionKey: null })) throw new JsonException();
        return identity;
    }
    public Task SaveIdentityAsync(LastFmIdentity? identity, CancellationToken token) => vault.WriteAsync(
        identity is null ? null : JsonSerializer.Serialize(identity, LastFmJsonContext.Default.LastFmIdentity), token);

    private sealed class StateRow { public StateRow() { } public string Json { get; set; } = ""; }
    private sealed class MappingRow
    {
        public MappingRow() { }
        public string Account { get; set; } = "";
        public string HistoryKey { get; set; } = "";
        public string TrackKey { get; set; } = "";
        public long OriginalStartedAt { get; set; }
        public long SubmittedAt { get; set; }
        public int Accepted { get; set; }
        public int Retimed { get; set; }
        public string? TrackJson { get; set; }
    }
    private sealed class KnownRow
    {
        public KnownRow() { }
        public string ApiKey { get; set; } = "";
        public string Account { get; set; } = "";
        public string TrackKey { get; set; } = "";
        public long StartedAt { get; set; }
        public int Retimed { get; set; }
        public int Uncertain { get; set; }
    }
    private sealed class AliasRow
    {
        public AliasRow() { }
        public string TrackKey { get; set; } = "";
        public long SubmittedAt { get; set; }
    }
}
