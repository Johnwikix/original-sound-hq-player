using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SQLite;
using System;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

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
        public string TranslatedLyrics { get; set => SetProperty(ref field, value); } = string.Empty;
        public string Krc { get; set => SetProperty(ref field, value); } = string.Empty;
        public string TKrc { get; set => SetProperty(ref field, value); } = string.Empty;
        public int LyricsOffsetMs { 
            get; 
            set { 
                if (SetProperty(ref field, value)) 
                {
                    if (App.Services.GetRequiredService<AppViewModel>().IsInitialized) {
                        App.Services.GetRequiredService<AppViewModel>().SendLyricsSettings();
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
            switch (page) {
                case "FavoriteSongsView":
                    App.Services.GetRequiredService<AppViewModel>().SequentialPlayingList = new(App.Services.GetRequiredService<AppViewModel>().FavoriteSongs);
                    break;
                case "PlayListSongs":
                    App.Services.GetRequiredService<AppViewModel>().SequentialPlayingList = new(App.Services.GetRequiredService<AppViewModel>().PlayListSongs.Select(x => x.Music));
                    break;
                case "SongsSourceView":
                    App.Services.GetRequiredService<AppViewModel>().SequentialPlayingList = new(App.Services.GetRequiredService<AppViewModel>().ListSongs);
                    break;
                case "AlbumSongsView":
                    App.Services.GetRequiredService<AppViewModel>().SequentialPlayingList = new(App.Services.GetRequiredService<AppViewModel>().AlbumSongs);
                    break;
                case "ArtistSongsView":
                    App.Services.GetRequiredService<AppViewModel>().SequentialPlayingList = new(App.Services.GetRequiredService<AppViewModel>().ArtistSongs);
                    break;
                case "FolderSongsView":
                    App.Services.GetRequiredService<AppViewModel>().SequentialPlayingList = new(App.Services.GetRequiredService<AppViewModel>().FolderSongs);
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
        public void UpdateFavourite() {
            IsFavorite = !IsFavorite;
            if (IsFavorite)
            {
                Order = App.Services.GetRequiredService<AppViewModel>().SongsSource
                                         .Where(m => m.IsFavorite)
                                         .OrderByDescending(m => m.Order)
                                         .FirstOrDefault()?.Order + 1 ?? 1;
                App.Services.GetRequiredService<AppViewModel>().AddToFavoriteSongs(this);
            }
            else {
                App.Services.GetRequiredService<AppViewModel>().RemoveFromFavoriteSongs(this);
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
                Order = App.Services.GetRequiredService<AppViewModel>().SongsSource
                                         .Where(m => m.IsFavorite)
                                         .OrderByDescending(m => m.Order)
                                         .FirstOrDefault()?.Order + 1 ?? 1;
                App.Services.GetRequiredService<AppViewModel>().AddToFavoriteSongs(this);
            }
            _ = App.Services.GetRequiredService<MusicDatabaseService>().AddToFavourite(this);
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
