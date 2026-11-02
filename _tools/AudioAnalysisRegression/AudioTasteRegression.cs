using System.Runtime.InteropServices;
using SQLite;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.AudioAnalysis;

internal static class AudioTasteRegression
{
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            checks++;
            Console.WriteLine("PASS " + message);
        }
        string root = Path.Combine(AppContext.BaseDirectory, "taste-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new SQLiteAsyncConnection(Path.Combine(root, "taste.db"));
        try
        {
            await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY,SourceId INTEGER,Path TEXT,Title TEXT)");
            await db.ExecuteAsync("CREATE TABLE RemoteTrack(MusicId INTEGER PRIMARY KEY,SourceId INTEGER,Href TEXT,Length INTEGER,ETag TEXT,Modified TEXT,Missing INTEGER)");
            await db.ExecuteAsync("CREATE TABLE WebDavSource(Id INTEGER PRIMARY KEY,Enabled INTEGER)");
            var repository = new AnalysisRepository(db);
            await repository.InitializeAsync();
            for (int id = 1; id <= 20; id++)
            {
                var raw = new float[384];
                for (int clip = 0; clip < 3; clip++) raw[clip * 128 + (id <= 4 ? 0 : 1)] = id % 2 == 1 ? 1 : -1;
                await db.ExecuteAsync("INSERT INTO Music VALUES(?,0,?,'Fixture')", id, "source-" + id);
                await repository.SaveAsync(new AnalysisRecord { MusicId = id, SourceKey = "source-" + id, Data = MemoryMarshal.AsBytes(raw.AsSpan()).ToArray() });
            }
            var tracks = Enumerable.Range(1, 20).Select(id => new RecommendationTrack(id, ["Artist " + id], "", "", "", 2020, id <= 2, true, 0)).ToArray();
            var scores = await AudioPreferenceScorer.ScoreAsync(repository, tracks, [], default);
            Check(scores.Count == 20 && scores[3] >= .8 && scores[4] >= .8,
                "two opposite favorite styles remain independently recommendable");
            Check(scores[5] < .2 && scores[6] < .2, "unrelated acoustic style is not made similar by averaging preferences");
            var playlist = tracks.Select(track => track with { IsFavorite = false, IsInPlaylist = track.Id == 1 }).ToArray();
            var playlistScores = await AudioPreferenceScorer.ScoreAsync(repository, playlist, [], default);
            Check(playlistScores[3] >= .8 && playlistScores[4] < .2, "playlist-only preference trains the acoustic model");
            var overlap = await AudioPreferenceScorer.ScoreAsync(repository, playlist.Select(track => track with { IsFavorite = track.IsInPlaylist }).ToArray(), [], default);
            Check(overlap.All(pair => Math.Abs(pair.Value - playlistScores[pair.Key]) < 1e-6), "overlapping playlists and favorites do not duplicate acoustic preference weight");
            Check((await AudioPreferenceScorer.ScoreAsync(repository, tracks.Select(track => track with { IsFavorite = false }).ToArray(), [], default)).Count == 0,
                "absent positive preferences do not manufacture an acoustic profile");
            Check((await AudioPreferenceScorer.ScoreAsync(repository, tracks, [], default, static () => false)).Count == 0,
                "disabled scoring does not read retained vectors");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await AudioPreferenceScorer.ScoreAsync(repository, tracks, [], cancellation.Token);
                throw new Exception("Scoring ignored cancellation");
            }
            catch (OperationCanceledException) { Check(true, "streaming acoustic scoring observes cancellation"); }
            var repeated = await AudioPreferenceScorer.ScoreAsync(repository, tracks, [], default);
            Check(repeated.All(pair => Math.Abs(pair.Value - scores[pair.Key]) < 1e-6), "acoustic preference fitting is deterministic for fixed inputs");
        }
        finally { await db.CloseAsync(); }
        Console.WriteLine($"PASS {checks} acoustic taste checks");
    }
}
