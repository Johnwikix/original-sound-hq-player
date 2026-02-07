using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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
        private ObservableCollection<Music> _musicList = [];
        public ObservableCollection<Music> MusicList
        {
            get => _musicList;
            set => SetProperty(ref _musicList, value);
        }

        private Music _selectedMusic;
        public Music SelectedMusic
        {
            get => _selectedMusic;
            set => SetProperty(ref _selectedMusic, value);
        }

        private ObservableCollection<Music> _selectedMusicItems;
        public ObservableCollection<Music> SelectedMusicItems
        {
            get => _selectedMusicItems ??= new ObservableCollection<Music>();
            set => SetProperty(ref _selectedMusicItems, value);
        }
        private MusicBrowsePage parentPage { get; }
        public AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private FavouritePlayListPage currentPage { get; set; }
        public FavouritePlayListViewModel(MusicBrowsePage musicBrowsePage,AppObservableObj appObservableObj, MusicDatabaseService musicDatabaseService)
        {
            parentPage = musicBrowsePage;
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
            parentPage.refreshPage += RefreshMusicList;
        }

        public void SetCurrentPage(FavouritePlayListPage page)
        {
            currentPage = page;
            //InitializeData();
        }

        public void ReceiveNavigation()
        {
            InitializeData();
            RefreshUsbDeviceMusicList();
        }

        private void RefreshMusicList(object? sender, bool e)
        {
            if (e) InitializeData();
        }

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

        public void InitializeData()
        {
            var musicList = _musicDatabaseService.GetFavoriteMusicFromMem(AppData.searchText);
            LoadMusicAsync(musicList);
        }

        public void LoadMusicAsync(IEnumerable<Music> musics)
        {
            try
            {
                MusicList.Clear();
                foreach (var music in musics)
                {
                    MusicList.Add(music);
                }
                SortMusicList(AppData.sortOrder);
                UpdateMusicListView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (parentPage is not null)
                {
                    if (AppObservableObj.CurrentPlayingMusic is not null)
                    {
                        var selectedMusic = MusicList.AsValueEnumerable().FirstOrDefault(music =>
                        music.Id == AppObservableObj.CurrentPlayingMusic.Id);

                        if (selectedMusic is not null)
                        {
                            SelectedMusic = selectedMusic;
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

        public void UpdateFavouriteMusic(Music music)
        {
            if (MusicList is not null && MusicList.Count > 0)
            {
                Music? currentMusic = MusicList.AsValueEnumerable().FirstOrDefault(m => m.Id == music.Id);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (currentMusic is not null && !music.IsFavorite)
                    {
                        MusicList.Remove(currentMusic);
                    }
                    if (currentMusic is null && music.IsFavorite)
                    {
                        MusicList.Insert(0, music);
                    }
                });

            }
        }

        public async Task DragItems()
        {
            if (AppData.sortOrder == "DefaultOrder")
            {
                for (int i = 0; i < MusicList.Count; i++)
                {
                    MusicList[i].Order = MusicList.Count - i;
                }
                await _musicDatabaseService.UpdateAllAsync([.. MusicList]);
                AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
            }
        }

        public void ClearUsbDeviceMusicList()
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var music in MusicList)
                {
                    music.IsExistOnDevice = 0;
                }
            });
        }

        public void RefreshUsbDeviceMusicList()
        {
            ToolUtils.RefreshUsbDeviceMusicList(MusicList);
        }

        public void SortMusicList(string sortOrder)
        {
            var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;
            if (MusicList.Count > 0)
            {
                ToolUtils.SortMusicListInPlace("favour", order, MusicList);
            }
        }

        public void AddMusicToTop(Music newMusic)
        {
            int maxOrder = MusicList.AsValueEnumerable().Any() ? MusicList.AsValueEnumerable().Max(m => m.Order) : 0;
            newMusic.Order = maxOrder + 1;
            MusicList.Insert(0, newMusic);
        }

        public void RemoveMusic(Music musicToRemove)
        {
            var music = MusicList.AsValueEnumerable().FirstOrDefault(m => m.Id == musicToRemove.Id);
            if (music is not null)
            {
                MusicList.Remove(music);
            }
        }

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
                AppObservableObj.SequentialPlayingList = new(MusicList);
                parentPage.PlayMusic(music: selectedMusic, IsChangeList: true);
            }
        }

        public void MusicDetail_Click()
        {
            var music = AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == SelectedMusic.Id);
            if (music is not null)
            {
                var musicDetailsWindow = new MusicDetailsWindow(music);
                musicDetailsWindow.MusicDetailChanged += MusicDetailsWindow_MusicDetailChanged;
                musicDetailsWindow.Activate();
            }
        }

        private void MusicDetailsWindow_MusicDetailChanged(object? sender, Music musicItem)
        {
            foreach (var music in MusicList)
            {
                if (music.Path == musicItem.Path)
                {
                    music.Title = musicItem.Title;
                    music.Author = musicItem.Author;
                    music.Album = musicItem.Album;
                    music.Year = musicItem.Year;
                    music.DiskNumber = musicItem.DiskNumber;
                    music.TrackNumber = musicItem.TrackNumber;
                    music.Lyrics = musicItem.Lyrics;
                    break;
                }
            }
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
                AppObservableObj.SequentialPlayingList = new(MusicList);
                parentPage.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public async void ReGetLyrics_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(item);
                    Music? music = AppData.allSongs.AsValueEnumerable().Where(m => m.Id == item.Id).FirstOrDefault();
                    if (music is not null)
                    {
                        music.Lyrics = lyrics;
                        music.TranslatdeLyrics = transLrc;
                        await _musicDatabaseService.UpdateMusicInfo(music);
                    }
                }
            }
            else
            {
                (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(SelectedMusic);
                Music? music = AppData.allSongs.AsValueEnumerable().Where(m => m.Id == SelectedMusic.Id).FirstOrDefault();
                if (music is not null)
                {
                    music.Lyrics = lyrics;
                    music.TranslatdeLyrics = transLrc;
                    await _musicDatabaseService.UpdateMusicInfo(music);
                }
            }
            AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
        }

        public async void DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                for (int i = MusicList.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(MusicList[i]))
                    {
                        if (ToolUtils.DeleteFileFromDisk((MusicList[i].Path)))
                        {
                            await _musicDatabaseService.RemoveMusic(MusicList[i].Id);
                            MusicList.RemoveAt(i);
                        }
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    await _musicDatabaseService.RemoveMusic(SelectedMusic.Id);
                    MusicList.Remove(SelectedMusic);
                }
            }
        }

        public async void SetAsFavoriteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null)
            {
                await _musicDatabaseService.CancelMusicsFavourite(uniqueSelectedMusics.AsValueEnumerable().ToList());
                for (int i = MusicList.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(MusicList[i]))
                    {
                        MusicList.RemoveAt(i);
                        if (AppObservableObj.CurrentPlayingMusic is not null)
                        {
                            if (AppObservableObj.CurrentPlayingMusic.Id == MusicList[i].Id)
                            {
                                AppObservableObj.CurrentPlayingMusic.IsFavorite = MusicList[i].IsFavorite;
                            }
                        }
                    }
                }

                AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
            }
        }

        public void OpenInExplorer_Click()
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

        public async Task ConvertAudio_Click(IEnumerable<Music> uniqueSelectedMusics, MenuFlyoutItem? menuItem)
        {
            parentPage?.ViewModel?.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
        }

        [RelayCommand]
        private void IsFavouriteIconButtonChange(Music music)
        {
            if (music is not null)
            {
                if (music.IsFavorite)
                {
                    MusicList.Remove(music);
                    _ = CancelFavouriteIconButtonChange(music);
                }
            }
        }

        private async Task CancelFavouriteIconButtonChange(Music music)
        {
            await AppObservableObj.AddToFavourite(music);
            AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
        }

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

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
            AppObservableObj.AddMusicToCurrentPlayList(music);
        }

        [RelayCommand]
        private void PlayMusic(Music music)
        {
            if (music is not null && parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new(MusicList);
                parentPage.PlayMusic(music: music, IsChangeList: true);
            }
        }
    }
}
