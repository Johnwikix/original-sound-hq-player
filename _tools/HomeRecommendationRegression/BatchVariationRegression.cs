using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

internal static class BatchVariationRegression
{
    public static int Run(HomeRecommendationService service, DateTime now)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        var tracks = Enumerable.Range(1, 360).Select(id => new RecommendationTrack(id, ["Artist " + id],
            "Album " + id, "Artist " + id, "", 2020, false, true, 0, "Song " + id)).ToArray();
        var audio = tracks.ToDictionary(track => track.Id, track => 1f - track.Id * .0025f);
        int[] Primary(HomeRecommendation result) => result.Songs.Concat(result.Featured ?? [])
            .Concat(result.Cards ?? []).Select(song => song.MusicId).ToArray();
        var first = service.Build(tracks, [], now, 0, default, audio);
        var next = service.Build(tracks, [], now, 1, default, audio);
        int overlap = Primary(first).Intersect(Primary(next)).Count();
        Console.WriteLine($"MEASURE: distinct acoustic scores, consecutive primary overlap {overlap}/28.");
        Check(overlap < 28, "refresh changes primary membership with distinct source scores");

        var recent = new List<HomeRecommendation> { first };
        for (int batch = 1; batch <= 8; batch++)
        {
            var input = recent.ToArray();
            var result = service.Build(tracks, [], now, batch, default, audio, recentRecommendations: input);
            var repeated = service.Build(tracks, [], now, batch, default, audio, recentRecommendations: input);
            int[] primary = Primary(result);
            overlap = primary.Intersect(Primary(input[0])).Count();
            Console.WriteLine($"MEASURE: refresh batch {batch}, previous primary overlap {overlap}/28.");
            Check(primary.Length == 28 && primary.Distinct().Count() == 28,
                "refresh retains independent filled primary slots in batch " + batch);
            Check(overlap <= 7, "refresh replaces at least three quarters of graded primary candidates in batch " + batch);
            Check(primary.All(id => id <= 180), "refresh keeps above-background acoustic evidence in batch " + batch);
            Check(primary.SequenceEqual(Primary(repeated)), "same batch and exposure snapshot remain reproducible in batch " + batch);
            recent.Insert(0, result);
            if (recent.Count > HomeRecommendationService.RecentBatchLimit) recent.RemoveAt(recent.Count - 1);
        }

        var allHeard = tracks.Select(track => track with { PlayCount = 5, Genre = track.Id <= 60 ? "Jazz" : "Metal" }).ToArray();
        HomeListening[] history = [new() { MusicId = 1, Weight = 5, Count = 5, LastPlayedTicks = now.Ticks }];
        var heardFirst = service.Build(allHeard, history, now, 0, default, audio);
        var heardNext = service.Build(allHeard, history, now, 1, default, audio, recentRecommendations: [heardFirst]);
        Check(heardNext.Familiar.SequenceEqual(heardFirst.Familiar), "a lone qualified listen remains eligible after recent exposure");

        var frequentTracks = tracks.Select(track => track with { IsFavorite = track.Id <= 20 }).ToArray();
        var frequentHistory = tracks.Select(track => new HomeListening
        {
            MusicId = track.Id, Count = 5, Weight = track.Id <= 20 ? 100 : 2, LastPlayedTicks = now.Ticks
        }).ToArray();
        var frequentFirst = service.Build(frequentTracks, frequentHistory, now, 0, default);
        Check(frequentFirst.Familiar.Count == 20 && frequentFirst.Familiar.All(song => song.MusicId <= 20),
            "unexposed familiar favorites retain their real listening advantage without a collection penalty");
        var frequentRecent = new List<HomeRecommendation> { frequentFirst };
        for (int batch = 1; batch <= 4; batch++)
        {
            var input = frequentRecent.ToArray();
            var result = service.Build(frequentTracks, frequentHistory, now, batch, default, recentRecommendations: input);
            int familiarOverlap = result.Familiar.Select(song => song.MusicId)
                .Intersect(input[0].Familiar.Select(song => song.MusicId)).Count();
            Check(result.Familiar.Count == 20 && familiarOverlap == 0,
                "recent familiar favorites give way to other qualified listens in batch " + batch);
            Check(result.Familiar.All(song => song.Reason == RecommendationReason.Familiar
                && frequentHistory.Any(listened => listened.MusicId == song.MusicId && listened.Count > 0)),
                "familiar rotation uses qualified listening candidates in batch " + batch);
            var repeated = service.Build(frequentTracks, frequentHistory, now, batch, default, recentRecommendations: input);
            Check(result.Familiar.SequenceEqual(repeated.Familiar),
                "familiar rotation remains reproducible with the same exposure snapshot in batch " + batch);
            frequentRecent.Insert(0, result);
            if (frequentRecent.Count > HomeRecommendationService.RecentBatchLimit)
                frequentRecent.RemoveAt(frequentRecent.Count - 1);
        }
        Check(heardNext.Discovery.Count == 20 && heardNext.Discovery.Select(song => song.MusicId)
            .Intersect(heardFirst.Discovery.Select(song => song.MusicId)).Count() <= 5,
            "heard alternative genres rotate with their own exposure penalty");
        var all = heardNext.Songs.Concat(heardNext.Familiar).Concat(heardNext.Discovery)
            .Concat(heardNext.Featured ?? []).Concat(heardNext.Cards ?? []).ToArray();
        Check(all.Select(song => song.MusicId).Distinct().Count() == all.Length, "rotation preserves page-wide recording allocation");

        var small = tracks.Take(12).ToArray();
        var smallFirst = service.Build(small, [], now, 0, default, audio);
        var smallNext = service.Build(small, [], now, 1, default, audio, recentRecommendations: [smallFirst]);
        Check(Primary(smallNext).Length == 12, "recent exposure remains a soft penalty when the library cannot provide new songs");

        var versions = tracks.Select(track => track with { Title = track.Id is 1 or 2 ? "Same recording" : track.Title,
            Artists = track.Id is 1 or 2 ? ["Same artist"] : track.Artists }).ToArray();
        HomeRecommendation priorVersion = new([], [], [], false, [new(1, RecommendationReason.Audio)]) { ExposedAtUtcTicks = now.Ticks };
        var alternate = service.Build(versions, [], now, 1, default, audio, recentRecommendations: [priorVersion]);
        Check(!Primary(alternate).Any(id => id is 1 or 2), "recent exposure also attenuates other files of the same recording");
        var familiarVersions = service.Build(versions, frequentHistory, now, 1, default, recentRecommendations: [priorVersion]);
        Check(familiarVersions.Familiar.Count == 20 && familiarVersions.Familiar.All(song => song.MusicId is not (1 or 2)),
            "familiar exposure also attenuates an alternate file of a previously displayed recording");
        var sourceOnly = tracks.Select(track => track with { Genre = track.Id <= 60 ? "Jazz" : "Metal" }).ToArray();
        var scarceAudio = new Dictionary<int, float> { [1] = 1 };
        var scarce = service.Build(sourceOnly, [], now, 1, default, scarceAudio, recentRecommendations: [priorVersion]);
        Check(scarce.Featured?.FirstOrDefault()?.MusicId == 1,
            "recent positive evidence stays ahead of unsupported fallback candidates");
        return checks;
    }
}
