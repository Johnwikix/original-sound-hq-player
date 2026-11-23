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
        await Wait(() => ActiveTab is Page { IsLoaded: true });
        await Task.Delay(650);
        return (Page)ActiveTab;
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
            File.WriteAllText(Path.Combine(Program.Output, "position-failure.json"), System.Text.Json.JsonSerializer.Serialize(new {
                Label = label, Before = double.IsFinite(before) ? before : (double?)null, After = double.IsFinite(after) ? after : (double?)null,
                list.IsLoaded, list.ActualHeight, scroll.VerticalOffset, scroll.ViewportHeight, scroll.ExtentHeight
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        Check(double.IsFinite(error) && error < 1.5, label + " retains the visible anchor within 1.5 DIP");
    }

    private async Task CheckBrowsePositions()
    {
        foreach (var (tag, index) in new[] { ("song", 520), ("favourite", 760) })
        {
            var page = await Open(tag);
            var list = (ListViewBase)page.FindName("MusicListView");
            double top = await Position(list, index);
            await Open(tag == "song" ? "favourite" : "song");
            Check(ReferenceEquals(page, await Open(tag)), tag + " reuses the original page");
            CheckTop(list, index, top, tag + " list after tab switch");
            await ReturnToLibrary();
            Check(ReferenceEquals(page, ActiveTab), tag + " stays selected after returning to the library");
            CheckTop(list, index, top, tag + " list after returning to the library");
        }
        _app.CurrentAlbumObj = _app.CurrentArtistObj = _app.CurrentFolderObj = null;
        foreach (var (tag, name) in new[] { ("album", "AlbumGridView"), ("artist", "ArtistsGridView") })
        {
            var page = await Open(tag);
            var zoom = (SemanticZoom)page.FindName("BrowseZoom");
            var grid = (GridView)page.FindName(name);
            Check(zoom.IsZoomedInViewActive, tag + " initially shows the detailed grid");
            double gridTop = await Position(grid, 636);
            await Open("song");
            Check(ReferenceEquals(page, await Open(tag)), tag + " reuses the original group page");
            CheckTop(grid, 636, gridTop, tag + " grouped grid");

            zoom.IsZoomedInViewActive = false;
            await Task.Delay(250);
            var groups = (GridView)page.FindName("ZoomedOutGridView");
            double groupTop = await Position(groups, 80);
            await Open("song");
            page = await Open(tag);
            zoom = (SemanticZoom)page.FindName("BrowseZoom");
            Check(!zoom.IsZoomedInViewActive, tag + " retains SemanticZoom's zoomed-out state");
            CheckTop((GridView)page.FindName("ZoomedOutGridView"), 80, groupTop, tag + " semantic index");
            zoom.IsZoomedInViewActive = true;
            await Task.Delay(250);
            double beforeDetail = await Position((GridView)page.FindName(name), 636);

            if (tag == "album") { _app.CurrentAlbumObj = _tiles[636]; ((ILibraryTab)page).Activate(); }
            if (tag == "artist") { _app.CurrentArtistObj = _tiles[636]; ((ILibraryTab)page).Activate(); }
            await Task.Delay(250);
            var detail = (WinUIMusicPlayer.View.Controls.MusicGroupDetailControl)page.FindName("DetailView");
            double detailTop = await Position((ListViewBase)detail.FindName("MusicListView"), 817);
            await Open("song");
            page = await Open(tag);
            detail = (WinUIMusicPlayer.View.Controls.MusicGroupDetailControl)page.FindName("DetailView");
            Check(detail.Visibility == Visibility.Visible, tag + " keeps the existing group detail navigation state");
            var detailList = (ListViewBase)detail.FindName("MusicListView");
            CheckTop(detailList, 817, detailTop, tag + " detail list");
            await ReturnToLibrary();
            Check(ReferenceEquals(page, ActiveTab) && _app.IsBackBtnEnable,
                tag + " keeps details selected and restores back availability after returning");
            CheckTop(detailList, 817, detailTop, tag + " detail list after returning to the library");
            if (page is AlbumPage album) album.CollapseDetail();
            if (page is ArtistPage artist) artist.CollapseDetail();
            await Task.Delay(500);
            CheckTop((GridView)page.FindName(name), 636, beforeDetail, tag + " return from details");
            await Capture(tag + "-restored.png");
        }
        await Open("favourite");
        await _app.ListSongs.ReplaceAllAsync([]);
        var songs = (ListView)(await Open("song")).FindName("MusicListView");
        Check(songs.Items.Count == 0, "cached songs page observes a collection cleared while away");
        await _app.ListSongs.ReplaceAllAsync(_all);
        await Wait(() => songs.Items.Count == _all.Count);
        Check(ReferenceEquals(songs.Items[0], _all[0]), "cached songs page observes a late collection update");
    }

    private async Task ReturnToLibrary()
    {
        _shell.ShellFrame.Navigate(typeof(FolderBrowsePage));
        await Task.Delay(150);
        _app.IsBackBtnEnable = false;
        _shell.NavigateToMusicBrowsePage();
        await Wait(() => ActiveTab is Page { IsLoaded: true });
        await Task.Delay(400);
    }
}
