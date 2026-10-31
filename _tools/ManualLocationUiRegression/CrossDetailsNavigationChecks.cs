using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

internal sealed partial class TestWindow
{
    private async Task CheckCrossDetailsNavigation()
    {
        await Task.Delay(500);
        var browser = _shell.CurrentLibraryPage!;
        int outerNavigations = 0;
        void Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs args) => outerNavigations++;
        _shell.ShellFrame.Navigated += Navigated;
        try
        {
            foreach (bool artist in new[] { false, true })
            {
                browser.SelectBarItem("song");
                await Wait(() => InnerFrame.Content is SongListPage { IsLoaded: true });
                _app.IsBackBtnEnable = false;
                _shell.ShowMusicDetails(new MusicDetailsRequest(_all[0], typeof(SongListPage)));
                await Wait(() => _shell.HasMusicDetails && ((ContentControl)browser.FindName("MusicDetailsOverlay")).IsLoaded);
                await Task.Delay(100);
                if (artist) browser.ViewModel.ArtistButtonCommand.Execute(_all[0].Author);
                else browser.ViewModel.AlbumButtonCommand.Execute(_all[0].Album);
                await Wait(() => artist ? InnerFrame.Content is ArtistPage { IsLoaded: true } : InnerFrame.Content is AlbumPage { IsLoaded: true });
                Check(!_shell.HasMusicDetails && ((ContentControl)browser.FindName("MusicDetailsOverlay")).Content is null,
                    "real footer command closes details before inner-Frame navigation to " + (artist ? "artist" : "album"));
                _shell.CloseMusicDetails();
                await Task.Delay(200);
                Check(_app.IsBackBtnEnable && ReferenceEquals(browser, _shell.CurrentLibraryPage),
                    "cross-link keeps the same browser and the new group's back availability");
            }
            browser.SelectBarItem("song");
            await Wait(() => InnerFrame.Content is SongListPage { IsLoaded: true });
            _app.IsBackBtnEnable = false;
            _shell.ShowMusicDetails(new MusicDetailsRequest(_all[0], typeof(SongListPage)));
            await Task.Delay(150);
            _shell.CloseMusicDetails();
            browser.ViewModel.AlbumButtonCommand.Execute(_all[0].Album);
            await Wait(() => InnerFrame.Content is AlbumPage { IsLoaded: true });
            await Task.Delay(300);
            Check(!_shell.HasMusicDetails && _app.IsBackBtnEnable,
                "a close callback queued before a cross-link cannot restore the old back snapshot over the new group");
            _shell.BeginPlayingTransitionForFixture();
            _shell.ShowMusicDetails(new MusicDetailsRequest(_all[0], typeof(SongListPage)));
            browser.ViewModel.ArtistButtonCommand.Execute(_all[0].Author);
            _shell.FinishPlayingTransitionForFixture();
            await Task.Delay(200);
            Check(!_shell.HasMusicDetails && _app.IsBackBtnEnable,
                "cross-link cancels a pending details request before the playing transition can reopen it");
            Check(outerNavigations == 0 && _shell.ReturnedToPlaying == 0,
                "footer cross-links neither reconstruct the browser nor bounce back to the playing overlay");
        }
        finally
        {
            _shell.CancelDetailsForFixture();
            _shell.FinishPlayingTransitionForFixture();
            _shell.ShellFrame.Navigated -= Navigated;
        }
    }
}
