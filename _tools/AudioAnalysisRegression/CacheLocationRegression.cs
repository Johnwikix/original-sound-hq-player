using SQLite;
using WinUIMusicPlayer.Services.AudioAnalysis;

internal static class CacheLocationRegression
{
    internal static async Task RunAsync()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "cache-location", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var database = new SQLiteAsyncConnection(Path.Combine(fixture, "library.db"));
        try
        {
            await database.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY,SourceId INTEGER,Path TEXT,Title TEXT)");
            await database.ExecuteAsync("CREATE TABLE RemoteTrack(MusicId INTEGER PRIMARY KEY,Href TEXT,Length INTEGER,ETag TEXT,Modified TEXT,Missing INTEGER)");
            await database.ExecuteAsync("CREATE TABLE WebDavSource(Id INTEGER PRIMARY KEY,Enabled INTEGER)");
            await database.ExecuteAsync("INSERT INTO Music VALUES(1,0,'local.flac','Preserved library')");
            var legacy = new AnalysisRepository(database);
            await legacy.InitializeAsync();
            await legacy.SaveEnabledAsync(false);
            await legacy.SaveAsync(new() { MusicId = 1, SourceKey = "local.flac", Fingerprint = "legacy", Data = new byte[1536] });
            string root = Path.Combine(fixture, "first");
            string first = root;
            var repository = new AnalysisRepository(database, () => root);
            await repository.InitializeAsync();
            Require(!Directory.Exists(root), "initializing settings must not open the cache before preferences are restored");
            Require((await repository.ReadVectorsAsync(0)).Count == 1, "legacy feature rows must migrate");
            Require(File.Exists(Path.Combine(root, "Analysis", "Features.db")), "features must reside under the unified root");
            Require(await database.ExecuteScalarAsync<int>("SELECT count(*) FROM main.sqlite_master WHERE name='AnalysisRecord'") == 0, "main database must no longer contain the derived cache table");
            Require(!await repository.ReadEnabledAsync(), "moving cache must preserve the persistent switch");
            Require((await repository.FindAsync(1))?.Fingerprint == "legacy", "migration must preserve the file version");
            root = Path.Combine(fixture, "second");
            Require((await repository.ReadVectorsAsync(0)).Count == 0, "a new cache location starts empty");
            await repository.SaveAsync(new() { MusicId = 1, SourceKey = "local.flac", Fingerprint = "new", Data = new byte[1536] });
            Require((await repository.FindAsync(1))?.Fingerprint == "new", "writes follow the new cache root");
            root = first;
            Require((await repository.FindAsync(1))?.Fingerprint == "legacy", "switching back must reuse the previous root without overwriting it");
            string blocked = Path.Combine(fixture, "blocked");
            File.WriteAllText(blocked, "file, not directory");
            root = blocked;
            try { await repository.ReadVectorsAsync(0); throw new Exception("Inaccessible cache unexpectedly opened"); }
            catch (IOException) { }
            root = first;
            Require((await repository.ReadVectorsAsync(0)).Count == 1, "failed root change must leave the previous cache recoverable");
            Require(await database.ExecuteScalarAsync<int>("SELECT count(*) FROM Music") == 1, "cache migration must preserve library data");
            Console.WriteLine("PASS unified feature cache: lazy open, legacy migration, settings preservation, root switch, reuse and failed-path recovery");
        }
        finally { await database.CloseAsync(); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
