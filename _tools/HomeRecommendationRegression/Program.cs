using System.Diagnostics;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
var service = new HomeRecommendationService();
var now = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
RecommendationTrack Track(int id, string artist = "", string genre = "", string album = "", bool favorite = false, bool available = true)
    => new(id, [artist], album, artist, genre, 2000 + id % 25, favorite, available, 0);
var library = new List<RecommendationTrack>();
for (int i = 1; i <= 80; i++) library.Add(Track(i, "Artist " + i % 20, i <= 60 ? "Pop" : "Jazz", "Album " + i % 25));
var cold = service.Build(library, [], now, 0, default);
Check(cold.IsColdStart && cold.Songs.Count == 12 && cold.Familiar.Count == 0, "cold start fills a useful shelf without pretending to have history");
Check(cold.Songs.Select(x => x.MusicId).Distinct().Count() == 12, "recommendations never repeat a song");
Check(cold.Songs.Select(x => library[x.MusicId - 1].Genre).Distinct().Count() == 2, "minority genres survive a dominant genre");
Check(cold.Songs.GroupBy(x => library[x.MusicId - 1].Artists[0]).All(g => g.Count() <= 2), "available artists respect the two-song quota");
Check(cold.Songs.GroupBy(x => (library[x.MusicId - 1].AlbumArtist, library[x.MusicId - 1].Album)).All(g => g.Count() == 1), "available albums are spread across the shelf without merging namesakes");
Check(service.Build(library, [], now, 0, default).Songs.SequenceEqual(cold.Songs), "same inputs and day produce stable recommendations");
Check(!service.Build(library, [], now, 1, default).Songs.SequenceEqual(cold.Songs), "refresh changes the batch");
Check(cold.Discovery.All(x => !cold.Songs.Any(s => s.MusicId == x.MusicId)), "discovery adds songs absent from the primary shelf");
Check(HomeRecommendationService.SplitGenres(" Jazz ;Pop|jazz,Unknown Genre, ,不明 ").SequenceEqual(["Jazz", "Pop"]), "multi-genre tags are deduplicated and unknown placeholders are ignored");
Check(HomeRecommendationService.SplitGenres("R&B/Soul").Single() == "R&B/Soul", "compound genre names are retained");
Check(HomeRecommendationService.SplitGenres("Other;Unknown Wave").SequenceEqual(["Other", "Unknown Wave"]),
    "real genre names are not discarded by broad placeholder prefixes");
var unknownNamedArtist = service.Build([Track(1, "Unknown Mortal Orchestra", "Rock", "Unknown Pleasures", true),
    Track(2, "Unknown Mortal Orchestra", "Rock", "Other Album")], [], now, 0, default);
Check(unknownNamedArtist.Songs.Any(song => song.Reason == RecommendationReason.Artist && song.Detail == "Unknown Mortal Orchestra"),
    "real artists starting with Unknown remain preference features");
var sparse = Enumerable.Range(1, 24).Select(i => Track(i)).ToArray();
Check(service.Build(sparse, [], now, 0, default).Songs.Count == 12, "missing artist, album and genre never discard songs");
Check(service.Build(Enumerable.Range(1, 30).Select(i => Track(i, "Artist " + i, "Pop")).ToArray(), [], now, 0, default).Discovery.Count == 0,
    "single-genre library does not claim to offer another genre");
Check(service.Build([Track(1)], [], now, 0, default).Songs.Count == 1, "one-song library relaxes quotas without manufacturing duplicates");
Check(service.Build([], [], now, 0, default).Songs.Count == 0, "empty library has no phantom recommendations");
Check(service.Build([Track(1, available: false)], [], now, 0, default).Songs.Count == 0, "known unavailable sources do not occupy playback slots");
Check(service.Build([Track(1, genre: "Jazz", favorite: true)], [], now, 0, default).IsColdStart == false, "favorites seed personalization without listening history");
Check(service.Build([Track(1)], [new() { MusicId = 999, Weight = 100, Count = 100 }], now, 0, default).IsColdStart,
    "deleted-song history cannot seed an unrelated profile");
var personalized = service.Build(library, [new() { MusicId = 61, Weight = 10, Count = 8, LastPlayedTicks = now.AddDays(-2).Ticks }], now, 0, default);
Check(!personalized.IsColdStart && personalized.Familiar.Count == 1 && personalized.Familiar[0].MusicId == 61,
    "effective listening produces a separate familiar shelf");
Check(personalized.Songs.Any(x => x.Reason == RecommendationReason.Genre && x.Detail == "Jazz"), "genre affinity produces an evidence-based explanation");
using (var stop = new CancellationTokenSource())
{
    stop.Cancel();
    try { service.Build(library, [], now, 0, stop.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Check(true, "recommendation computation observes cancellation"); }
}
foreach (string entry in new[] { "Home", "AddFolder", "MusicBrowse", "PlayLists", "Stats" })
    Check(StartupEntry.Normalize(entry) == entry, "existing startup choice remains: " + entry);
Check(StartupEntry.Normalize("removed-page") == "Home" && StartupEntry.Normalize(null) == "Home", "invalid startup choice falls back to Home");

string path = Path.Combine(Path.GetTempPath(), "home-recommendation-" + Guid.NewGuid().ToString("N") + ".db");
var db = new SQLiteAsyncConnection(path);
try
{
    await db.ExecuteAsync("CREATE TABLE PlaybackHistory (MusicId INTEGER, StartedAt INTEGER, TotalDurationMs REAL, DurationPlayedMs REAL)");
    async Task Insert(int id, double days, double duration, double played) =>
        _ = await db.ExecuteAsync("INSERT INTO PlaybackHistory VALUES (?,?,?,?)", id, now.AddDays(-days).Ticks, duration, played);
    await Insert(1, 0, 100000, 100000);
    await Insert(1, 30, 100000, 100000);
    await Insert(2, 1, 100000, 49999);
    await Insert(3, 91, 100000, 100000);
    await Insert(4, -1, 100000, 100000);
    await Insert(5, 0, 0, 1000);
    await Insert(6, 0, 100000, 50000);
    var signals = await HomeListeningQuery.ReadAsync(db, now, .5, default);
    Check(signals.Count == 2, "real SQLite query excludes short, expired, future and invalid-duration sessions");
    var signal = signals.Single(x => x.MusicId == 1);
    Check(signal.Count == 2 && Math.Abs(signal.Weight - 1.5) < .00001 && signal.LastPlayedTicks == now.Ticks,
        "real SQLite aggregation preserves counts and thirty-day decay");
    Check(Math.Abs(signals.Single(x => x.MusicId == 6).Weight - .5) < .00001, "exactly fifty percent is a qualified positive signal");
}
finally { await db.CloseAsync(); File.Delete(path); }
foreach (int size in new[] { 10000, 100000 })
{
    var large = new RecommendationTrack[size];
    for (int i = 0; i < size; i++) large[i] = Track(i + 1, "Artist " + i % 3000, "Genre " + i % 40, "Album " + i % 8000);
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    var result = service.Build(large, [], now, 0, default);
    Check(result.Songs.Count == 12, "bounded candidate selection for " + size + " songs");
    Console.WriteLine($"MEASURE: synthetic {size} tracks, {watch.Elapsed.TotalMilliseconds:F1} ms, {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1048576.0:F2} MiB allocated; input creation excluded.");
}
Console.WriteLine($"PASS: {checks} checks.");
