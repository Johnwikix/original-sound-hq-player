using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SQLite;
using System;
using System.Linq;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;

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
        public string Lyrics { get; set => SetProperty(ref field, value); } = string.Empty;
        public string TranslatdeLyrics { get; set => SetProperty(ref field, value); } = string.Empty;
        public int PlayCount { get; set => SetProperty(ref field, value); } = 0;
        [Ignore]
        public int IsExistOnDevice { get; set => SetProperty(ref field, value); } = 0;
        public DateTime CreateTime { get; set => SetProperty(ref field, value); }
        public DateTime UpdateTime { get; set => SetProperty(ref field, value); }

        [RelayCommand]
        public void Play(string page) 
        {
            switch (page) {
                case "FavoriteSongsView":
                    App.Services.GetRequiredService<AppObservableObj>().SequentialPlayingList = new(App.Services.GetRequiredService<AppObservableObj>().FavoriteSongs);
                    break;
                case "PlayListSongs":
                    App.Services.GetRequiredService<AppObservableObj>().SequentialPlayingList = new(App.Services.GetRequiredService<AppObservableObj>().PlayListSongs.Select(x => x.Music));
                    break;
                case "AllSongsView":
                    App.Services.GetRequiredService<AppObservableObj>().SequentialPlayingList = new([.. App.Services.GetRequiredService<AppObservableObj>().AllSongsView.Cast<Music>()]);
                    break;
                case "AlbumSongsView":
                    App.Services.GetRequiredService<AppObservableObj>().SequentialPlayingList = new([.. App.Services.GetRequiredService<AppObservableObj>().AlbumSongsView.Cast<Music>()]);
                    break;
                case "ArtistSongsView":
                    App.Services.GetRequiredService<AppObservableObj>().SequentialPlayingList = new([.. App.Services.GetRequiredService<AppObservableObj>().ArtistSongsView.Cast<Music>()]);
                    break;
                case "FolderSongsView":
                    App.Services.GetRequiredService<AppObservableObj>().SequentialPlayingList = new([.. App.Services.GetRequiredService<AppObservableObj>().FolderSongsView.Cast<Music>()]);
                    break;
            }
            App.Services.GetRequiredService<MusicBrowsePage>().PlayMusic(music: this, IsChangeList: true);
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList()
        {
            App.Services.GetRequiredService<AppObservableObj>().AddMusicToCurrentPlayList(this);
        }

        [RelayCommand]
        public void UpdateFavourite() {
            IsFavorite = !IsFavorite;
            if (IsFavorite)
            {
                Order = App.Services.GetRequiredService<AppObservableObj>().AllSongs
                                         .Where(m => m.IsFavorite)
                                         .OrderByDescending(m => m.Order)
                                         .FirstOrDefault()?.Order + 1 ?? 1;
                App.Services.GetRequiredService<AppObservableObj>().AddToFavoriteSongs(this);
            }
            else {
                App.Services.GetRequiredService<AppObservableObj>().RemoveFromFavoriteSongs(this);
                Order = 0;
            }
            _ = App.Services.GetRequiredService<MusicDatabaseService>().AddToFavourite(this);
        }

        public async void Remove() 
        {
            await App.Services.GetRequiredService<MusicDatabaseService>().RemoveMusic(Id);
            App.Services.GetRequiredService<AppObservableObj>().RemoveFromAllSongs(this);
            App.Services.GetRequiredService<AppObservableObj>().RemoveFromFavoriteSongs(this);
            App.Services.GetRequiredService<AppObservableObj>().RemoveFromPlayListSongs(this);
        }

    }
}
