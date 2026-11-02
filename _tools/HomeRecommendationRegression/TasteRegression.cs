using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

internal static class TasteRegression
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
        RecommendationTrack Track(int id, string artist, string genre, bool favorite = false) =>
            new(id, [artist], "Album " + id, artist, genre, 2020, favorite, true, 0, "Song " + id);
        RecommendedSong[] Primary(HomeRecommendation result) =>
            (result.Featured ?? []).Concat(result.Cards ?? []).Concat(result.Songs).ToArray();

        var focused = new List<RecommendationTrack> { Track(1, "Favorite", "Jazz", true) };
        for (int id = 2; id <= 81; id++) focused.Add(Track(id, "Jazz " + id, "Jazz"));
        for (int id = 82; id <= 321; id++) focused.Add(Track(id, "Metal " + id, "Metal"));
        for (int batch = 0; batch < 3; batch++)
        {
            var result = service.Build(focused, [], now, batch, default);
            Check(result.Songs.Count(song => song.MusicId <= 81) >= 16,
                "preferred genre survives diversity and exploration across batch " + batch);
            Check(Primary(result).Take(8).All(song => song.MusicId <= 81),
                "featured slots retain clear positive taste across batch " + batch);
            Check(result.Discovery.Count > 0 && result.Discovery.Count(song => song.MusicId <= 81) >= result.Discovery.Count * .8,
                "discovery finds unheard music around the preferred style across batch " + batch);
        }

        var acousticLibrary = new List<RecommendationTrack>();
        var audio = new Dictionary<int, float>();
        for (int id = 1; id <= 100; id++)
        {
            bool liked = id <= 25;
            acousticLibrary.Add(Track(id, "Metadata " + id % 25, "Pop", liked));
            audio[id] = .05f;
        }
        for (int id = 101; id <= 160; id++)
        {
            acousticLibrary.Add(Track(id, "Acoustic " + id, ""));
            audio[id] = .95f;
        }
        var acoustic = service.Build(acousticLibrary, [], now, 0, default, audio);
        Check(acoustic.Songs.Count(song => song.MusicId >= 101) >= 16,
            "strong acoustic matches outrank broad metadata matches from different musical styles");
        Check(Primary(acoustic).Any(song => song.MusicId >= 101 && song.Reason == RecommendationReason.Audio),
            "acoustic explanation belongs to an actually promoted matching song");

        var remoteLibrary = new List<RecommendationTrack> { Track(1, "Favorite", "Jazz", true) };
        for (int id = 2; id <= 120; id++) remoteLibrary.Add(Track(id, "Local " + id, "Jazz"));
        for (int id = 121; id <= 140; id++) remoteLibrary.Add(Track(id, "Community " + id, ""));
        var community = new CommunitySignals(
            Enumerable.Range(121, 20).ToDictionary(id => "Community " + id, _ => .95f, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, float>());
        var remote = service.Build(remoteLibrary, [], now, 0, default, null, community);
        Check(Primary(remote).Count(song => song.MusicId >= 121) >= 8,
            "strong community similarities materially influence selected songs");

        var concentrated = new List<RecommendationTrack> { Track(1, "Preferred", "Jazz", true) };
        for (int id = 2; id <= 40; id++) concentrated.Add(Track(id, "Preferred", "Jazz"));
        for (int id = 41; id <= 200; id++) concentrated.Add(Track(id, "Unrelated " + id, "Metal"));
        var concentratedResult = service.Build(concentrated, [], now, 0, default);
        Check(concentratedResult.Songs.Count(song => song.MusicId <= 40) >= 16,
            "artist quota relaxes before filling the page with unrelated genres");

        var invalid = new Dictionary<int, float> { [2] = float.NaN, [3] = float.PositiveInfinity, [4] = -1 };
        var clean = service.Build(focused, [], now, 0, default);
        var corrupt = service.Build(focused, [], now, 0, default, invalid);
        Check(Primary(clean).Select(song => song.MusicId).SequenceEqual(Primary(corrupt).Select(song => song.MusicId)),
            "invalid external scores cannot poison candidate ordering");
        Check(service.Build(focused, [], now, 0, default, new Dictionary<int, float>()).Songs.SequenceEqual(clean.Songs),
            "missing analysis retains the metadata fallback");
        var staleSignals = service.Build(focused, [], now, 0, default, new Dictionary<int, float> { [9999] = 1 },
            new CommunitySignals(new Dictionary<string, float> { ["Removed artist"] = 1 }, new Dictionary<string, float>()));
        Check(Primary(staleSignals).SequenceEqual(Primary(clean)),
            "signals outside the playable library do not alter metadata fallback weights");
        var playlistOnly = focused.Select(track => track with { IsFavorite = false, IsInPlaylist = track.Id == 1 }).ToArray();
        var playlistResult = service.Build(playlistOnly, [], now, 0, default);
        Check(!playlistResult.IsColdStart && playlistResult.Songs.Count(song => song.MusicId <= 81) >= 16,
            "playlist membership supplies an explicit positive preference without favorites or history");
        Check(RecommendationEvidence.Strength(playlistOnly[0], null) == RecommendationEvidence.Strength(playlistOnly[0] with { IsFavorite = true }, null),
            "overlapping favorite and playlist membership does not duplicate the same positive vote");
        var oldAndNew = Enumerable.Range(1, 350).Select(id => Track(id, "Artist " + id, "Jazz", id <= 300)
            with { PlayCount = id <= 300 ? 10 : 0 }).ToArray();
        var oldAndNewScores = oldAndNew.ToDictionary(track => track.Id, track => track.Id <= 300 ? .98f : .8f);
        var discoveryResult = service.Build(oldAndNew, [], now, 0, default, oldAndNewScores);
        Check(discoveryResult.Discovery.Count == 20 && discoveryResult.Discovery.All(song => song.MusicId > 300),
            "listened favorites cannot exhaust the bounded pool of unheard style matches");
        return checks;
    }
}
