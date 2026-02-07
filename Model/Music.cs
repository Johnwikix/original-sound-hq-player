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
        public string Path { get; set; }

        private string _title;
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        private string _author;
        public string Author
        {
            get => _author;
            set => SetProperty(ref _author, value);
        }


        public TimeSpan Duration { get; set; }
        private string _album;
        public string Album
        {
            get => _album;
            set => SetProperty(ref _album, value);
        }

        public string FolderPath { get; set; }
        public string _lastLevelFolderPath;
        public string LastLevelFolderPath
        {
            get => _lastLevelFolderPath;
            set => SetProperty(ref _lastLevelFolderPath, value);
        }

        public string Extension { get; set; }
        public int Order { get; set; }
        public int BitDepth { get; set; }
        public int BitRate { get; set; }
        public int SampleRate { get; set; }
        [Ignore]
        public int PlayListOrder { get; set; }
        public int Channel { get; set; }
        public int Year { get; set; }
        private bool _isFavorite = false;
        public bool IsFavorite
        {
            get => _isFavorite;
            set => SetProperty(ref _isFavorite, value);
        }

        private int _trackNumber;
        public int TrackNumber
        {
            get => _trackNumber;
            set => SetProperty(ref _trackNumber, value);
        }

        private int _diskNumber;
        public int DiskNumber
        {
            get => _diskNumber;
            set => SetProperty(ref _diskNumber, value);
        }

        private string _lyrics;
        public string Lyrics
        {
            get => _lyrics;
            set => SetProperty(ref _lyrics, value);
        }

        private string _tranlatedLyrics;
        public string TranslatdeLyrics {
            get => _tranlatedLyrics;
            set => SetProperty(ref _tranlatedLyrics, value);
        }

        private int _playCount = 0;
        public int PlayCount
        {
            get => _playCount;
            set => SetProperty(ref _playCount, value);
        }

        private int _isExistOnDevice = 0;
        [Ignore]
        public int IsExistOnDevice
        {
            get => _isExistOnDevice;
            set => SetProperty(ref _isExistOnDevice, value);
        }

        private DateTime _createTime;
        public DateTime CreateTime
        {
            get => _createTime;
            set => SetProperty(ref _createTime, value);
        }
        private DateTime _updateTime;
        public DateTime UpdateTime
        {
            get => _updateTime;
            set => SetProperty(ref _updateTime, value);
        }
        [RelayCommand]
        public void Play(string page) 
        {
            switch (page) {
                case "FavoriteSongsView":
                    App.Services.GetRequiredService<AppObservableObj>().SequentialPlayingList = new(App.Services.GetRequiredService<AppObservableObj>().FavoriteSongs);
                    App.Services.GetRequiredService<AppObservableObj>().FavoriteSongsSelectedMusic = this;
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
            await App.Services.GetRequiredService<MusicDatabaseService>().RemoveMusic(this.Id);
        }

    }
}
