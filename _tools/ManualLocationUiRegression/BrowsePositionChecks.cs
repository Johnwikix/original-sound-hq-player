using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;

internal sealed partial class TestWindow
{
    private readonly Music[] _tiles = Enumerable.Range(1, 960).Select(i => new Music
    {
        Id = 2000 + i, Title = "Tile " + i, Album = "Album " + i,
        Author = "Artist " + i, LastLevelFolderPath = "Folder " + i
    }).ToArray();

    private void PrepareGroupSources()
    {
        var groups = _tiles.Select((song, index) => (song, index))
            .GroupBy(x => x.index / 8).Select(g => new MusicGroup("G" + g.Key.ToString("D3"), g.Select(x => x.song))).ToArray();
        _app.AlbumPageSource.Source = groups;
        _app.ArtistPageSource.Source = groups;
        _app.FolderPageSource.Source = groups;
    }

    private async Task<Page> Open(string tag)
    {
        _shell.CurrentLibraryPage!.SelectBarItem(tag);
        await Wait(() => InnerFrame.Content is Page { IsLoaded: true });
        await Task.Delay(650);
        return (Page)InnerFrame.Content;
    }

    private static double Top(ListViewBase list, int index)
        => list.ContainerFromIndex(index) is FrameworkElement element
            ? element.TransformToVisual(Find<ScrollViewer>(list)).TransformPoint(new Point()).Y : double.NaN;

    private async Task<double> Position(ListViewBase list, int index)
    {
        Check(await new ListViewCentering(list).LocateIndexAsync(index), "fixture can scroll the real virtualizing " + list.GetType().Name);
        var scroll = Find<ScrollViewer>(list);
        scroll.ChangeView(null, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + 13), null, true);
        await Task.Delay(180);
        return Top(list, index);
    }

    private void CheckTop(ListViewBase list, int index, double before, string label)
    {
        double after = Top(list, index);
        double error = Math.Abs(after - before);
        _measurements.Add(new { Label = label, ErrorDip = double.IsFinite(error) ? error : (double?)null });
        if (!double.IsFinite(error) || error >= 1.5)
        {
            var scroll = Find<ScrollViewer>(list);
            var state = label.StartsWith("album") ? _app.State.Browse.Positions.Albums :
                label.StartsWith("artist") ? _app.State.Browse.Positions.Artists : _app.State.Browse.Positions.Folders;
            File.WriteAllText(Path.Combine(Program.Output, "position-failure.json"), System.Text.Json.JsonSerializer.Serialize(new {
                Label = label, Before = double.IsFinite(before) ? before : (double?)null, After = double.IsFinite(after) ? after : (double?)null,
                list.IsLoaded, list.ActualHeight, scroll.VerticalOffset, scroll.ViewportHeight, scroll.ExtentHeight, State = state
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        Check(double.IsFinite(error) && error < 1.5, label + " restores the visible anchor within 1.5 DIP");
    }

    private async Task CheckBrowsePositions()
    {
        foreach (var (tag, index) in new[] { ("song", 520), ("favourite", 760) })
        {
            var page = await Open(tag);
            var list = (ListViewBase)page.FindName("MusicListView");
            double top = await Position(list, index);
            await Open(tag == "song" ? "favourite" : "song");
            var rebuilt = await Open(tag);
            Check(!ReferenceEquals(page, rebuilt), tag + " position survives actual page reconstruction");
            CheckTop((ListViewBase)rebuilt.FindName("MusicListView"), index, top, tag + " list");
        }
        _app.CurrentAlbumObj = _app.CurrentArtistObj = _app.CurrentFolderObj = null;
        foreach (var (tag, name) in new[] { ("album", "AlbumGridView"), ("artist", "ArtistsGridView"), ("folder", "FolderGridView") })
        {
            var page = await Open(tag);
            var zoom = (SemanticZoom)page.FindName("BrowseZoom");
            var grid = (GridView)page.FindName(name);
            Check(zoom.IsZoomedInViewActive, tag + " initially shows the detailed grid");
            double gridTop = await Position(grid, 636);
            await Open("song");
            page = await Open(tag);
            zoom = (SemanticZoom)page.FindName("BrowseZoom");
            CheckTop((GridView)page.FindName(name), 636, gridTop, tag + " grouped grid");

            zoom.IsZoomedInViewActive = false;
            await Task.Delay(250);
            var groups = (GridView)page.FindName("ZoomedOutGridView");
            double groupTop = await Position(groups, 80);
            await Open("song");
            page = await Open(tag);
            zoom = (SemanticZoom)page.FindName("BrowseZoom");
            Check(!zoom.IsZoomedInViewActive, tag + " restores SemanticZoom's zoomed-out state");
            CheckTop((GridView)page.FindName("ZoomedOutGridView"), 80, groupTop, tag + " semantic index");
            zoom.IsZoomedInViewActive = true;
            await Task.Delay(250);
            double beforeDetail = await Position((GridView)page.FindName(name), 636);

            if (tag == "album") { _app.CurrentAlbumObj = _tiles[636]; ((AlbumPage)page).ReceiveNavigationParameter(new()); }
            if (tag == "artist") { _app.CurrentArtistObj = _tiles[636]; ((ArtistPage)page).ReceiveNavigationParameter(new()); }
            if (tag == "folder") { _app.CurrentFolderObj = _tiles[636]; ((FolderBrowsePage)page).ReceiveNavigationParameter(new()); }
            await Task.Delay(250);
            var detail = (WinUIMusicPlayer.View.Controls.MusicGroupDetailControl)page.FindName("DetailView");
            double detailTop = await Position((ListViewBase)detail.FindName("MusicListView"), 817);
            await Open("song");
            page = await Open(tag);
            detail = (WinUIMusicPlayer.View.Controls.MusicGroupDetailControl)page.FindName("DetailView");
            Check(detail.Visibility == Visibility.Visible, tag + " keeps the existing group detail navigation state");
            CheckTop((ListViewBase)detail.FindName("MusicListView"), 817, detailTop, tag + " detail list");
            if (page is AlbumPage album) album.CollapseDetail();
            if (page is ArtistPage artist) artist.CollapseDetail();
            if (page is FolderBrowsePage folder) folder.CollapseDetail();
            await Task.Delay(500);
            CheckTop((GridView)page.FindName(name), 636, beforeDetail, tag + " return from details");
            await Capture(tag + "-restored.png");
        }
        var songsPage = await Open("song");
        await Position((ListViewBase)songsPage.FindName("MusicListView"), 570);
        await Open("favourite");
        var saved = _app.State.Browse.Positions.Songs.Items.Bookmark!.Value;
        var reversed = _all.Reverse().ToArray();
        await _app.ListSongs.ReplaceAllAsync(reversed);
        songsPage = await Open("song");
        int movedIndex = Array.FindIndex(reversed, song => song.Id.ToString() == saved.AnchorKey);
        CheckTop((ListViewBase)songsPage.FindName("MusicListView"), movedIndex, saved.Y, "reordered song anchor");

        await Open("favourite");
        await _app.ListSongs.ReplaceAllAsync([]);
        songsPage = await Open("song");
        Check(((ListView)songsPage.FindName("MusicListView")).Items.Count == 0, "empty source waits without restoring a nonexistent row");
        await _app.ListSongs.ReplaceAllAsync(_all);
        await Task.Delay(700);
        int restoredIndex = Array.FindIndex(_all.ToArray(), song => song.Id.ToString() == saved.AnchorKey);
        CheckTop((ListViewBase)songsPage.FindName("MusicListView"), restoredIndex, saved.Y, "late populated song anchor");

        await Open("favourite");
        var withoutAnchor = _all.Where(song => song.Id.ToString() != saved.AnchorKey).ToArray();
        await _app.ListSongs.ReplaceAllAsync(withoutAnchor);
        songsPage = await Open("song");
        var fallback = Find<ScrollViewer>((ListView)songsPage.FindName("MusicListView"));
        Check(double.IsFinite(fallback.VerticalOffset) && fallback.VerticalOffset <= fallback.ScrollableHeight,
            "removed anchor falls back to a valid nearby position");
        await Open("favourite");
        await _app.ListSongs.ReplaceAllAsync(_all);

        // A different query must not reuse the old query's bookmark.
        await Open("favourite");
        _app.State.Browse.SearchText = "different query";
        var reset = await Open("song");
        Check(Find<ScrollViewer>((ListView)reset.FindName("MusicListView")).VerticalOffset < 1,
            "different filter context starts at the top");
        _app.State.Browse.SearchText = "";
    }
}
