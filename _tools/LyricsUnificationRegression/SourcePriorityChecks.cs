using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;

internal static class SourcePriorityChecks
{
    public static async Task RunAsync(LyricsParser parser, Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "lyrics-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var db = new SQLiteAsyncConnection(Path.Combine(folder, "test.db"));
        string previousOrder = AppSettings.LocalLyricsFormatOrder;
        bool previousPriority = AppSettings.PreferDatabaseLyrics;
        bool previousAuto = AppSettings.IsAutoLyricsEnabled;
        try
        {
            await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY)");
            await db.ExecuteAsync("INSERT INTO Music(Id) VALUES(1)");
            await db.CreateTableAsync<MusicLyrics>();
            var database = new MusicDatabaseService(db, parser);
            await database.Lyrics.InitializeAsync(db.DatabasePath);
            var online = new LyricsOnlineSearch { Handler = (_, _, _) => Task.FromResult<LyricsDocument?>(parser.Import("[00:01.000]online")) };
            var remote = new WebDavLibraryService { Result = parser.Import("[00:01.000]remote") };
            var resolver = new LyricsRefreshService(database, parser, online, remote, NullLogger<LyricsRefreshService>.Instance);
            var music = new Music { Id = 1, Path = Path.Combine(folder, "track.flac") };
            string sidecar = Path.ChangeExtension(music.Path, ".lrc");
            await File.WriteAllTextAsync(sidecar, "[00:01.000]file");
            AppSettings.LocalLyricsFormatOrder = LyricsFilePolicy.DefaultOrder;
            AppSettings.IsAutoLyricsEnabled = true;

            async Task Store(LyricsDocument document, string kind = "User")
            {
                var snapshot = await database.Lyrics.GetAsync(1);
                check(await database.Lyrics.SaveAsync(1, document, snapshot.Revision, kind), "priority fixture commits database revision");
            }
            async Task<string> Resolve(Music track)
            {
                var lines = await resolver.SetLyrics(track, default);
                return lines.Count == 0 ? "" : string.Concat(lines[0].Words.Select(word => word.Word));
            }

            await Store(parser.Import("[00:01.000]database"));
            AppSettings.PreferDatabaseLyrics = false;
            check(await Resolve(music) == "file", "files first selects sidecar over user database lyrics");
            AppSettings.PreferDatabaseLyrics = true;
            check(await Resolve(music) == "database", "database first selects user database lyrics over sidecar");
            await Store(new(new("invalid untimed content", LyricsFormat.Unknown), null), "Embedded");
            check(await Resolve(music) == "file", "invalid database lyrics fall back to files");
            await Store(LyricsDocument.Empty, "Online");
            check(await Resolve(music) == "file" && online.Calls == 0, "empty database falls back to file before searching online");
            await Store(parser.Import("[00:01.000]database"));
            AppSettings.PreferDatabaseLyrics = false;
            await File.WriteAllTextAsync(sidecar, "invalid file");
            check(await Resolve(music) == "database", "invalid file falls back to database");
            File.Delete(sidecar);
            check(await Resolve(music) == "database", "missing file falls back to database");

            music.SourceId = 1;
            AppSettings.PreferDatabaseLyrics = true;
            int published = 0;
            await resolver.SetLyrics(music, default, _ => published++);
            check(remote.Calls == 0 && published == 0, "database first skips remote IO and temporary cached publication");
            AppSettings.PreferDatabaseLyrics = false;
            var remoteDisplay = await resolver.SetLyrics(music, default, _ => published++);
            check(remote.Calls == 1 && published == 1 && remote.LastOrder == LyricsFilePolicy.DefaultOrder &&
                remoteDisplay[0].Words[0].Word == "remote", "files first publishes cached preview then asynchronously resolves remote sidecar with format order");
            check((await database.Lyrics.GetAsync(1)).Document.Original.Content.Contains("database"), "remote sidecar cannot overwrite user database edits");
            await Store(LyricsDocument.Empty, "Embedded");
            AppSettings.PreferDatabaseLyrics = true;
            check(await Resolve(music) == "remote" && remote.Calls == 2, "database first falls back to remote when empty");
            check((await database.Lyrics.GetAsync(1)).SourceKind == "RemoteSidecar", "remote fallback retains conditional caching");
            remote.Result = null;
            AppSettings.PreferDatabaseLyrics = false;
            check(await Resolve(music) == "remote", "missing remote sidecar falls back to database");
            await Store(LyricsDocument.Empty);
            foreach (bool databaseFirst in new[] { false, true })
            {
                AppSettings.PreferDatabaseLyrics = databaseFirst;
                check(await Resolve(music) == "" && online.Calls == 0, "user clear suppresses automatic search under both source orders");
            }
            await Store(LyricsDocument.Empty, "Embedded");
            check(await Resolve(music) == "online" && online.Calls == 1, "both unavailable sources fall back to online search");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.Handler = async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            };
            AppSettings.PreferDatabaseLyrics = false;
            using var cancelled = new CancellationTokenSource();
            Task pending = resolver.SetLyrics(music, cancelled.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            bool observedCancellation = false;
            try { await pending; }
            catch (OperationCanceledException) { observedCancellation = true; }
            check(observedCancellation && online.Calls == 1, "source resolution observes cancellation without starting online fallback");

            var external = new Music { Path = Path.Combine(folder, "external.flac"), EmbeddedLyrics = "[00:01.000]embedded" };
            await File.WriteAllTextAsync(Path.ChangeExtension(external.Path, ".lrc"), "[00:01.000]file");
            OneShotLyricsCache.Save(external.Path, parser.Import("[00:01.000]cached"));
            AppSettings.PreferDatabaseLyrics = false;
            check(await Resolve(external) == "file", "external files first preserves sidecar priority");
            AppSettings.PreferDatabaseLyrics = true;
            check(await Resolve(external) == "cached", "external database first uses saved cache before files and embedded lyrics");
            var empty = new Music { Path = Path.Combine(folder, "external-clear.flac") };
            check(OneShotLyricsCache.SaveEdited(empty.Path, LyricsDocument.Empty, 0), "external clear priority fixture commits");
            check(await Resolve(empty) == "" && online.Calls == 1, "database-first external user clear suppresses automatic search");
            await File.WriteAllTextAsync(Path.ChangeExtension(empty.Path, ".lrc"), "[00:01.000]file");
            check(await Resolve(empty) == "file", "empty external cache still falls back to a valid file");
        }
        finally
        {
            AppSettings.LocalLyricsFormatOrder = previousOrder;
            AppSettings.PreferDatabaseLyrics = previousPriority;
            AppSettings.IsAutoLyricsEnabled = previousAuto;
            await db.CloseAsync();
        }
    }
}
