using SQLite;
using WinUIMusicPlayer.Services.Recommendations;

internal static class CommunitySeedRegression
{
    public static async Task<int> RunAsync(DateTime now)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        string path = Path.Combine(Path.GetTempPath(), "community-seeds-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SQLiteAsyncConnection(path);
        try
        {
            await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY,Title TEXT,Author TEXT,IsFavorite INTEGER)");
            await db.ExecuteAsync("CREATE TABLE PlayList(Id INTEGER PRIMARY KEY)");
            await db.ExecuteAsync("CREATE TABLE PlayListMusic(PlayListId INTEGER,MusicId INTEGER)");
            await db.ExecuteAsync("CREATE TABLE PlaybackHistory(MusicId INTEGER,StartedAt INTEGER,TotalDurationMs REAL,DurationPlayedMs REAL)");
            for (int id = 1; id <= 20; id++) await db.ExecuteAsync("INSERT INTO Music VALUES(?,?,'Dominant',1)", id, "Song " + id);
            await db.ExecuteAsync("INSERT INTO Music VALUES(21,'Duet','Singer A & Singer B',1),(22,'Playlist only','Curated',0),(23,'Recently liked','Recent',0),(24,'Orphaned','Orphan',0),(25,'Too short','Short',0)");
            await db.ExecuteAsync("INSERT INTO PlayList VALUES(1),(2)");
            await db.ExecuteAsync("INSERT INTO PlayListMusic VALUES(1,22),(2,22),(999,24)");
            await db.ExecuteAsync("INSERT INTO PlaybackHistory VALUES(23,?,100000,100000),(25,?,100000,1000)", now.Ticks, now.Ticks);
            var candidates = await CommunitySeedQuery.ReadAsync(db, now, .5, default);
            Check(candidates.Count(item => item.Artist == "Dominant") == 1, "duplicate artist favorites cannot exhaust the community seed budget");
            Check(candidates.Single(item => item.Id == 22).IsPreferred, "playlist-only track is a positive community seed");
            Check(candidates.All(item => item.Id is not (24 or 25)), "orphaned playlist and unqualified short listen are not positive community seeds");
            Check(candidates.Single(item => item.Id == 23).ListeningWeight == 1, "community seeds reuse qualified recent listening aggregation");
            var seeds = CommunitySeedQuery.Select(candidates, static artist => artist.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            Check(seeds.Count == 5 && seeds.Select(seed => seed.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5,
                "community seeds use distinct split artist identities including the playlist-only preference");
            Check(seeds.Any(seed => seed.Artist == "Singer A") && seeds.Any(seed => seed.Artist == "Singer B"), "collaboration credits do not become an artificial combined artist API query");
            var many = Enumerable.Range(1, 100).Select(id => new CommunitySeedCandidate { Id = id, Artist = "Artist " + id, Title = "Title", IsPreferred = true }).ToList();
            Check(CommunitySeedQuery.Select(many, static artist => [artist]).Count == 8, "community request budget remains bounded to eight distinct seeds");
        }
        finally { await db.CloseAsync(); File.Delete(path); }
        return checks;
    }
}
