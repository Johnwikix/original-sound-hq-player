using Microsoft.UI.Dispatching;
using SQLite;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

internal sealed partial class RegressionApplication
{
    private async Task CheckRecommendationPreferencesAsync(HomeViewModel home, MusicDatabaseService data,
        SQLiteAsyncConnection database, FixtureEnrichment analysis)
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
        await home.StopAsync();
        var stopped = analysis.LastTracks;
        data.NotifyRecommendationPreferencesChanged();
        await Task.Delay(60);
        Check(ReferenceEquals(analysis.LastTracks, stopped), "stopped home detaches playlist preference notifications");
    }
}
