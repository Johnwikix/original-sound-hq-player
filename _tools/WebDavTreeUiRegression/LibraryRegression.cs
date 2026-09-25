using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

namespace WebDavTreeUiRegression;

public sealed partial class TestApp
{
    private static async Task CheckLibraryAndSourcesAsync(Grid host)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "scope-" + Guid.NewGuid().ToString("N") + ".db");
        var sqlite = new SQLiteAsyncConnection(path);
        try
        {
            await sqlite.CreateTableAsync<Music>();
            await sqlite.CreateTableAsync<RemoteTrack>();
            await sqlite.CreateTableAsync<WebDavSource>();
            await sqlite.CreateTableAsync<WebDavCacheSettings>();
            await sqlite.CreateTableAsync<MusicLyrics>();
            await sqlite.CreateTableAsync<PlayListMusic>();
            var db = new MusicDatabaseService(sqlite);
            var first = new WebDavSource { Name = "First", BaseUri = "http://localhost/dav/", Roots = "/dav/" };
            var second = new WebDavSource { Name = "Second", BaseUri = "http://localhost/dav/", Roots = "/dav/" };
            await db.SaveWebDavSourceAsync(first);
            await db.SaveWebDavSourceAsync(second);
            var local = new Music { Title = "Local" };
            await sqlite.InsertAsync(local);
            var inside = (await db.CommitRemoteEntriesAsync(first, "/dav/music/", "initial",
                [new("/dav/music/keep.flac", "keep.flac", false, 10, "v1", null)]))[0];
            var outside = (await db.CommitRemoteEntriesAsync(first, "/dav/music-old/", "initial",
                [new("/dav/music-old/hidden.flac", "hidden.flac", false, 10, "v1", null)]))[0];
            var direct = (await db.CommitRemoteEntriesAsync(first, "/dav/", "initial",
                [new("/dav/direct.flac", "direct.flac", false, 10, "v1", null)]))[0];
            var unrelated = (await db.CommitRemoteEntriesAsync(second, "/dav/", "initial",
                [new("/dav/other.flac", "other.flac", false, 10, "v1", null)]))[0];
            outside.IsFavorite = true;
            await sqlite.UpdateAsync(outside);
            await sqlite.InsertAsync(new PlayListMusic { PlayListId = 1, MusicId = outside.Id });
            Check((await db.GetVisibleMusicAsync()).Count == 5, "initial whole-root library");
            first.Roots = "/dav/music/";
            await db.SaveWebDavSourceAsync(first);
            var visible = await db.GetVisibleMusicAsync();
            Check(visible.Count == 3 && visible.Exists(m => m.Id == inside.Id) && visible.Exists(m => m.Id == local.Id)
                && visible.Exists(m => m.Id == unrelated.Id), "scope shrinks immediately before any network scan");
            Check(await db.GetRemoteMusicAsync(first.Id, "/dav/direct.flac") is null, "excluded browser lookup stays hidden");
            Check((await sqlite.FindAsync<Music>(outside.Id)).IsFavorite && (await sqlite.Table<PlayListMusic>().CountAsync()) == 1,
                "scope changes preserve favorite and playlist identity");
            // New service instance models reading the saved database again after restart.
            Check((await new MusicDatabaseService(sqlite).GetVisibleMusicAsync()).Count == 3, "scope persists across library reload");
            first.Roots = "/dav/";
            await db.SaveWebDavSourceAsync(first);
            var restored = await db.CommitRemoteEntriesAsync(first, "/dav/music-old/", "second",
                [new("/dav/music-old/hidden.flac", "hidden.flac", false, 10, "v1", null)]);
            Check(restored[0].Id == outside.Id && restored[0].IsFavorite && (await db.GetVisibleMusicAsync()).Count == 4,
                "expanded scan restores same track identity");
            await db.MarkRemoteSourceScannedAsync(first.Id, "second");
            visible = await db.GetVisibleMusicAsync();
            Check(visible.Count == 3 && !visible.Exists(m => m.Id == inside.Id), "completed scan removes unobserved tracks from visible query");

            var app = new AppViewModel();
            var queries = new LibraryQueries();
            var library = new WebDavLibraryService(db);
            var sources = new WebDavSourcesViewModel(db, library, new RemotePlaybackService(), new RemoteAudioCache(), app, queries);
            var combo = new ComboBox { ItemsSource = sources.Choices, DisplayMemberPath = "Name" };
            combo.SetBinding(ComboBox.SelectedItemProperty, new Binding
            {
                Source = sources, Path = new PropertyPath(nameof(sources.SelectedSource)), Mode = BindingMode.TwoWay
            });
            host.Children.Add(combo);
            try
            {
                await sources.LoadAsync();
                sources.ShowSongs(first);
                await UntilAsync(() => combo.SelectedItem is MusicSourceChoice choice && choice.Id == first.Id, "real ComboBox selects source");
                await sources.LoadAsync();
                Check(sources.SelectedSource?.Id == first.Id && app.State.Browse.SourceFilterId == first.Id,
                    "refresh keeps existing source selection");
                await library.RemoveSourceAsync(first);
                await UntilAsync(() => sources.SelectedSource?.Id == -1 && app.State.Browse.SourceFilterId == -1
                    && queries.Filter == -1 && combo.SelectedItem is MusicSourceChoice { Id: -1 },
                    "removing selected source resets real ComboBox and both filters to all sources");
                Check((await db.GetVisibleMusicAsync()).Count == 2, "source removal keeps local and other source songs");
                sources.SelectedSource = sources.Choices[1];
                await library.RemoveSourceAsync(second);
                await UntilAsync(() => sources.Choices.Count == 3, "second source removed");
                Check(sources.SelectedSource?.Id == 0, "removing unselected source preserves local filter");
            }
            finally
            {
                await sources.StopAsync();
                host.Children.Remove(combo);
            }
        }
        finally
        {
            await sqlite.CloseAsync();
            File.Delete(path);
        }
    }
}
