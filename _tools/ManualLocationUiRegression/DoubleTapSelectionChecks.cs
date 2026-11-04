using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

internal sealed partial class TestWindow
{
    private async Task CheckDoubleTapSelectionAsync()
    {
        var viewModel = App.Services.GetRequiredService<SongListViewModel>();
        var playback = App.Services.GetRequiredService<PlaybackCoordinator>();
        var oldMusic = _all[0];
        var target = _all[1];
        var originalSelection = viewModel.SelectedMusic;
        var originalPlaying = _app.SelectedPlaybackMusic;
        var originalQueue = _app.SequentialPlayingList;
        int rebounds = 0;
        void QueueChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(AppViewModel.CurrentPlayingList)) return;
            rebounds++;
            viewModel.SelectedMusic = oldMusic;
        }
        try
        {
            _app.SelectedPlaybackMusic = oldMusic;
            viewModel.SelectedMusic = target;
            _app.PropertyChanged += QueueChanged;
            await viewModel.MusicListView_DoubleTappedAsync();
            Check(rebounds > 0 && ReferenceEquals(playback.LastMusic, target),
                "double tap captures the requested song before synchronous queue selection rebound");
            viewModel.SelectedMusic = oldMusic;
            await viewModel.MusicListView_DoubleTappedAsync(_all[2]);
            Check(ReferenceEquals(playback.LastMusic, _all[2]),
                "explicit row intent overrides an unrelated selected item");
        }
        finally
        {
            _app.PropertyChanged -= QueueChanged;
            _app.SequentialPlayingList = originalQueue;
            _app.SelectedPlaybackMusic = originalPlaying;
            viewModel.SelectedMusic = originalSelection;
        }
    }
}
