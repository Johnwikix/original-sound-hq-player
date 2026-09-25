using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.View;

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
    public static IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();
    public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
    internal static Func<int, Music?>? LegacyCoverLookup;
    private Window? _window;
    private readonly string _resultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            File.AppendAllText(_resultPath, "UNHANDLED: " + args.Exception);
            Environment.Exit(1);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(_resultPath, "STARTED\n");
        try
        {
            var library = new LibraryState();
            var queries = new LibraryQueries(library);
            var page = new PlayListPage();
            var playlists = page.ViewModel.AppViewModel.AllPlayList;
            var playlist = new PlayList { Id = 1, Name = "Existing playlist", SongCount = 2 };
            var empty = new PlayList { Id = 2, Name = "Empty playlist" };
            playlists.Add(playlist);
            playlists.Add(empty);
            LegacyCoverLookup = id => playlists.FirstOrDefault(p => p.Id == id)?.CoverMusic;
            _window = new Window { Content = page, Title = "Playlist cover regression" };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(700, 500));
            _window.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
            _window.Activate();
            await Until(() => CoverImage(page.TestGrid, playlist) != null, "card renders before library and mappings");
            Check(CoverImage(page.TestGrid, playlist)!.Source == null, "initial placeholder is empty");

            // Prime the empty index, then deliver the library/mapping asynchronously like startup.
            queries.FindById(1);
            var first = new Music { Id = 1, Cover = new WriteableBitmap(2, 2) };
            var second = new Music { Id = 2, Cover = new WriteableBitmap(3, 3) };
            var mappings = await Task.Run(() => new List<PlayListMusic>
            {
                new() { PlayListId = 1, MusicId = 1, Order = 1 },
                new() { PlayListId = 1, MusicId = 2, Order = 2 },
                new() { PlayListId = 1, MusicId = 999, Order = 3 },
            });
            library.Replace([first, second]);
            int uiThread = Environment.CurrentManagedThreadId;
            int coverChanges = 0;
            playlist.PropertyChanged += (_, e) =>
            {
                Check(Environment.CurrentManagedThreadId == uiThread, "property notification stays on UI thread");
                if (e.PropertyName == nameof(PlayList.CoverMusic)) coverChanges++;
            };
            PlaylistSummaryProjection.Refresh(playlists, mappings, queries);
            await Until(() => ReferenceEquals(CoverImage(page.TestGrid, playlist)?.Source, second.Cover),
                "late mapping updates existing card without changing Id or persisted SongCount");
            Check(playlist.SongCount == 2 && empty.CoverMusic == null, "missing tracks and empty playlists are handled");
            Check(coverChanges == 1, "cover change is notified independently of song count");

            mappings[0].Order = 4;
            PlaylistSummaryProjection.Refresh(playlists, mappings, queries);
            await Until(() => ReferenceEquals(CoverImage(page.TestGrid, playlist)?.Source, first.Cover), "reorder updates cover with unchanged count");
            library.Replace([second]);
            PlaylistSummaryProjection.Refresh(playlists, mappings, queries);
            await Until(() => ReferenceEquals(CoverImage(page.TestGrid, playlist)?.Source, second.Cover), "library deletion selects remaining track");
            Check(playlist.SongCount == 1, "deleted library track leaves count consistent");

            var newlyAdded = new PlayList { Id = 3, Name = "New playlist" };
            playlists.Add(newlyAdded);
            mappings.Add(new() { PlayListId = 3, MusicId = 2, Order = 1 });
            PlaylistSummaryProjection.Refresh(playlists, mappings, queries);
            await Until(() => ReferenceEquals(CoverImage(page.TestGrid, newlyAdded)?.Source, second.Cover), "new playlist gets cover");
            mappings.Clear();
            PlaylistSummaryProjection.Refresh(playlists, mappings, queries);
            await Until(() => CoverImage(page.TestGrid, playlist)?.Source == null && playlist.SongCount == 0, "removing all members clears cover");

            var table = new SQLite.TableMapping(typeof(PlayList));
            Check(table.Columns.All(c => c.Name != nameof(PlayList.CoverMusic)), "cover projection is not a database column");
            File.AppendAllText(_resultPath, "PASS: real card XAML, image behavior, late data, reorder, library removal, new/empty playlists, notifications and SQLite Ignore.\n");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.AppendAllText(_resultPath, "FAIL: " + ex);
        }
        finally
        {
            _window?.Close();
            Exit();
        }
    }

    private static Image? CoverImage(GridView grid, PlayList playlist)
        => grid.ContainerFromItem(playlist) is DependencyObject container ? FindImage(container) : null;

    private static Image? FindImage(DependencyObject node)
    {
        if (node is Image image) return image;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindImage(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        long end = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < end) await Task.Delay(20);
        Check(condition(), message);
    }
}
