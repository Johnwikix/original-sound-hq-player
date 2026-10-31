using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

internal static class ReadonlyQueryChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task RunAsync(string root)
    {
        var database = new MusicDatabaseService(Path.Combine(root, "readonly-lifetimes.db"));
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<PlaybackHistory>();
        await database.Connection.InsertAsync(new Folder { Name = "fixture", Path = root, Type = Folder.TypeLocal });
        var app = new AppViewModel();
        var folders = new AddFolderViewModel(database, app, NullLogger<AddFolderViewModel>.Instance,
            new FolderAccessService(NullLogger<FolderAccessService>.Instance));
        var native = database.Connection.GetConnection();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int roots = 0, counts = 0;
        int blockRoot = 1;
        native.Trace = true;
        native.Tracer = sql =>
        {
            if (!sql.Contains("select", StringComparison.OrdinalIgnoreCase)) return;
            if (sql.Contains("from \"Folder\"", StringComparison.OrdinalIgnoreCase))
            {
                int count = Interlocked.Increment(ref roots);
                if (count == blockRoot)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("SQLite trace gate timed out");
                }
            }
            if (sql.Contains("AS SongCount FROM Music", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref counts);
        };
        try
        {
            folders.Activate();
            Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "initial real folder query did not start");
            var field = typeof(AddFolderViewModel).GetField("_folderLoadTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = (Task)field.GetValue(folders)!;
            var refresh = typeof(AddFolderViewModel).GetMethod("NotifyCommands", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int i = 0; i < 20; i++) refresh.Invoke(folders, null);
            Check(ReferenceEquals(original, field.GetValue(folders)), "repeated reloads replaced the in-flight folder load");
            release.Set();
            await original.WaitAsync(TimeSpan.FromSeconds(10));
            Check(roots == 2 && counts == 2, $"20 invalidations should merge into one subsequent read, got roots={roots}, counts={counts}");
            Check(folders.FolderList.Count == 1, "merged folder read did not publish the final result");

            folders.Deactivate();
            entered.Reset(); release.Reset(); blockRoot = roots + 1;
            folders.Activate();
            Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "second folder query did not enter SQLite");
            int beforeCounts = counts;
            folders.Deactivate();
            var stopping = folders.StopAsync();
            Check(!stopping.IsCompleted, "stop only cancelled a wait instead of waiting for the actual SQLite query");
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            Check(counts == beforeCounts, "cancelled root query still started its second count query");

            var stats = new PlaybackStatsService(app, database, NullLogger<PlaybackStatsService>.Instance);
            using var cancellation = new CancellationTokenSource();
            int historyQueries = 0;
            native.Tracer = sql =>
            {
                if (sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && sql.Contains("FROM PlaybackHistory", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref historyQueries);
                    cancellation.Cancel();
                }
            };
            try
            {
                await stats.GetStatsSnapshotAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, cancellationToken: cancellation.Token);
                throw new Exception("cancelled statistics request returned a snapshot");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Check(historyQueries == 1, "cancelled statistics range still started top-song/artist/album SQL queries");
            native.Tracer = _ => { };
            var snapshot = await stats.GetStatsSnapshotAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
            Check(snapshot.TracksPlayedCount == 0, "read cancellation damaged the reusable SQLite connection");
            Console.WriteLine("PASS: real SQLite: 20 source invalidations coalesce; stop waits blocked SQL; cancellation skips subsequent count/top queries; connection remains usable.");
        }
        finally
        {
            release.Set();
            native.Trace = false;
            await folders.StopAsync();
            await database.Connection.CloseAsync();
        }
    }
}

namespace WinUIMusicPlayer.Model.Stats
{
    // Data containers only; the linked SQL aggregation and token checks are production code.
    public class SongPlayStat
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public int MusicId { get; set; }
        public int PlayCount { get; set; }
        public double TotalDurationSeconds { get; set; }
    }
    public class ArtistPlayStat : SongPlayStat { }
    public class AlbumPlayStat : SongPlayStat { }
}
