using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Xaml.Interactivity;
using SQLite;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;

namespace WinUIMusicPlayer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}

public sealed partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static Window MainWindow { get; private set; } = null!;
    public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
    private readonly List<string> _checks = [];
    private readonly string _result = Path.Combine(AppContext.BaseDirectory, "result.txt");
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => File.AppendAllText(_result, "UNHANDLED: " + args.Exception);
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        SQLiteAsyncConnection? db = null;
        StatsViewModel? stats = null;
        try
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(fixture);
            string cover = Path.Combine(fixture, "cover.png");
            using (var stream = File.Create(cover))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 2, 2, 96, 96,
                    [0, 128, 255, 255, 0, 128, 255, 255, 0, 128, 255, 255, 0, 128, 255, 255]);
                await encoder.FlushAsync();
            }
            AppSettings.MusicCoverCache = Path.Combine(fixture, "covers");
            Directory.CreateDirectory(AppSettings.MusicCoverCache);
            db = new SQLiteAsyncConnection(Path.Combine(fixture, Guid.NewGuid() + ".db"));
            await db.CreateTableAsync<Music>();
            await db.CreateTableAsync<PlaybackHistory>();
            var surviving = new Music { Id = 1, SongId = 1, Path = cover, ImageHash = "first" };
            await db.InsertAsync(surviving);
            await db.InsertAsync(new PlaybackHistory
            {
                MusicId = 999, SongIdAtPlay = 1, Title = "Song", Author = "Artist", Album = "Album",
                StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow, TotalDurationMs = 180000, DurationPlayedMs = 150000
            });
            var app = new AppViewModel();
            var service = new PlaybackStatsService(app, new MusicDatabaseService(db), NullLogger<PlaybackStatsService>.Instance);
            stats = new StatsViewModel(app, service, NullLogger<StatsViewModel>.Instance,
                new ShutdownCoordinator(new AppLifecycle(), NullLogger<ShutdownCoordinator>.Instance));
            var remote = new WebDavLibraryService();
            var matcher = new LrcService();
            Services = new ServiceCollection().AddSingleton(stats).AddSingleton(remote).AddSingleton(matcher).BuildServiceProvider();
            var frame = new Frame();
            MainWindow = new Window { Content = frame };
            MainWindow.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
            MainWindow.AppWindow.Show(false);
            frame.Navigate(typeof(StatsPage));
            await Until(() => !stats.IsLoading && stats.TopAlbums.Count == 1, "real statistics query");
            var page = (StatsPage)frame.Content;
            await Until(() => Images(page).Count == 3, "real compiled templates");
            var album = stats.TopAlbums[0];
            var artist = stats.TopArtists[0];
            var song = stats.TopSongs[0];
            Check(album.MusicId == 999 && artist.MusicId == 999 && stats.TracksPlayedCount == 1,
                "real SQLite query keeps orphaned historical IDs and play counts");
            Check(Images(page).All(image => image.Source is null), "late library initially shows placeholders");
            int uiThread = Environment.CurrentManagedThreadId;
            int notifications = 0;
            album.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(AlbumPlayStat.Music)) return;
                Check(Environment.CurrentManagedThreadId == uiThread, "cover notification uses UI thread");
                notifications++;
            };
            await Task.Run(async () => await Task.Delay(20));
            app.Publish(surviving);
            Check(ReferenceEquals(album.Music, surviving) && ReferenceEquals(artist.Music, surviving),
                "deleted history anchors fall back to same album and artist after late library publication");
            await Until(() => Images(page).Count(image => image.Source is not null) == 2, "bound album and artist artwork");
            Check(song.Music is null && notifications == 1, "missing individual song remains a placeholder");
            Check(Images(page).Where(image => image.Source is not null).Select(image => image.Source).Distinct().Count() == 2,
                "shared cover decode creates independently owned WinRT image sources");
            int reads = ToolUtils.Reads;
            app.Publish(surviving);
            await Task.Delay(80);
            Check(notifications == 1 && ToolUtils.Reads == reads, "unchanged publication preserves sources without reloading");

            // Stop an in-flight old cover, publish a newer representative and release the old reader.
            ToolUtils.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var obsolete = new Music { Id = 2, Path = cover, ImageHash = "obsolete" };
            app.Publish(obsolete);
            await Until(() => ToolUtils.Reads > reads, "in-flight old representative");
            var previousSources = Images(page).Select(image => image.Source).ToArray();
            var current = new Music { Id = 3, Path = cover, ImageHash = "current" };
            app.Publish(current);
            ToolUtils.ReadGate.SetResult();
            ToolUtils.ReadGate = null;
            await Until(() => Images(page).Count(image => image.Source is not null && !previousSources.Contains(image.Source)) == 2,
                "new representative loads after cancellation");
            Check(ReferenceEquals(album.Music, current) && ReferenceEquals(artist.Music, current), "late old artwork cannot replace the latest representative");
            app.Publish();
            await Until(() => Images(page).All(image => image.Source is null), "removed group clears old artwork");
            app.Publish(new Music { Id = 4, Author = "Other artist", Album = "Other album", Path = cover });
            Check(album.Music is null && artist.Music is null, "unrelated songs do not fill absent groups");
            var anchor = new Music { Id = 999, SongId = 1, Path = cover, ImageHash = "anchor" };
            app.Publish(surviving, anchor);
            await Until(() => Images(page).Count(image => image.Source is not null) == 3, "restored anchors load all three covers");
            Check(ReferenceEquals(album.Music, anchor) && ReferenceEquals(artist.Music, anchor) && ReferenceEquals(song.Music, anchor),
                "valid history anchor takes precedence and individual song binding updates");
            stats.SelectedTimeRangeIndex = (int)StatsRange.AllTime;
            await Until(() => !stats.IsLoading, "range refresh");
            Check(ReferenceEquals(album, stats.TopAlbums[0]) && ReferenceEquals(artist, stats.TopArtists[0]),
                "range refresh preserves unchanged rows");
            frame.Navigate(typeof(Page));
            app.Publish();
            Check(ReferenceEquals(album.Music, anchor), "inactive page unsubscribes library updates");
            frame.Navigate(typeof(StatsPage));
            await Until(() => !stats.IsLoading, "reentry refresh");
            Check(album.Music is null && artist.Music is null && song.Music is null, "reentry resolves retained rows against current library");
            await CheckRemoteCoversAsync(app, (StatsPage)frame.Content, remote, matcher, await File.ReadAllBytesAsync(cover));
            await stats.StopAsync();
            await stats.StopAsync();
            await CheckCoverPriorityAsync(cover);
            File.WriteAllLines(_result, _checks);
        }
        catch (Exception ex)
        {
            File.WriteAllLines(_result, _checks.Append("FAIL: " + ex));
            Environment.ExitCode = 1;
        }
        finally
        {
            if (stats is not null) await stats.StopAsync();
            if (db is not null) await db.CloseAsync();
            CoverLoadQueue.Shutdown();
            MainWindow?.Close();
            Exit();
        }
    }

    private async Task CheckRemoteCoversAsync(AppViewModel app, StatsPage page, WebDavLibraryService remote, LrcService matcher, byte[] png)
    {
        Music legacy = new() { Id = 101, Path = "remote://1/track/legacy", IsRemote = true };
        remote.Covers[legacy.Id] = ([], true);
        matcher.MatchAsync = (_, _) => Task.FromResult<byte[]?>(png);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        var image = await CoverLoadQueue.EnqueueAsync(legacy, token);
        Check(image is null && matcher.MatchCount == 0, "disabled auto matching leaves a blank remote thumbnail without calling the matcher");

        AppSettings.IsAutoCoverEnabled = true;
        app.Publish(legacy);
        await Until(() => Images(page).Count(candidate => candidate.Source is not null) == 2, "legacy blank hash album and artist artwork");
        Check(legacy.ImageHash.Length == 0 && matcher.MatchCount == 1,
            "real album and artist templates render an empty-hash remote cover through one shared match");

        AppSettings.IsAutoCoverEnabled = false;
        Music reopened = new() { Id = legacy.Id, Path = legacy.Path, IsRemote = true };
        image = await CoverLoadQueue.EnqueueAsync(reopened, token);
        Check(image is not null && matcher.MatchCount == 1, "reopening a legacy row with auto matching disabled displays the stored cover");
        (image as IDisposable)?.Dispose();

        AppSettings.IsAutoCoverEnabled = true;
        Music otherSource = new() { Id = 102, Path = "remote://2/track/legacy", IsRemote = true };
        remote.Covers[otherSource.Id] = ([], false);
        image = await CoverLoadQueue.EnqueueAsync(otherSource, token);
        Check(image is null && matcher.MatchCount == 1, "another source's remote failure cannot use the cached match or trigger a new one");

        remote.Covers[otherSource.Id] = (png, false);
        image = await CoverLoadQueue.EnqueueAsync(otherSource, token);
        Check(image is not null && matcher.MatchCount == 1, "a server image does not invoke automatic matching");
        (image as IDisposable)?.Dispose();

        remote.Covers[otherSource.Id] = ([], true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        matcher.MatchAsync = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return png;
        };
        var pending = CoverLoadQueue.EnqueueAsync(otherSource, token);
        await started.Task.WaitAsync(token);
        AppSettings.IsAutoCoverEnabled = false;
        ToolUtils.CancelRemoteCoverMatch();
        image = await pending;
        Check(image is null && matcher.MatchCount == 2, "disabling matching cancels an in-flight remote search without displaying or caching it");

        AppSettings.IsAutoCoverEnabled = true;
        string sharedAlbumArt = new('A', 64);
        Music first = new() { Id = 103, Path = "remote://1/track/first", IsRemote = true, Title = "First" };
        Music second = new() { Id = 104, Path = "remote://1/track/second", IsRemote = true, Title = "Second" };
        first.ImageHash = WebDavCachePaths.GetJellyfinImageHash(1, "first", sharedAlbumArt, first.Title, first.Author, first.Album);
        second.ImageHash = WebDavCachePaths.GetJellyfinImageHash(1, "second", sharedAlbumArt, second.Title, second.Author, second.Album);
        string cacheRoot = AppSettings.MusicCoverCache;
        Check(WebDavCachePaths.Cover(cacheRoot, first.ImageHash) == WebDavCachePaths.Cover(cacheRoot, second.ImageHash),
            "album songs share the server artwork cache key");
        remote.Covers[first.Id] = ([], true);
        remote.Covers[second.Id] = ([], true);
        using var alternate = new MemoryStream();
        using var alternateStream = alternate.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, alternateStream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 1, 1, 96, 96, [255, 0, 0, 255]);
        await encoder.FlushAsync();
        byte[] alternatePng = alternate.ToArray();
        matcher.MatchAsync = (music, _) => Task.FromResult<byte[]?>(music.Title == first.Title ? png : alternatePng);
        var covers = await Task.WhenAll(CoverLoadQueue.EnqueueAsync(first, token, prioritize: true),
            CoverLoadQueue.EnqueueAsync(second, token, prioritize: true));
        Check(matcher.MatchCount == 4 && covers[0] is not null && covers[1] is not null &&
            FirstPixel(covers[0]!).SequenceEqual(new byte[] { 0, 128, 255, 255 }) &&
            FirstPixel(covers[1]!).SequenceEqual(new byte[] { 255, 0, 0, 255 }) &&
            File.Exists(WebDavCachePaths.MatchedCover(cacheRoot, first.ImageHash)) &&
            File.Exists(WebDavCachePaths.MatchedCover(cacheRoot, second.ImageHash)) &&
            File.Exists(CoverLoadQueue.GetThumbCachePath(first.ImageHash, CoverLoadQueue.CoverSize)) &&
            File.Exists(CoverLoadQueue.GetThumbCachePath(second.ImageHash, CoverLoadQueue.CoverSize)),
            "two songs in one album resolve their own matched image, decoded thumbnail and disk thumbnail");

        static byte[] FirstPixel(ImageSource cover)
        {
            var bitmap = (Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap)cover;
            using var pixels = bitmap.PixelBuffer.AsStream();
            byte[] result = new byte[4];
            pixels.ReadExactly(result);
            return result;
        }
    }

    private async Task CheckCoverPriorityAsync(string cover)
    {
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolUtils.ReadStarted = music => order.Enqueue(music.Id);
        ToolUtils.ReadGate = gate;
        try
        {
            // Remote covers have a single worker. Keep it busy until both queued items have a waiter.
            var current = CoverLoadQueue.EnqueueAsync(new Music { Id = 21, Path = cover, IsRemote = true }, default);
            await Until(() => order.Count == 1, "single cover worker occupied");
            var ordinary = CoverLoadQueue.EnqueueAsync(new Music { Id = 22, Path = cover, IsRemote = true }, default);
            var next = new Music { Id = 23, Path = cover, IsRemote = true };
            var queued = CoverLoadQueue.EnqueueAsync(next, default);
            var foreground = CoverLoadQueue.EnqueueAsync(next, default, prioritize: true);
            gate.TrySetResult();
            var sources = await Task.WhenAll(current, ordinary, queued, foreground).WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                Check(order.SequenceEqual(new[] { 21, 23, 22 }), "foreground cover promotes the existing request without a duplicate read");
                Check(sources.All(source => source is not null) && !ReferenceEquals(sources[2], sources[3]),
                    "the ordinary queue drains and promoted waiters own independent image sources");
                var bitmap = sources[3] as Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap
                    ?? throw new InvalidOperationException("Foreground cover did not use the direct bitmap path.");
                using var pixels = bitmap.PixelBuffer.AsStream();
                byte[] firstPixel = new byte[4];
                pixels.ReadExactly(firstPixel);
                Check(firstPixel.AsSpan().SequenceEqual(new byte[] { 0, 128, 255, 255 }),
                    "foreground cover preserves decoded premultiplied BGRA pixels");
            }
            finally
            {
                foreach (var source in sources) (source as IDisposable)?.Dispose();
            }
        }
        finally
        {
            gate.TrySetResult();
            ToolUtils.ReadGate = null;
            ToolUtils.ReadStarted = null;
        }
    }

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks.Add("PASS: " + message);
    }
    private static List<Image> Images(DependencyObject root)
    {
        var result = new List<Image>();
        Visit(root, result);
        return result;
    }
    private static void Visit(DependencyObject node, List<Image> result)
    {
        if (node is Image image && Interaction.GetBehaviors(image).OfType<AlbumCoverBehavior>().Any()) result.Add(image);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i), result);
    }
    private static async Task Until(Func<bool> predicate, string message)
    {
        long deadline = Environment.TickCount64 + 10000;
        while (!predicate() && Environment.TickCount64 < deadline) await Task.Delay(20);
        if (!predicate()) throw new TimeoutException(message);
    }
}
