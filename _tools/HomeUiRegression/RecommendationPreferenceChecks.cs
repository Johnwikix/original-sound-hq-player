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
        var app = Field<AppViewModel>(home, "_app");
        var playback = Field<PlaybackCoordinator>(home, "_playback");
        var primary = home.FeaturedSongs.Concat(home.CardSongs).Concat(home.Songs).Select(song => song.Music).ToArray();
        var theme = home.Themes.First();
        await theme.PlayCommand.ExecuteAsync(theme);
        Check(app.SequentialPlayingList.SequenceEqual(primary) && playback.LastId == theme.Artwork.Id,
            $"carousel starts the selected song with the shared primary queue (actual {app.SequentialPlayingList.Count}, expected {primary.Length})");
        var sharedQueue = app.SequentialPlayingList;
        foreach (var song in new[] { home.CardSongs.Last(), home.Songs.Last() })
        {
            await song.PlayCommand.ExecuteAsync(song);
            Check(ReferenceEquals(sharedQueue, app.SequentialPlayingList) && playback.LastId == song.Music.Id,
                "side card and primary shelf start the chosen song in the same queue");
        }
        var sideSong = home.CardSongs[0];
        sideSong.Music.IsRemoteOffline = true;
        await sideSong.PlayCommand.ExecuteAsync(sideSong);
        Check(playback.LastId == sideSong.Music.Id && ReferenceEquals(sharedQueue, app.SequentialPlayingList),
            "an explicit play still reaches the coordinator when the displayed source becomes unavailable");
        sideSong.Music.IsRemoteOffline = false;
        Check(home.OpenDetails(sideSong), "side card opens the real home detail presentation");
        var details = home.DetailPresentation!;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)details.PlayCommand).ExecuteAsync(details.PlayParameter);
        Check(app.SequentialPlayingList.SequenceEqual(primary) && playback.LastId == sideSong.Music.Id && playback.LastExactVersion,
            "details starts the inspected version inside the same primary queue");
        home.BeginCloseDetails();
        home.CompleteCloseDetails();
        var activeQueue = app.SequentialPlayingList;
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
        Check(ReferenceEquals(activeQueue, app.SequentialPlayingList), "refresh and navigation preserve the playing snapshot");
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
        var representative = app.FindById(1)!;
        representative.SongId = 7001;
        representative.SongPreferredMusicId = representative.Id;
        representative.IsMissing = true;
        var sibling = new WinUIMusicPlayer.Model.Music { Id = 7002, SongId = representative.SongId, SongPreferredMusicId = representative.Id,
            SourceId = 2, Title = representative.Title, Author = representative.Author, Album = representative.Album, PlayCount = 1 };
        app.Replace([representative, sibling]);
        await Wait(() => analysis.LastTracks.Count == 1 && analysis.LastTracks[0].IsAvailable && analysis.LastTracks[0].PlayCount == representative.PlayCount + 1 && !Field<bool>(home, "_running"));
        Check(home.Familiar.Count == 1 && home.Familiar[0].Music.Id == representative.Id && home.PlaySongCommand.CanExecute(home.Familiar[0]),
            "the missing display version stays on the shelf with its playable sibling and combined history");
        var library = Enumerable.Range(1, 260).Select(id => new WinUIMusicPlayer.Model.Music
        {
            Id = 8000 + id, Title = "Track " + id, Author = "Artist " + id,
            Album = "Album " + id, PlayCount = 1
        }).ToList();
        analysis.Scores = library.ToDictionary(music => music.Id, music => 1f - (music.Id - 8000) / 300f);
        app.Replace(library);
        await Wait(() => home.Soundalikes.Count == 12 && home.Rediscover.Count == 12);
        foreach (var (name, shelf, header) in new[]
        {
            ("SoundalikesShelf", home.Soundalikes, "Similar sounds"), ("RediscoverShelf", home.Rediscover, "Listen again")
        })
        {
            var control = (RecommendationShelf)page.FindName(name);
            Check(control.Visibility == Visibility.Visible && ReferenceEquals(control.ItemsSource, shelf)
                && control.Header is TextBlock title && title.Text == header, "compiled binding and localized header for " + name);
            var song = shelf[0];
            home.ShowPreview(song);
            Check(ReferenceEquals(home.PreviewSong, song), "preview accepts " + name);
            await song.PlayCommand.ExecuteAsync(song);
            Check(app.SequentialPlayingList.SequenceEqual(shelf.Select(item => item.Music)) && playback.LastId == song.Music.Id,
                "themed shelf plays its own queue for " + name);
        }
        var currentBatch = Field<int>(home, "_batch");
        var lastQueue = app.SequentialPlayingList;
        home.Deactivate();
        var exposures = Field<WinUIMusicPlayer.Model.Recommendations.HomeRecommendation[]>(home, "_recentRecommendations");
        exposures[0] = exposures[0] with { ExposedAtUtcTicks = DateTime.UtcNow.AddMinutes(-31).Ticks };
        home.Activate(DispatcherQueue.GetForCurrentThread());
        await Wait(() => !Field<bool>(home, "_running") && Field<int>(home, "_batch") != currentBatch);
        Check(ReferenceEquals(lastQueue, app.SequentialPlayingList), "a later visit rotates a stale batch without replacing playback");
        await home.StopAsync();
        var stopped = analysis.LastTracks;
        data.NotifyRecommendationPreferencesChanged();
        await Task.Delay(60);
        Check(ReferenceEquals(analysis.LastTracks, stopped), "stopped home detaches playlist preference notifications");
    }
}
