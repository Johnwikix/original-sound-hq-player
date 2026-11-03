using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SQLite;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class RegressionApplication
{
    private async Task CheckRecommendationPreferencesAsync(HomeViewModel home, MusicDatabaseService data,
        SQLiteAsyncConnection database, FixtureEnrichment analysis, HomePage page)
    {
        Check(analysis.LastTracks.All(track => !track.IsInPlaylist), "empty playlists do not invent positive preferences");
        await database.ExecuteAsync("INSERT INTO PlayList VALUES(123),(124)");
        var displayed = home.Songs.ToArray();
        await data.AddMusicToPlayList(123, 31);
        await Task.Delay(80);
        Check(home.Songs.SequenceEqual(displayed) && analysis.LastTracks.All(track => !track.IsInPlaylist),
            "adding to a playlist defers recalculation without moving the current cards");
        home.RefreshCommand.Execute(null);
        await Wait(() => analysis.LastTracks.Any(track => track.Id == 31 && track.IsInPlaylist));
        Check(analysis.LastTracks.Single(track => track.Id == 31).IsInPlaylist,
            "explicit refresh loads real SQLite playlist membership into acoustic and metadata inputs");
        await data.AddMusicToPlayList(124, 31);
        Check((await data.GetRecommendationPlaylistIdsAsync(default)).Count == 1,
            "overlapping real playlists contribute a single positive identity");
        await database.ExecuteAsync("DELETE FROM PlayListMusic WHERE MusicId=31");
        data.NotifyRecommendationPreferencesChanged();
        home.RefreshCommand.Execute(null);
        await Wait(() => analysis.LastTracks.All(track => !track.IsInPlaylist));
        Check(analysis.LastTracks.All(track => !track.IsInPlaylist), "removing the last playlist membership removes the explicit preference");
        home.Deactivate();
        await data.AddMusicToPlayList(123, 32);
        home.Activate(DispatcherQueue.GetForCurrentThread());
        await Wait(() => analysis.LastTracks.Any(track => track.Id == 32 && track.IsInPlaylist));
        Check(analysis.LastTracks.Single(track => track.Id == 32).IsInPlaylist,
            "returning to the page applies playlist changes received while inactive");
        Check(home.Discovery.Count > 0 && ((RecommendationShelf)page.FindName("DiscoveryShelf")).Visibility == Visibility.Visible,
            "shipping alternative-genre shelf remains visible with recent listening preferences");
        var app = Field<AppViewModel>(home, "_app");
        var allHeardSongs = app.SongsSource.ToList();
        foreach (var song in allHeardSongs) song.PlayCount = 5;
        app.Replace(allHeardSongs);
        await Wait(() => analysis.LastTracks.All(track => track.PlayCount == 5) && !Field<bool>(home, "_running"));
        Check(home.Songs.Count > 0 && home.Familiar.Count > 0 && home.Discovery.Count > 0,
            "an entirely heard library publishes all three shelves through the shipping view model");
        Check(((RecommendationShelf)page.FindName("SongsShelf")).Visibility == Visibility.Visible
            && ((RecommendationShelf)page.FindName("FamiliarShelf")).Visibility == Visibility.Visible
            && ((RecommendationShelf)page.FindName("DiscoveryShelf")).Visibility == Visibility.Visible,
            "compiled bindings keep all three shelves visible after every track has been played");
        await database.ExecuteAsync("INSERT INTO PlayListMusic (PlayListId, MusicId, [Order]) SELECT 123, Id, Id FROM Music");
        data.NotifyRecommendationPreferencesChanged();
        home.RefreshCommand.Execute(null);
        await Wait(() => analysis.LastTracks.All(track => track.IsInPlaylist) && !Field<bool>(home, "_running"));
        Check(home.Songs.Count > 0 && home.Familiar.Count > 0 && home.HasContent && !home.IsUnavailable,
            "all-playlisted library retains downweighted recommendations and unpenalized familiar content");
        app.Replace([app.FindById(1)!]);
        await Wait(() => home.Familiar.Count == 1 && home.Songs.Count == 0 && !Field<bool>(home, "_running"));
        Check(((ScrollViewer)page.FindName("ContentScroll")).Visibility == Visibility.Visible
            && ((RecommendationShelf)page.FindName("FamiliarShelf")).Visibility == Visibility.Visible
            && ((RecommendationShelf)page.FindName("SongsShelf")).Visibility == Visibility.Collapsed
            && ((StackPanel)page.FindName("UnavailablePanel")).Visibility == Visibility.Collapsed,
            "compiled bindings show familiar-only results without an empty primary shelf or unavailable overlay");
        Check(home.PlaySongCommand.CanExecute(home.Familiar[0]), "familiar-only cards retain the shared playback command");
        await home.StopAsync();
        var stopped = analysis.LastTracks;
        data.NotifyRecommendationPreferencesChanged();
        await Task.Delay(60);
        Check(ReferenceEquals(analysis.LastTracks, stopped), "stopped home detaches playlist preference notifications");
    }
}
