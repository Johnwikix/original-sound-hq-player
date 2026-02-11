using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class SongCollectionViewModel : ObservableObject
    {
        public Music SelectedMusic { get; set => SetProperty(ref field, value); }
        public string SecondTitle { get; set => SetProperty(ref field, value); }
        public string ThirdTitle { get; set => SetProperty(ref field, value); }
        private MusicBrowsePage? _parentPage { get; }
        private MusicBrowseViewModel? _musicBrowseViewModel{ get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private SongCollectionPage _currentPage{ get; set; }        
       
        public SongCollectionViewModel(MusicBrowsePage parent, MusicBrowseViewModel musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            _parentPage = parent;
            //_parentPage.refreshSong += RefreshSong;
            _musicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
        }
        public void SetCurrentPage(SongCollectionPage page)
        {
            _currentPage = page;
        }
        public void ReceiveNavigation()
        {
            //_parentPage?.DisableBackButton();
            //RefreshUsbDeviceMusicList(null, null);
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

        //public void RefreshUsbDeviceMusicList(object? sender, EventArgs e)
        //{
        //    //ToolUtils.RefreshUsbDeviceMusicList(MusicList);
        //}

        //private void RefreshSong(object? sender, EventArgs e)
        //{
        //    RefreshPage();
        //}

        public void RefreshPage()
        {
            if (_parentPage is not null && AppViewModel.CurrentAlbumObj is not null)
            {
                SecondTitle = string.Join(" · ", AppViewModel.AllSongs.AsValueEnumerable()
                    .Where(music => music.Album == AppViewModel.CurrentAlbumObj.Album)
                    .Select(music => music.Author)
                    .Distinct().ToArray());
                ThirdTitle = $"{(AppViewModel.CurrentAlbumObj.Year != 0 ? $"{AppViewModel.CurrentAlbumObj.Year} · ".ToString() : "")}{AppViewModel.AllSongs.AsValueEnumerable().Count(music => music.Album == AppViewModel.CurrentAlbumObj.Album)} {ToolUtils.GetString("NumberOfSongs")}";             
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

        //[RelayCommand]
        //private void PlayMusic(Music music)
        //{
        //    if (music is not null && _parentPage is not null)
        //    {
        //        AppViewModel.SequentialPlayingList = new(MusicList);
        //        _parentPage.PlayMusic(music: music, IsChangeList: true);
        //    }
        //}

        //public void SortMusicList(string sortOrder, string type)
        //{
        //    var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;

        //    if (MusicList.Count > 0)
        //    {
        //        ToolUtils.SortMusicListInPlace(type, order, MusicList);
        //    }
        //}

        //public void UpdateFavouriteMusic(Music music)
        //{
        //    if (MusicList is not null && MusicList.Count > 0)
        //    {
        //        Music? currentMusic = MusicList.AsValueEnumerable().FirstOrDefault(m => m.Id == music.Id);
        //        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //        {
        //            if (currentMusic is not null)
        //            {
        //                currentMusic.IsFavorite = music.IsFavorite;
        //            }
        //        });
        //    }
        //}

        public void UpdateMusicListView()
        {
            try
            {
                if (AppViewModel.CurrentPlayingList is not null)
                {
                    var selectedMusic = AppViewModel.AllSongs.AsValueEnumerable().FirstOrDefault(music =>
                        music.Id == AppViewModel.CurrentPlayingMusic.Id);

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
                AppViewModel.SequentialPlayingList = new([.. AppViewModel.AlbumSongsView.Cast<Music>()]);
                _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                AppViewModel.SequentialPlayingList = new(uniqueSelectedMusics);
                _parentPage?.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First(), IsChangeList: true);
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppViewModel.SequentialPlayingList = new([.. AppViewModel.AlbumSongsView.Cast<Music>()]);
                    _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
                }
            }
        }

        public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                for (int i = AppViewModel.AllSongs.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(AppViewModel.AllSongs[i]))
                    {
                        if (ToolUtils.DeleteFileFromDisk((AppViewModel.AllSongs[i].Path)))
                        {
                            AppViewModel.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                        }
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                }
            }
        }
        public async Task SetAsFavoriteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == item.Id)?.UpdateFavourite();
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.UpdateFavourite();
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

        public async void ReGetLyrics_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            AppViewModel.ReGetLyrics(uniqueSelectedMusics, SelectedMusic);
            //if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            //{
            //    foreach (Music item in uniqueSelectedMusics)
            //    {
            //        (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(item);
            //        Music? music = AppViewModel.AllSongs.AsValueEnumerable().Where(m => m.Id == item.Id).FirstOrDefault();
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
            //    Music? music = AppViewModel.AllSongs.AsValueEnumerable().Where(m => m.Id == SelectedMusic.Id).FirstOrDefault();
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
                AppViewModel.SequentialPlayingList = new([.. AppViewModel.AlbumSongsView.Cast<Music>()]);
                if (AppViewModel.SequentialPlayingList.Count > 0)
                {
                    _parentPage.PlayMusic(music: AppViewModel.SequentialPlayingList[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        private void OnAlbumInfoChanged()
        {
            if (AppViewModel.CurrentAlbumObj is not null)
            {
                var albumDetailWindow = new AlbumDetailWindow(AppViewModel.CurrentAlbumObj);
                albumDetailWindow.Activate();
            }
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
                AppViewModel.AddMusicToCurrentPlayList(music);
        }

    }
}
