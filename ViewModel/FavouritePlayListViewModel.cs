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
using TagLib.Ape;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class FavouritePlayListViewModel : ObservableObject
    {      
        //private ObservableCollection<Music> _musicList = [];
        //public ObservableCollection<Music> MusicList
        //{
        //    get => _musicList;
        //    set => SetProperty(ref _musicList, value);
        //}

        private MusicBrowsePage parentPage { get; }
        public AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private FavouritePlayListPage currentPage { get; set; }
        public FavouritePlayListViewModel(MusicBrowsePage musicBrowsePage,AppObservableObj appObservableObj, MusicDatabaseService musicDatabaseService)
        {
            parentPage = musicBrowsePage;
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
            //parentPage.refreshPage += RefreshMusicList;
        }

        public void SetCurrentPage(FavouritePlayListPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            //InitializeData();
            //RefreshUsbDeviceMusicList();
            UpdateMusicListView();
        }

        //private void RefreshMusicList(object? sender, bool e)
        //{
        //    if (e) InitializeData();
        //}

        public void ShowTransmission()
        {
            if (parentPage is not null)
            {
                parentPage.ShowTransmission();
            }
        }

        public void HideTransmission()
        {
            if (parentPage is not null)
            {
                parentPage.HideTransmission();
            }
        }

        //public void InitializeData()
        //{
        //    //var musicList = _musicDatabaseService.GetFavoriteMusicFromMem(AppData.searchText);
        //    //LoadMusicAsync(musicList);
        //}

        //public void LoadMusicAsync(IEnumerable<Music> musics)
        //{
        //    //try
        //    //{
        //    //    MusicList.Clear();
        //    //    foreach (var music in musics)
        //    //    {
        //    //        MusicList.Add(music);
        //    //    }
        //    //    SortMusicList(AppData.sortOrder);
        //    //    UpdateMusicListView();
        //    //}
        //    //catch (Exception ex)
        //    //{
        //    //    Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
        //    //}
        //}

        public void UpdateMusicListView()
        {
            try
            {
                if (parentPage is not null)
                {
                    if (AppObservableObj.CurrentPlayingMusic is not null)
                    {
                        var selectedMusic = AppObservableObj.AllSongs.AsValueEnumerable().FirstOrDefault(music =>
                        music.Id == AppObservableObj.CurrentPlayingMusic.Id);

                        if (selectedMusic is not null)
                        {
                            AppObservableObj.FavoriteSongsSelectedMusic = selectedMusic;
                            currentPage.OnScrollToMusic(selectedMusic);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            }
        }

        //public void UpdateFavouriteMusic(Music music)
        //{
        //    //if (MusicList is not null && MusicList.Count > 0)
        //    //{
        //    //    Music? currentMusic = MusicList.AsValueEnumerable().FirstOrDefault(m => m.Id == music.Id);
        //    //    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //    //    {
        //    //        if (currentMusic is not null && !music.IsFavorite)
        //    //        {
        //    //            MusicList.Remove(currentMusic);
        //    //        }
        //    //        if (currentMusic is null && music.IsFavorite)
        //    //        {
        //    //            MusicList.Insert(0, music);
        //    //        }
        //    //    });

        //    //}
        //}

        public async Task DragItems()
        {
            if (AppObservableObj.SelectedSortOption.Tag == "DefaultOrder")
            {
                for (int i = 0; i < AppObservableObj.FavoriteSongs.Count; i++)
                {
                    AppObservableObj.FavoriteSongs[i].Order = AppObservableObj.FavoriteSongs.Count - i;
                }
                await _musicDatabaseService.UpdateAllAsync([.. AppObservableObj.FavoriteSongs]);
            }
        }

        //public void ClearUsbDeviceMusicList()
        //{
        //    //App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //    //{
        //    //    foreach (var music in MusicList)
        //    //    {
        //    //        music.IsExistOnDevice = 0;
        //    //    }
        //    //});
        //}

        //public void RefreshUsbDeviceMusicList()
        //{
        //    ToolUtils.RefreshUsbDeviceMusicList(MusicList);
        //}

        //public void SortMusicList(string sortOrder)
        //{
        //    var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;
        //    //if (MusicList.Count > 0)
        //    //{
        //    //    ToolUtils.SortMusicListInPlace("favour", order, MusicList);
        //    //}
        //}

        //public void AddMusicToTop(Music newMusic)
        //{
        //    //int maxOrder = MusicList.AsValueEnumerable().Any() ? MusicList.AsValueEnumerable().Max(m => m.Order) : 0;
        //    //newMusic.Order = maxOrder + 1;
        //    //MusicList.Insert(0, newMusic);
        //}

        //public void RemoveMusic(Music musicToRemove)
        //{
        //    //var music = MusicList.AsValueEnumerable().FirstOrDefault(m => m.Id == musicToRemove.Id);
        //    //if (music is not null)
        //    //{
        //    //    MusicList.Remove(music);
        //    //}
        //}

        public async Task<bool> IsDeleteFromDisk()
        {
            if (parentPage is null)
            {
                return false;
            }
            return await parentPage.AreUSureDeleteFromDisk();
        }

        public void MusicListView_DoubleTapped(Music selectedMusic)
        {
            if (selectedMusic is not null && parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new(AppObservableObj.FavoriteSongs);
                parentPage.PlayMusic(music: selectedMusic, IsChangeList: true);
            }
        }

        public void MusicDetail_Click()
        {
            var musicDetailsWindow = new MusicDetailsWindow(AppObservableObj.FavoriteSongsSelectedMusic);
            musicDetailsWindow.Activate();
        }

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                AppObservableObj.SequentialPlayingList = new(uniqueSelectedMusics);
                parentPage.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First(), IsChangeList: true);
            }
            else
            {
                AppObservableObj.SequentialPlayingList = new(AppObservableObj.FavoriteSongs);
                parentPage.PlayMusic(music: AppObservableObj.FavoriteSongsSelectedMusic, IsChangeList: true);
            }
        }

        public async void ReGetLyrics_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(item);
                    Music? music = AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == item.Id);
                    if (music is not null)
                    {
                        music?.Lyrics = lyrics;
                        music?.TranslatdeLyrics = transLrc;
                        await _musicDatabaseService.UpdateMusicInfo(music);
                    }
                }
            }
            else
            {
                (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(AppObservableObj.FavoriteSongsSelectedMusic);
                Music? music = AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == AppObservableObj.FavoriteSongsSelectedMusic.Id);
                if (music is not null)
                {
                    music.Lyrics = lyrics;
                    music.TranslatdeLyrics = transLrc;
                    await _musicDatabaseService.UpdateMusicInfo(music);
                }
            }
        }

        public async void DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                for (int i = AppObservableObj.AllSongs.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(AppObservableObj.AllSongs[i]))
                    {
                        if (ToolUtils.DeleteFileFromDisk((AppObservableObj.AllSongs[i].Path)))
                        {
                            AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == AppObservableObj.FavoriteSongsSelectedMusic.Id)?.Remove();
                        }
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(AppObservableObj.FavoriteSongsSelectedMusic.Path))
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == AppObservableObj.FavoriteSongsSelectedMusic.Id)?.Remove();
                }
            }
        }

        public async void SetAsFavoriteMenuItem_Click(List<Music> uniqueSelectedMusics)
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
                if (AppObservableObj.FavoriteSongsSelectedMusic is not null)
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == AppObservableObj.FavoriteSongsSelectedMusic.Id)?.UpdateFavourite();
                }
            }
        }

        public void OpenInExplorer_Click()
        {
            var filePath = AppObservableObj.FavoriteSongsSelectedMusic.Path;
            if (System.IO.File.Exists(filePath))
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

        public async Task ConvertAudio_Click(IEnumerable<Music> uniqueSelectedMusics, MenuFlyoutItem? menuItem)
        {
            parentPage?.ViewModel?.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
        }

        //[RelayCommand]
        //private void IsFavouriteIconButtonChange(Music music)
        //{
        //    if (music is not null)
        //    {
        //        if (music.IsFavorite)
        //        {
        //            //MusicList.Remove(music);
        //            _ = CancelFavouriteIconButtonChange(music);
        //        }
        //    }
        //}

        //private async Task CancelFavouriteIconButtonChange(Music music)
        //{
        //    await AppObservableObj.AddToFavourite(music);
        //    AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
        //}

        public void AlbumTextBlock_Tapped(TextBlock textBlock)
        {
            string albumName = textBlock.Text;
            // 假设 AlbumDetailsPage 是目标页面，将专辑名作为参数传递
            if (parentPage is not null)
            {
                parentPage.SelectBarAlbum(albumName);
            }
        }

        public void AuthorTextBlock_Tapped(TextBlock textBlock)
        {
            string artist = textBlock.Text;
            if (parentPage is not null)
            {
                parentPage.SelectBarArtist(artist);
            }
        }        

        //[RelayCommand]
        //private void PlayMusic(Music music)
        //{
        //    if (music is not null && parentPage is not null)
        //    {
        //        AppObservableObj.SequentialPlayingList = new(AppObservableObj.FavoriteSongs);
        //        parentPage.PlayMusic(music: music, IsChangeList: true);
        //    }
        //}
    }
}
