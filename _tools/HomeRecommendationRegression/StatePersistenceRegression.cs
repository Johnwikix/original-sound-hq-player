using System.Text.Json;
using SQLite;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

internal static class StatePersistenceRegression
{
    public static async Task<int> RunAsync(HomeRecommendationService service, DateTime now)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        int[] All(HomeRecommendation page) => page.Songs.Concat(page.Familiar).Concat(page.Discovery)
            .Concat(page.Featured ?? []).Concat(page.Cards ?? []).Concat(page.Soundalikes ?? []).Concat(page.Rediscover ?? []).Select(song => song.MusicId).ToArray();
        var tracks = Enumerable.Range(1, 360).Select(id => new RecommendationTrack(id, ["Artist " + id],
            "Album " + id, "Artist " + id, "", 2020, false, true, 0, "Song " + id)).ToArray();
        var audio = tracks.ToDictionary(track => track.Id, track => 1f - track.Id * .0025f);
        var first = service.Build(tracks, [], now, 17, default, audio);
        string path = Path.Combine(Path.GetTempPath(), "home-state-" + Guid.NewGuid().ToString("N") + ".db");
        var database = new SQLiteAsyncConnection(path);
        try
        {
            var store = new RecommendationStateRepository(database);
            var empty = await store.LoadAsync(now, default);
            Check(empty.Recent.Length == 0, "a missing recommendation state starts without invented exposure");
            await store.SaveAsync(HomeRecommendationState.Capture(17, [first], now), now);
            await database.CloseAsync();
            database = new SQLiteAsyncConnection(path);
            store = new(database);
            var loaded = await store.LoadAsync(now.AddHours(2), default);
            Check(loaded.NextBatch == 18 && loaded.Recent.Length == 1,
                "a new database connection restores the next batch and published exposure");
            Check(loaded.Recent[0].ExposedAtUtcTicks == now.Ticks
                && loaded.Recent[0].MusicIds.Order().SequenceEqual(All(first).Order()),
                "restart restores actual song IDs and original exposure time without renewing it");
            var restored = loaded.Restore(now.AddHours(2));
            var restarted = service.Build(tracks, [], now.AddHours(2), loaded.NextBatch, default, audio, recentRecommendations: restored);
            Check(All(restarted).Length == 40 && All(restarted).Intersect(All(first)).Count() <= 10,
                "same-day restart rotates previously published recommendations");

            var beforeExpiry = now.AddDays(7).AddTicks(-1);
            Check((await store.LoadAsync(beforeExpiry, default)).Recent.Length == 1,
                "exposure remains active immediately before its seven-day deadline");
            var expiry = now.AddDays(7);
            var expired = await store.LoadAsync(expiry, default);
            Check(expired.Recent.Length == 0 && expired.NextBatch == 18,
                "expiry clears exposure without resetting the persisted batch counter");
            var noHistory = service.Build(tracks, [], expiry, 18, default, audio);
            var expiredInput = service.Build(tracks, [], expiry, 18, default, audio, recentRecommendations: [first]);
            Check(All(noHistory).SequenceEqual(All(expiredInput)),
                "expired exposure has no ranking effect even before the next persistence write");

            HomeRecommendation old = first with { ExposedAtUtcTicks = now.AddDays(-8).Ticks };
            var withExpiredSlot = service.Build(tracks, [], now, 18, default, audio, recentRecommendations: [old, first]);
            var withoutExpiredSlot = service.Build(tracks, [], now, 18, default, audio, recentRecommendations: [first]);
            Check(All(withExpiredSlot).SequenceEqual(All(withoutExpiredSlot)),
                "expired batches do not consume a recent-batch attenuation slot");

            HomeRecommendation allShelves = new([new(1, RecommendationReason.Artist)], [new(2, RecommendationReason.Familiar)],
                [new(3, RecommendationReason.Explore)], false, [new(4, RecommendationReason.Audio)], [new(5, RecommendationReason.Community)],
                [new(6, RecommendationReason.Audio)], [new(7, RecommendationReason.Rediscover)])
                { ExposedAtUtcTicks = now.Ticks };
            var captured = HomeRecommendationState.Capture(int.MaxValue, [allShelves, first, old, first], now);
            Check(captured.NextBatch == int.MinValue && captured.Recent.Length == 3
                && captured.Recent[0].MusicIds.SequenceEqual([1, 2, 3, 4, 5, 6, 7]),
                "all shelves persist within three live batches and counter wraparound remains valid");
            var many = Enumerable.Range(1, 12).Select(id => new HomeRecommendation(
                [new(id, RecommendationReason.Audio)], [], [], false) { ExposedAtUtcTicks = now.AddMinutes(-id).Ticks }).ToArray();
            var bounded = HomeRecommendationState.Capture(12, many, now).Restore(now);
            Check(bounded.Select(page => page.Songs.Single().MusicId).SequenceEqual(Enumerable.Range(1, 8)),
                "only the eight newest batches survive capture and restore, newest first");
            var oversized = new HomeRecommendationState
            {
                NextBatch = 23,
                Recent = [new() { ExposedAtUtcTicks = now.Ticks, MusicIds = Enumerable.Range(1, 1000).Prepend(-1).Prepend(1).ToArray() }]
            }.Normalize(now);
            Check(oversized.Recent[0].MusicIds.Length == HomeRecommendationState.SongsPerBatchLimit
                && oversized.Recent[0].MusicIds.Distinct().Count() == HomeRecommendationState.SongsPerBatchLimit && oversized.Recent[0].MusicIds.All(id => id > 0),
                "loaded exposure is bounded and rejects invalid or duplicate IDs");

            await database.ExecuteAsync("UPDATE HomeRecommendationState SET StateJson=? WHERE Id=1", "{broken");
            try { await store.LoadAsync(now, default); throw new Exception("Corrupt state accepted"); }
            catch (JsonException) { Check(true, "corrupt state is reported rather than silently replacing persisted data"); }
            Check(await database.ExecuteScalarAsync<string>("SELECT StateJson FROM HomeRecommendationState WHERE Id=1") == "{broken",
                "failed loading leaves the existing database payload intact");
            string future = "{\"SchemaVersion\":99,\"NextBatch\":7,\"Recent\":[]}";
            await database.ExecuteAsync("UPDATE HomeRecommendationState SET StateJson=? WHERE Id=1", future);
            try { await store.LoadAsync(now, default); throw new Exception("Future schema accepted"); }
            catch (NotSupportedException) { Check(true, "future state schemas cannot be overwritten as current defaults"); }
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await store.LoadAsync(now, canceled.Token); throw new Exception("Canceled state read accepted"); }
            catch (OperationCanceledException) { Check(true, "canceled initialization does not consume or clear persisted exposure"); }
        }
        finally
        {
            await database.CloseAsync();
            File.Delete(path);
        }
        return checks;
    }
}
