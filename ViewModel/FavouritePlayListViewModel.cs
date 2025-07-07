using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class FavouritePlayListViewModel : ObservableObject
    {
        //public EventHandler MusicListViewUpdated;        
        private ObservableCollection<Music> _musicList = new ObservableCollection<Music>();
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
        private MusicBrowsePage parentPage;
        private FavouritePlayListPage currentPage;
        private MusicPlaybackService _musicPlaybackService;
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        public FavouritePlayListViewModel(MusicPlaybackService musicPlaybackService, AudioConverterService converterService,MusicBrowsePage musicBrowsePage)
        {
            parentPage = musicBrowsePage;
            parentPage.refreshPage += RefreshMusicList;
            parentPage.refreshUsbDeviceMusicList += refreshUsbDeviceMusicList;
            parentPage.clearUsbDeviceMusicList += clearUsbDeviceMusicList;
            _musicPlaybackService = musicPlaybackService;
            _converterService = converterService;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");
            InitializeData();
        }
        public void SetCurrentPage(FavouritePlayListPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            //parentPage = parent;            
            InitializeData();
            ClearUsbDeviceMusicList();
            RefreshUsbDeviceMusicList();
        }

        private void clearUsbDeviceMusicList(object? sender, EventArgs e)
        {
            ClearUsbDeviceMusicList();
        }

        private void refreshUsbDeviceMusicList(object? sender, EventArgs e)
        {
            RefreshUsbDeviceMusicList();
        }

        private void RefreshMusicList(object? sender, EventArgs e)
        {
            InitializeData();
        }

        public void ShowTransmission()
        {
            if (parentPage != null)
            {
                parentPage.ShowTransmission();
            }
        }

        public void HideTransmission()
        {
            if (parentPage != null)
            {
                parentPage.HideTransmission();
            }
        }

        public void InitializeData()
        {
            var musicList = MusicDatabaseService.GetFavoriteMusicFromMem(AppData.searchText);
            LoadMusicAsync(musicList);
        }

        public void LoadMusicAsync(List<Music> musics)
        {
            try
            {
                MusicList = new ObservableCollection<Music>(musics);
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
                if (parentPage != null)
                {
                    if (_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic != null)
                    {
                        var selectedMusic = MusicList.FirstOrDefault(music =>
                        music.Id == _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic.Id);

                        if (selectedMusic != null)
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
            if (MusicList != null && MusicList.Count > 0)
            {
                Music? currentMusic = MusicList.FirstOrDefault(m => m.Id == music.Id);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (currentMusic != null && !music.IsFavorite)
                    {
                        MusicList.Remove(currentMusic);
                    }
                    if (currentMusic == null && music.IsFavorite)
                    {
                        MusicList.Insert(0, music);
                    }
                });
                
            }
        }

        public async Task DragItems()
        {
            for (int i = 0; i < MusicList.Count; i++)
            {
                MusicList[i].Order = MusicList.Count - i;
                await MusicDatabaseService.UpdateMuisc(MusicList[i]);
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
        }

        public void ClearUsbDeviceMusicList()
        {
            foreach (var music in MusicList)
            {
                music.IsExistOnDevice = 0;
            }
        }

        public void RefreshUsbDeviceMusicList()
        {
            var usbMusicGroups = AppData.musicOnUsbDevice
                            .GroupBy(u => u.Title)
                            .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var music in MusicList)
            {
                music.IsExistOnDevice = 0;

                if (usbMusicGroups.TryGetValue(music.Title, out var matchingItems))
                {
                    music.IsExistOnDevice = 1;
                    foreach (var usbMusic in matchingItems)
                    {
                        if (music.Author == usbMusic.Author &&
                            music.Album == usbMusic.Album &&
                            music.Extension == usbMusic.Extension)
                        {
                            music.IsExistOnDevice = 2;
                            break;
                        }
                    }
                }
            }
        }

        public void SortMusicList(string sortOrder)
        {
            var order = "DefaultOrder";
            //List<Music> musics = new List<Music>();
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            MusicList = new ObservableCollection<Music>(ToolUtils.SortMusicList("favour", order, MusicList));
            //if (MusicList.Count > 0)
            //{
            //    ToolUtils.SortMusicList("favour", order, MusicList);
            //}
            //MusicList.Clear();
            //foreach (Music music in musics)
            //{
            //    MusicList.Add(music);
            //}
        }

        public void AddMusicToTop(Music newMusic)
        {
            int maxOrder = MusicList.Any() ? MusicList.Max(m => m.Order) : 0;
            newMusic.Order = maxOrder + 1;
            MusicList.Insert(0, newMusic);
        }

        public void RemoveMusic(Music musicToRemove)
        {
            var music = MusicList.FirstOrDefault(m => m.Id == musicToRemove.Id);
            if (music != null)
            {
                MusicList.Remove(music);
            }
        }

        public async void MusicListView_DoubleTapped(Music selectedMusic)
        {
            if (selectedMusic != null && parentPage != null)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = MusicList;
                await parentPage.PlayMusic(selectedMusic);
            }
        }

        public void MusicDetail_Click()
        {
            var musicDetailsWindow = new MusicDetailsWindow(SelectedMusic);
            musicDetailsWindow.MusicDetailChanged += MusicDetailsWindow_MusicDetailChanged;
            musicDetailsWindow.Activate();
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
                    music.TrackNumber = musicItem.TrackNumber;
                    music.Lyrics = musicItem.Lyrics;
                    break;
                }
            }
        }
        public async void PlayMenuItem_Click(List<Music> uniqueSelectedMusics)
        {            
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                await parentPage.PlayMusic(uniqueSelectedMusics[0]);
            }
            else
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = MusicList;
                await parentPage.PlayMusic(SelectedMusic);                
            }
        }

        public async void DeleteMenuItem_Click(List<Music> uniqueSelectedMusics) 
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {                    
                    await MusicDatabaseService.RemoveMusic(item.Id);
                    ToolUtils.DeleteFileFromDisk(item.Path);
                    MusicList.Remove(item);
                }
            }
            else
            {
                await MusicDatabaseService.RemoveMusic(SelectedMusic.Id);
                ToolUtils.DeleteFileFromDisk(SelectedMusic.Path);
                MusicList.Remove(SelectedMusic);
            }
        }

        public async void SetAsFavoriteMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    if (item.IsFavorite)
                    {
                        MusicList.Remove(item);
                    }
                    await parentPage.AddToFavourite(item);
                    AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
                }
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

        public async void ConvertAudio_Click(MenuFlyoutItem? menuItem, List<Music> uniqueSelectedMusics)
        {
            _progressDialog.RequestedTheme = AppSettings.elementTheme;
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    int progressBarValue = 0;                    
                    await _progressDialog.UpdateProgress(progressBarValue);
                    _converterService.updateProgress += (sender, progress) =>
                    {
                        if (progressBarValue < (int)progress)
                        {
                            progressBarValue = (int)progress;
                        }
                        if (progressBarValue < 100)
                        {
                            _ = _progressDialog.UpdateProgress(progressBarValue);
                        }
                    };
                    _progressDialog.XamlRoot = currentPage.XamlRoot;
                    _ = _progressDialog.ShowAsync();

                    List<Task> conversionTasks = new List<Task>();
                    foreach (Music item in uniqueSelectedMusics)
                    {
                        Task conversionTask = _converterService.ConvertAudio2Wav(item, menuItem.Tag.ToString());
                        conversionTasks.Add(conversionTask);
                    }
                    await Task.WhenAll(conversionTasks);
                    _ = _progressDialog.UpdateProgress(100);
                }
            }
            else
            {
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    if (SelectedMusic != null)
                    {
                        if (SelectedMusic.Extension.ToLower() == menuItem?.Tag?.ToString()?.ToLower())
                        {
                            parentPage?.ViewModel.UpdateInfoBar(ToolUtils.GetString("InfoBarMessageConverter"));
                            return;
                        }
                        int progressBarValue = 0;
                        _ = _progressDialog.UpdateProgress(progressBarValue);
                        _ = _converterService.ConvertAudio2Wav(SelectedMusic, menuItem.Tag.ToString());
                        _converterService.updateProgress += (sender, progress) =>
                        {
                            progressBarValue = (int)progress;
                            _ = _progressDialog.UpdateProgress(progressBarValue);
                        };
                        if (progressBarValue < 100)
                        {
                            _progressDialog.XamlRoot = currentPage.XamlRoot;
                            _ = _progressDialog.ShowAsync();
                        }
                    }                    
                }
            }
        }

        public async void IsFavouriteIconButton_Click(Music music)
        {
            if (music != null)
            {
                if (music.IsFavorite)
                {
                    MusicList.Remove(music);
                }
                await parentPage.AddToFavourite(music);
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            }
        }

        public void AlbumTextBlock_Tapped(TextBlock textBlock)
        {
            string albumName = textBlock.Text;
            // 假设 AlbumDetailsPage 是目标页面，将专辑名作为参数传递
            if (parentPage != null)
            {
                parentPage.SelectBarAlbum(albumName);
            }
        }

        public void AuthorTextBlock_Tapped(TextBlock textBlock)
        {
            string artist = textBlock.Text;
            if (parentPage != null)
            {
                parentPage.SelectBarArtist(artist);
            }
        }
    }
}
