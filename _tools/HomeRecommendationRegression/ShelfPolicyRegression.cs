using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

/// <summary>按栏目真实用途验证收藏回听、跨流派探索和候选配额。</summary>
internal static class ShelfPolicyRegression
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
        RecommendationTrack Track(int id, string genre, bool favorite = false, bool playlist = false) =>
            new(id, ["Artist " + id], "Album " + id, "Artist " + id, genre, 2020,
                favorite, true, 0, "Song " + id, playlist);
        HomeListening Listen(int id, double weight) =>
            new() { MusicId = id, Count = (int)weight, Weight = weight, LastPlayedTicks = now.AddDays(-2).Ticks };
        IEnumerable<RecommendedSong> NewSongs(HomeRecommendation result) =>
            result.Songs.Concat(result.Featured ?? []).Concat(result.Cards ?? []).Concat(result.Discovery);

        var collected = new[] { Track(1, "Jazz", favorite: true), Track(2, "Rock", playlist: true) };
        var replay = service.Build(collected, [Listen(1, 8), Listen(2, 4)], now, 0, default);
        Check(replay.Familiar.Count > 0 && replay.Familiar[0].MusicId == 1,
            "recent favorites remain eligible for the familiar shelf in an all-collected library");
        Check(NewSongs(replay).All(song => song.MusicId == 2),
            "small collected libraries can recommend a different collected recording without duplicating familiar history");
        var single = service.Build([collected[0]], [Listen(1, 8)], now, 0, default);
        Check(single.Familiar.Count == 1 && single.Songs.Count == 0,
            "one collected recording retains its familiar playback entry");

        var library = Enumerable.Range(1, 321).Select(id => Track(id, id <= 81 ? "Jazz" : "Metal", id == 1)).ToArray();
        for (int batch = 0; batch < 3; batch++)
        {
            var result = service.Build(library, [Listen(1, 8)], now, batch, default);
            Check(result.Familiar.Any(song => song.MusicId == 1), "favorite replay survives batch " + batch);
            Check(result.Songs.Count == 20 && result.Songs.All(song => song.MusicId <= 81),
                "primary recommendations retain preferred style in batch " + batch);
            Check(result.Discovery.Count == 20 && result.Discovery.All(song => song.MusicId > 81),
                "another-genre shelf survives low taste affinity in batch " + batch);
            var all = NewSongs(result).Concat(result.Familiar).ToArray();
            Check(all.Select(song => song.MusicId).Distinct().Count() == all.Length,
                "independent shelf policies retain page-wide deduplication in batch " + batch);
        }

        var allHeardTracks = library.Select(track => track with { PlayCount = 5 }).ToArray();
        var allHeardHistory = library.Select(track => Listen(track.Id, track.Id <= 81 ? 10 : 1)).ToArray();
        var allHeard = service.Build(allHeardTracks, allHeardHistory, now, 0, default);
        Check(allHeard.Discovery.Count == 20 && allHeard.Discovery.All(song => song.MusicId > 81),
            "an entirely heard library retains an alternative-genre shelf");
        var mixedHeard = library.Select(track => track.Id >= 102 ? track with { PlayCount = 5 } : track).ToArray();
        var mixed = service.Build(mixedHeard, [Listen(1, 8)], now, 0, default);
        Check(mixed.Discovery.Count == 20 && mixed.Discovery.All(song => song.MusicId is >= 82 and <= 101),
            "unheard alternatives rank ahead of equally unfamiliar heard songs without excluding replay");
        var heardInHistory = Enumerable.Range(1, 81).Select(id => Listen(id, 10))
            .Concat(Enumerable.Range(102, 220).Select(id => new HomeListening
            {
                MusicId = id, Weight = .5, Count = 1, LastPlayedTicks = now.AddDays(-2).Ticks
            })).ToArray();
        var historyMixed = service.Build(library, heardInHistory, now, 0, default);
        Check(historyMixed.Discovery.Count == 20 && historyMixed.Discovery.All(song => song.MusicId is >= 82 and <= 101),
            "qualified history also attenuates discovery when cumulative play count is stale");

        var narrow = Enumerable.Range(1, 41).Select(id => Track(id, id <= 21 ? "Jazz" : "Metal", id == 1)).ToArray();
        var reserved = service.Build(narrow, [], now, 0, default);
        Check(reserved.Songs.Count == 20 && reserved.Discovery.Count == 13
            && reserved.Featured?.Count == 5 && reserved.Cards?.Count == 3,
            "discovery receives its own quota while preserving primary and featured slots");
        Check(reserved.Songs.Concat(reserved.Featured ?? []).Concat(reserved.Cards ?? []).Count(song => song.MusicId is > 1 and <= 21) == 20
            && reserved.Discovery.All(song => song.MusicId > 21),
            "primary selection cannot consume the reserved alternative-genre pool");

        var heard = new[] { Listen(1, 8) }.Concat(Enumerable.Range(82, 240).Select(id => Listen(id, 1))).ToArray();
        Check(service.Build(library, heard, now, 0, default).Discovery.All(song => song.MusicId <= 81),
            "alternative genre selection still follows the dominant listening preference");
        var unplayable = library.Select(track => track.Id > 81 ? track with { IsAvailable = false } : track).ToArray();
        Check(service.Build(unplayable, [], now, 0, default).Discovery.Count == 0,
            "offline alternative genres do not produce unplayable discoveries");

        var familiarTracks = Enumerable.Range(1, 80).Select(id => Track(id, "Jazz", id == 1, id == 2)).ToArray();
        var acoustic = familiarTracks.ToDictionary(track => track.Id, track => track.Id <= 2 ? 0f : 1f);
        var familiar = service.Build(familiarTracks, [Listen(1, 15), Listen(2, 10), Listen(3, 1)], now, 0, default, acoustic);
        Check(familiar.Familiar.Take(2).Select(song => song.MusicId).SequenceEqual([1, 2]),
            "familiar ranking follows effective listening rather than the acoustic relevance gate");
        return checks;
    }
}
