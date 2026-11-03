using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

internal static class OwnedTrackRegression
{
    public static int Run(HomeRecommendationService service, DateTime now)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        RecommendationTrack Track(int id, string artist, string title, bool favorite = false, bool playlist = false) =>
            new(id, [artist], "Album " + id, artist, "Jazz", 2020, favorite, true, 0, title, playlist);
        RecommendedSong[] All(HomeRecommendation result) => result.Songs.Concat(result.Discovery)
            .Concat(result.Featured ?? []).Concat(result.Cards ?? []).ToArray();

        var small = new[]
        {
            Track(1, "Artist A", "Seed", favorite: true),
            Track(2, "Artist B", "Playlist seed", playlist: true),
            Track(3, "Artist C", "Both", favorite: true, playlist: true),
            Track(4, "Artist A", "Seed"),
            Track(5, "Artist A", "Different song"),
            Track(6, "Different artist", "Seed"),
            Track(7, "Artist A", "Another song"),
            Track(8, "Alternate credit", "  sEeD  ")
        };
        var result = service.Build(small, [], now, 0, default);
        Check(All(result).All(song => song.MusicId is not (1 or 2 or 3 or 4 or 6 or 8)),
            "favorites, playlist members and their duplicate recordings are excluded from new recommendation shelves");
        Check(result.Songs.Count == 2 && result.Songs.Any(song => song.MusicId == 5) && result.Songs.Any(song => song.MusicId == 7),
            "title exclusion keeps different songs by the same artist");
        Check(All(result).All(song => song.MusicId is not (6 or 8)),
            "collected namesakes remain excluded across alternate credits, case and whitespace");
        Check(!result.IsColdStart, "excluded explicit likes still train the recommendation profile");
        var collectionOnly = service.Build(small[..4], [new() { MusicId = 1, Weight = 5, Count = 5 }], now, 0, default);
        Check(All(collectionOnly).Length == 0 && collectionOnly.Familiar.Count == 1,
            "all-collected library retains familiar history without backfilling new recommendation shelves");

        var large = Enumerable.Range(1, 430).Select(id => Track(id, "Artist " + id, "Song " + id,
            favorite: id <= 200, playlist: id is > 200 and <= 400)).ToArray();
        var audio = large.ToDictionary(track => track.Id, track => track.Id <= 400 ? 1f : .9f);
        var largeResult = service.Build(large, [], now, 0, default, audio);
        Check(largeResult.Songs.Count == 20 && (largeResult.Featured?.Count ?? 0) == 5 && (largeResult.Cards?.Count ?? 0) == 3,
            "collected high-scoring songs cannot exhaust bounded candidate pools or slot reservations");
        Check(All(largeResult).All(song => song.MusicId > 400), "strong audio scores cannot reintroduce collected songs");
        var history = large.Select(track => new HomeListening { MusicId = track.Id, Count = 2, Weight = 2, LastPlayedTicks = now.AddDays(-2).Ticks }).ToArray();
        var familiar = service.Build(large, history, now, 0, default, audio);
        Check(familiar.Familiar.Any(song => song.MusicId <= 400) && All(familiar).All(song => song.MusicId > 400),
            "familiar includes collected history while new recommendations retain collection exclusion");
        var unknown = service.Build([Track(1, "", "", favorite: true), Track(2, "", "")], [], now, 0, default);
        Check(unknown.Songs.Count == 1 && unknown.Songs[0].MusicId == 2, "missing recording identity does not exclude unrelated uncollected music");
        var removed = service.Build(small.Select(track => track with { IsFavorite = false, IsInPlaylist = false }).ToArray(), [], now, 0, default);
        Check(All(removed).Any(song => song.MusicId is 1 or 4), "removing collection membership makes the recording eligible on the next build");
        return checks;
    }
}
