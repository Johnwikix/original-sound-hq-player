using AnimatedWin2dControls.Messages;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public static class MusicCommands
    {
        public static IAsyncRelayCommand<Music> PlayCommand { get; } = new AsyncRelayCommand<Music>(PlayAsync);
        public static IRelayCommand<Music> UpdateFavouriteCommand { get; } = new RelayCommand<Music>(UpdateFavourite);
        public static IRelayCommand<Music> AddToPlayListCommand { get; } = new RelayCommand<Music>(AddToCurrentPlayList);
        public static IRelayCommand<Music> AddToFavouriteCommand { get; } = new RelayCommand<Music>(AddToFavourite);

        private static async Task PlayAsync(Music? music)
        {
            if (music is null) return;
            var app = App.Services.GetRequiredService<AppViewModel>();
            var page = AppData.CurrentPage;
            if (page == typeof(FavouritePlayListPage))
                app.SequentialPlayingList = new(app.FavoriteSongs);
            else if (page == typeof(PlayListPage))
            {
                var src = app.PlayListSongs;
                var span = src.AsSpan();
                var arr = new Music[span.Length];
                for (int i = 0; i < span.Length; i++) arr[i] = span[i].Music;
                app.SequentialPlayingList = new(arr);
            }
            else if (page == typeof(SongListPage))
                app.SequentialPlayingList = new(app.ListSongs);
            else if (page == typeof(AlbumPage))
                app.SequentialPlayingList = new(app.AlbumSongs);
            else if (page == typeof(ArtistPage))
                app.SequentialPlayingList = new(app.ArtistSongs);
            else if (page == typeof(FolderBrowsePage))
                app.SequentialPlayingList = new(app.FolderSongs);

            await App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(music: music, IsChangeList: true);
        }

        private static void UpdateFavourite(Music? music)
        {
            if (music is null) return;
            music.IsFavorite = !music.IsFavorite;
            var app = App.Services.GetRequiredService<AppViewModel>();
            if (music.IsFavorite)
            {
                music.Order = ComputeNextFavoriteOrder(app);
                app.AddToFavoriteSongs(music);
            }
            else
            {
                app.RemoveFromFavoriteSongs(music);
                music.Order = 0;
            }
            _ = App.Services.GetRequiredService<MusicDatabaseService>().AddToFavourite(music);
        }

        private static void AddToFavourite(Music? music)
        {
            if (music is null) return;
            if (!music.IsFavorite)
            {
                music.IsFavorite = true;
                var app = App.Services.GetRequiredService<AppViewModel>();
                music.Order = ComputeNextFavoriteOrder(app);
                app.AddToFavoriteSongs(music);
            }
            _ = App.Services.GetRequiredService<MusicDatabaseService>().AddToFavourite(music);
        }

        private static void AddToCurrentPlayList(Music? music)
        {
            if (music is null) return;
            App.Services.GetRequiredService<AppViewModel>().AddMusicToCurrentPlayList(music);
        }

        private static int ComputeNextFavoriteOrder(AppViewModel app)
        {
            var span = CollectionsMarshal.AsSpan(app.SongsSource);
            int max = 0;
            for (int i = 0; i < span.Length; i++)
            {
                var m = span[i];
                if (m.IsFavorite && m.Order > max) max = m.Order;
            }
            return max + 1;
        }

        public static void OnLyricsOffsetChanged(Music music, int value)
        {
            if (App.Services.GetRequiredService<AppViewModel>().IsInitialized)
            {
                OffsetMsBus.Publish(value);
                _ = App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(music);
            }
        }

        public static async Task RemoveMusicAsync(Music music)
        {
            await App.Services.GetRequiredService<MusicDatabaseService>().RemoveMusic(music.Id);
            var app = App.Services.GetRequiredService<AppViewModel>();
            app.RemoveFromSongsSource(music);
            app.RemoveFromFavoriteSongs(music);
            app.RemoveFromPlayListSongs(music);
        }
    }
}
