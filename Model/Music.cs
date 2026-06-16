using AnimatedWin2dControls.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using SQLite;
using System;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using ZLinq;

namespace WinUIMusicPlayer.Model
{
    public partial class Music : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Path { get; set => SetProperty(ref field, value); } = string.Empty;
        public string Title { get; set => SetProperty(ref field, value); } = string.Empty;
        public string Author { get; set => SetProperty(ref field, value); } = string.Empty;
        public TimeSpan Duration { get; set; }
        public string Album { get; set => SetProperty(ref field, value); } = string.Empty;

        public string FolderPath { get; set; } = string.Empty;
        public string LastLevelFolderPath { get; set => SetProperty(ref field, value); } = string.Empty;
        public string Extension { get; set => SetProperty(ref field, value); } = string.Empty;
        public int Order { get; set => SetProperty(ref field, value); } = 0;
        public int BitDepth { get; set => SetProperty(ref field, value); } = 0;
        public int BitRate { get; set => SetProperty(ref field, value); } = 0;
        public int SampleRate { get; set => SetProperty(ref field, value); }
        public int Channel { get; set => SetProperty(ref field, value); } = 0;
        public int Year { get; set => SetProperty(ref field, value); } = 0;
        public bool IsFavorite { get; set => SetProperty(ref field, value); } = false;
        public int TrackNumber { get; set => SetProperty(ref field, value); } = 0;
        public int DiskNumber { get; set => SetProperty(ref field, value); } = 0;
        public int LyricsOffsetMs
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (App.Services.GetRequiredService<AppViewModel>().IsInitialized)
                    {
                        OffsetMsBus.Publish(value);
                        Save();
                    }
                }
            }
        } = 0;
        public int PlayCount { get; set; } = 0;
        public bool IsLrcSearched { get; set; } = false;
        public bool IsKrcSearched { get; set; } = false;
        [Ignore]
        public int IsExistOnDevice { get; set => SetProperty(ref field, value); } = 0;
        public string ImageHash { get; set => SetProperty(ref field, value); } = string.Empty;
        public DateTime CreateTime { get; set => SetProperty(ref field, value); }
        public DateTime UpdateTime { get; set => SetProperty(ref field, value); }

        [RelayCommand]
        public async Task Play(string page)
        {
            var app = App.Services.GetRequiredService<AppViewModel>();
            switch (page)
            {
                case "FavoriteSongsView":
                    app.SequentialPlayingList = new(app.FavoriteSongs);
                    break;
                case "PlayListSongs":
                    {
                        var src = app.PlayListSongs;
                        var span = src.AsSpan();
                        var arr = new Music[span.Length];
                        for (int i = 0; i < span.Length; i++) arr[i] = span[i].Music;
                        app.SequentialPlayingList = new(arr);
                    }
                    break;
                case "SongsSourceView":
                    app.SequentialPlayingList = new(app.ListSongs);
                    break;
                case "AlbumSongsView":
                    app.SequentialPlayingList = new(app.AlbumSongs);
                    break;
                case "ArtistSongsView":
                    app.SequentialPlayingList = new(app.ArtistSongs);
                    break;
                case "FolderSongsView":
                    app.SequentialPlayingList = new(app.FolderSongs);
                    break;
            }
            await App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(music: this, IsChangeList: true);
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList()
        {
            App.Services.GetRequiredService<AppViewModel>().AddMusicToCurrentPlayList(this);
        }

        [RelayCommand]
        public void UpdateFavourite()
        {
            IsFavorite = !IsFavorite;
            var app = App.Services.GetRequiredService<AppViewModel>();
            if (IsFavorite)
            {
                Order = ComputeNextFavoriteOrder(app);
                app.AddToFavoriteSongs(this);
            }
            else
            {
                app.RemoveFromFavoriteSongs(this);
                Order = 0;
            }
            _ = App.Services.GetRequiredService<MusicDatabaseService>().AddToFavourite(this);
        }

        [RelayCommand]
        public void AddToFavourite()
        {
            if (!IsFavorite)
            {
                IsFavorite = true;
                var app = App.Services.GetRequiredService<AppViewModel>();
                Order = ComputeNextFavoriteOrder(app);
                app.AddToFavoriteSongs(this);
            }
            _ = App.Services.GetRequiredService<MusicDatabaseService>().AddToFavourite(this);
        }

        private static int ComputeNextFavoriteOrder(AppViewModel app)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(app.SongsSource);
            int max = 0;
            for (int i = 0; i < span.Length; i++)
            {
                var m = span[i];
                if (m.IsFavorite && m.Order > max) max = m.Order;
            }
            return max + 1;
        }

        public async void Remove()
        {
            await App.Services.GetRequiredService<MusicDatabaseService>().RemoveMusic(Id);
            App.Services.GetRequiredService<AppViewModel>().RemoveFromSongsSource(this);
            App.Services.GetRequiredService<AppViewModel>().RemoveFromFavoriteSongs(this);
            App.Services.GetRequiredService<AppViewModel>().RemoveFromPlayListSongs(this);
        }

        private async void Save()
        {
            await App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(this);
        }

        public static implicit operator RelativePanel(Music v)
        {
            throw new NotImplementedException();
        }
    }
}
