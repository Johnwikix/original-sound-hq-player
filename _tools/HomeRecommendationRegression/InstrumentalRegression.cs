using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

internal static class InstrumentalRegression
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
        RecommendationTrack Track(int id, string title, bool favorite = false) =>
            new(id, ["Artist " + id % 60], "Album " + id % 60, "Artist " + id % 60,
                "Pop", 2020, favorite, true, 0, title);
        RecommendationFeatures Feature(RecommendationTrack track, bool collected = false) =>
            new(track, track.Artists, ["Pop"], track.Album, "", collected);
        RecommendedSong[] Primary(HomeRecommendation result) =>
            (result.Featured ?? []).Concat(result.Cards ?? []).Concat(result.Songs).ToArray();

        foreach (string title in new[] { "Song (Instrumental)", "Song - INSTRUMENTAL", "Song [instrumental version]", "Instrumental" })
        {
            var track = Track(1, title, true);
            Check(Feature(track).RecommendationWeight == .25,
                "instrumental title matching ignores case: " + title);
            Check(Math.Abs(Feature(track, true).RecommendationWeight - .025) < 1e-12,
                "instrumental and collection attenuation compose: " + title);
            var listening = new HomeListening { MusicId = 1, Count = 5, Weight = 10 };
            Check(RecommendationEvidence.Strength(track, listening)
                == RecommendationEvidence.Strength(track with { Title = "Song" }, listening),
                "instrumental titles preserve explicit and listening training evidence: " + title);
        }
        Check(Feature(Track(1, "")).RecommendationWeight == 1
            && Feature(Track(1, "Song (Live)")).RecommendationWeight == 1,
            "empty and other version titles retain their existing weight");

        var tracks = Enumerable.Range(1, 360)
            .Select(id => Track(id, "Song " + id + (id <= 300 ? " (Instrumental)" : ""))).ToList();
        tracks.Add(Track(361, "Favorite seed", true) with { Artists = ["Seed artist"], Album = "Seed album", AlbumArtist = "Seed artist" });
        var audio = tracks.ToDictionary(track => track.Id, track => track.Id <= 300 ? 1f : .9f);
        var community = new CommunitySignals(tracks.SelectMany(track => track.Artists).Distinct()
            .ToDictionary(artist => artist, _ => 1f, StringComparer.OrdinalIgnoreCase), new Dictionary<string, float>());
        bool Vocal(RecommendedSong song) => Feature(tracks[song.MusicId - 1]).InstrumentalWeight == 1;
        foreach (var source in new[] { RecommendationSources.Metadata, RecommendationSources.Audio,
            RecommendationSources.Community, RecommendationSources.All })
        {
            var result = service.Build(tracks, [], now, 0, default, audio, community, source);
            Check(Primary(result).Length == 28 && Primary(result).All(Vocal),
                "vocal alternatives survive a bounded pool dominated by same-album instrumentals: " + source);
        }

        var coldTracks = tracks.Take(360).Select(track => track with { Genre = "" }).ToArray();
        var cold = service.Build(coldTracks, [], now, 0, default);
        Check(Primary(cold).Length == 28 && Primary(cold).Count(Vocal) >= 24,
            "cold-start fallback also favors available vocal alternatives");
        var differentGenres = tracks.Select(track => track with { Genre = track.Id <= 360 ? "Metal" : "Pop" }).ToArray();
        var discovery = service.Build(differentGenres, [], now, 0, default);
        Check(discovery.Discovery.Count == 20 && discovery.Discovery.All(Vocal),
            "equally unfamiliar vocal alternatives precede instrumentals in discovery");
        var history = tracks.Take(360).Select(track => new HomeListening
        {
            MusicId = track.Id, Count = 5, Weight = 10, LastPlayedTicks = now.Ticks
        }).ToArray();
        var familiar = service.Build(tracks, history, now, 0, default);
        Check(familiar.Familiar.Count == 20 && familiar.Familiar.All(Vocal),
            "familiar listening applies instrumental attenuation before its bounded candidate pool");

        var onlyInstrumentals = tracks.Take(40).ToArray();
        var onlyResult = service.Build(onlyInstrumentals, [], now, 0, default);
        Check(Primary(onlyResult).Length == 28,
            "an instrumental-only library retains independent primary recommendations");
        HomeRecommendation shownLone = new([new(onlyInstrumentals[0].Id, RecommendationReason.Explore)], [], [], true);
        var lone = service.Build([onlyInstrumentals[0]], [], now, 1, default, recentRecommendations: [shownLone]);
        Check(lone.Songs.Count == 1 && lone.Songs[0].MusicId == onlyInstrumentals[0].Id,
            "instrumental and recent-exposure penalties never remove the only available song");
        return checks;
    }
}
