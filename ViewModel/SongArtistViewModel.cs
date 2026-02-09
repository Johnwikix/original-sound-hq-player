using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class SongArtistViewModel : ObservableObject
    {
        public Music SelectedMusic { get; set => SetProperty(ref field, value); }
        public string SecondTitle { get; set => SetProperty(ref field, value); }
        public string ThirdTitle { get; set => SetProperty(ref field, value); }
        private MusicBrowsePage? _parentPage { get;}
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        public AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private SongArtistListPage _currentPage { get; set; }

        public SongArtistViewModel(MusicBrowseViewModel musicBrowseViewModel, AppObservableObj appObservableObj, MusicDatabaseService musicDatabaseService)
        {
            _parentPage = musicBrowseViewModel?.MusicBrowsePage;
            //_parentPage.refreshSong += RefreshSong;
            _musicBrowseViewModel = musicBrowseViewModel;
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
        }
        public void SetCurrentPage(SongArtistListPage page)
        {
            _currentPage = page;
        }
        public void ReceiveNavigation()
        {
            RefreshUsbDeviceMusicList(null, null);
            RefreshPage();
            UpdateMusicListView();
            App.MainWindow.IsBackBtnEnable = true;
        }

        //public void ClearUsbDeviceMusicList(object? sender, EventArgs e)
        //{

        //    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //    {
        //        foreach (var music in MusicList)
        //        {
        //            music.IsExistOnDevice = 0;
        //        }
        //    });
        //}

        public void RefreshUsbDeviceMusicList(object? sender, EventArgs e)
        {
            //ToolUtils.RefreshUsbDeviceMusicList(MusicList);
        }

        //private void RefreshSong(object? sender, EventArgs e)
        //{
        //    RefreshPage();
        //}

        public void RefreshPage()
        {
            if ( _parentPage is not null && AppObservableObj.CurrentArtistObj is not null)
            {
                var authorAlbums = AppObservableObj.AllSongs.AsValueEnumerable()
                    .Where(music => music.Author == AppObservableObj.CurrentArtistObj.Author)
                    .Select(music => music.Album)
                    .Distinct()
                    .Count();
                SecondTitle = $"{AppObservableObj.AllSongs.AsValueEnumerable().Count(music => music.Author == AppObservableObj.CurrentArtistObj.Author)} {ToolUtils.GetString("NumberOfSongs")} · {authorAlbums} {ToolUtils.GetString("NumberOfAlbums")}";
                ThirdTitle = ToolUtils.GetString("Artist");                
                //LoadMusicAsync(musics, "artist");
            }
        }

        public async Task<bool> IsDeleteFromDisk()
        {
            if (_parentPage is null)
            {
                return false;
            }
            return await _parentPage.AreUSureDeleteFromDisk();
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (AppObservableObj.CurrentPlayingList is not null)
                {
                    var selectedMusic = AppObservableObj.AllSongs.AsValueEnumerable().FirstOrDefault(music =>
                        music.Id == AppObservableObj.CurrentPlayingMusic.Id);

                    if (selectedMusic is not null)
                    {
                        SelectedMusic = selectedMusic;
                        _currentPage?.OnScrollToMusic(selectedMusic);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            }
        }

        public void MusicListView_DoubleTapped()
        {
            if (SelectedMusic is not null && _parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new([.. AppObservableObj.ArtistSongsView.Cast<Music>()]);
                _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                AppObservableObj.SequentialPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                _parentPage?.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First(), IsChangeList: true);
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppObservableObj.SequentialPlayingList = new([.. AppObservableObj.ArtistSongsView.Cast<Music>()]);
                    _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
                }
            }
        }

        public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                for (int i = AppObservableObj.AllSongs.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(AppObservableObj.AllSongs[i]))
                    {
                        if (ToolUtils.DeleteFileFromDisk((AppObservableObj.AllSongs[i].Path)))
                        {
                            AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                        }
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                }
            }
        }

        public async Task SetAsFavoriteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == item.Id)?.UpdateFavourite();
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.UpdateFavourite();
                }
            }
        }

        public void OpenInExplorer_Click()
        {
            if (SelectedMusic is not null)
            {
                var filePath = SelectedMusic.Path;
                if (File.Exists(filePath))
                {
                    try
                    {
                        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"打开资源管理器时出错: {ex.Message}");
                    }
                }
                else
                {
                    Debug.WriteLine($"文件不存在: {filePath}");
                }
            }
        }

        public async Task ConvertAudio_Click(IEnumerable<Music> uniqueSelectedMusics, MenuFlyoutItem? menuItem)
        {
            _musicBrowseViewModel?.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
        }

        [RelayCommand]
        private async void IsFavouriteIconButtonChange(Music music)
        {
            if (music is not null)
            {
                await AppObservableObj.AddToFavourite(music);
                AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
            }
        }

        public async void ReGetLyrics_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            AppObservableObj.ReGetLyrics(uniqueSelectedMusics, SelectedMusic);
            //if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            //{
            //    foreach (Music item in uniqueSelectedMusics)
            //    {
            //        (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(item);
            //        Music? music = AppObservableObj.AllSongs.AsValueEnumerable().Where(m => m.Id == item.Id).FirstOrDefault();
            //        if (music is not null)
            //        {
            //            music.Lyrics = lyrics;
            //            music.TranslatdeLyrics = transLrc;
            //            await _musicDatabaseService.UpdateMusicInfo(music);
            //        }
            //    }
            //}
            //else
            //{
            //    (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(SelectedMusic);
            //    Music? music = AppObservableObj.AllSongs.AsValueEnumerable().Where(m => m.Id == SelectedMusic.Id).FirstOrDefault();
            //    if (music is not null)
            //    {
            //        music.Lyrics = lyrics;
            //        music.TranslatdeLyrics = transLrc;
            //        await _musicDatabaseService.UpdateMusicInfo(music);
            //    }
            //}
        }

        public void AuthorTextBlock_Tapped(string artist)
        {
            if (_parentPage is not null)
            {
                _parentPage.SelectBarArtist(artist);
            }
        }

        public void AlbumTextBlock_Tapped(string albumName)
        {
            if (_parentPage is not null)
            {
                _parentPage.SelectBarAlbum(albumName);
            }
        }

        public void MusicDetail_Click()
        {
            var musicDetailsWindow = new MusicDetailsWindow(SelectedMusic);
            musicDetailsWindow.Activate();
        }

        public void ShowTransmission()
        {
            if (_parentPage is not null)
            {
                _parentPage.ShowTransmission();
            }
        }

        public void HideTransmission()
        {
            if (_parentPage is not null)
            {
                _parentPage.HideTransmission();
            }
        }

        [RelayCommand]
        private void OnPlayAll()
        {
            if (_parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new([.. AppObservableObj.ArtistSongsView.Cast<Music>()]);
                if (AppObservableObj.SequentialPlayingList.Count > 0)
                {
                    _parentPage.PlayMusic(music: AppObservableObj.SequentialPlayingList[0], IsChangeList: true);
                }
            }
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
            AppObservableObj.AddMusicToCurrentPlayList(music);
        }
    }
}
