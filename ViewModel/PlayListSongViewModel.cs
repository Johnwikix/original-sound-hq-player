using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.View;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using WinUIMusicPlayer.Helper;
using Microsoft.UI.Xaml.Controls;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class PlayListSongViewModel : ObservableObject
    {
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

        private string _playListName;
        public string PlayListName
        {
            get => _playListName;
            set => SetProperty(ref _playListName, value);
        }

        private MusicBrowsePage? _parentPage;
        private PlayListSongPage _currentPage;
        private string _lastSearchText = "";
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        private int _currentPlayListId;
        private MusicPlaybackService _musicPlaybackService;

        public PlayListSongViewModel(MusicBrowsePage parent,MusicPlaybackService musicPlaybackService, AudioConverterService converterService)
        {
            _parentPage = parent;          
            _parentPage.refreshPage += RefreshPlayList;
            _parentPage.clearUsbDeviceMusicList += ClearUsbDeviceMusicList;
            _parentPage.refreshUsbDeviceMusicList += RefreshUsbDeviceMusicList;
            _musicPlaybackService = musicPlaybackService;
            _converterService = converterService;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");
            _converterService.updateProgress += OnConverterProgressUpdated;
        }

        public void SetCurrentPage(PlayListSongPage page)
        {
            _currentPage = page;
        }

        public void ReceiveNavigation()
        {
            _parentPage.DisableBackButton();
            PlayListName = _parentPage.currentPlayList.Name;
            InitizeData();
            ClearUsbDeviceMusicList(null, null);
            RefreshUsbDeviceMusicList(null, null);            
        }

        public void ShowTransmission()
        {
            if (_parentPage != null)
            {
                _parentPage.ShowTransmission();
            }
        }

        public void HideTransmission()
        {
            if (_parentPage != null)
            {
                _parentPage.HideTransmission();
            }
        }

        private void OnConverterProgressUpdated(object sender, double progress)
        {
            if (_progressDialog != null && progress < 100)
            {
                _ = _progressDialog.UpdateProgress((int)progress);
            }
        }

        public void ClearUsbDeviceMusicList(object? sender, EventArgs e)
        {
            foreach (var music in MusicList)
            {
                music.IsExistOnDevice = 0;
            }
        }

        public void RefreshUsbDeviceMusicList(object? sender, EventArgs e)
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

        private void RefreshPlayList(object? sender, EventArgs e)
        {
            InitizeData();
        }

        private void InitizeData()
        {
            if (_parentPage != null)
            {
                var musicList = MusicDatabaseService.GetMusicByPlayListIdFromMem(_parentPage.currentPlayListId, AppData.searchText);
                LoadMusicAsync(musicList);
            }
        }

        public async void MusicListView_DragItemsCompleted()
        {
            if (_parentPage != null)
            {
                for (int i = 0; i < MusicList.Count; i++)
                {
                    MusicList[i].PlayListOrder = MusicList.Count - i;
                    await MusicDatabaseService.UpdatePlayListMusicOrder(_parentPage.currentPlayList.Id, MusicList[i]);
                }
                await MusicDatabaseService.GetPlayListMusic();
            }
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

        public void SortMusicList(string sortOrder)
        {
            var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;
            //List<Music> musics = new List<Music>();

            if (MusicList.Count > 0)
            {
                MusicList = new ObservableCollection<Music>(ToolUtils.SortMusicList("playList", order, MusicList));
            }

            //MusicList.Clear();
            //foreach (var music in musics)
            //{
            //    MusicList.Add(music);
            //}
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic != null)
                {
                    var selectedMusic = MusicList.FirstOrDefault(music =>
                        music.Id == _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic.Id);

                    if (selectedMusic != null)
                    {
                        SelectedMusic = selectedMusic;
                        _currentPage.OnScrollToMusic(selectedMusic);
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
                    if (currentMusic != null)
                    {
                        currentMusic.IsFavorite = music.IsFavorite;
                    }
                });                
            }
        }

        public async Task MusicListView_DoubleTapped()
        {
            if (SelectedMusic != null && _parentPage != null)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList =
                                        new ObservableCollection<Music>(MusicList);
                await _parentPage.PlayMusic(SelectedMusic);
            }
        }

        public async Task PlayMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                await _parentPage.PlayMusic(uniqueSelectedMusics[0]);
            }
            else
            {
                if (SelectedMusic != null)
                {
                    _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList =
                                new ObservableCollection<Music>(MusicList);
                    await _parentPage.PlayMusic(SelectedMusic);
                }
            }
        }

        public async Task DeleteMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    await MusicDatabaseService.RemoveMusicFromPlayList(_currentPlayListId, item.Id);
                    MusicList.Remove(item);
                }
            }
            else
            {
                if (SelectedMusic != null)
                {
                    await MusicDatabaseService.RemoveMusicFromPlayList(_currentPlayListId, SelectedMusic.Id);
                    MusicList.Remove(SelectedMusic);
                }
            }
        }

        public async Task SetAsFavoriteMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    await _parentPage.AddToFavourite(item);
                }
            }
            else
            {                
                if (SelectedMusic != null)
                {
                    await _parentPage.AddToFavourite(SelectedMusic);
                }
            }

            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
        }

        public void OpenInExplorer_Click()
        {
            if (SelectedMusic != null)
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

        public async Task ConvertAudio_Click(List<Music> uniqueSelectedMusics, MenuFlyoutItem? menuItem)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    int progressBarValue = 0;
                    _progressDialog.RequestedTheme = AppSettings.elementTheme;
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
                    _progressDialog.XamlRoot = _currentPage.XamlRoot;
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
                    int progressBarValue = 0;
                    _progressDialog.RequestedTheme = AppSettings.elementTheme;
                    _ = _progressDialog.UpdateProgress(progressBarValue);
                    _ = _converterService.ConvertAudio2Wav(SelectedMusic, menuItem.Tag.ToString());
                    _converterService.updateProgress += (sender, progress) =>
                    {
                        progressBarValue = (int)progress;
                        _ = _progressDialog.UpdateProgress(progressBarValue);
                    };
                    if (progressBarValue < 100)
                    {
                        _progressDialog.XamlRoot = _currentPage.XamlRoot;
                        _ = _progressDialog.ShowAsync();
                    }
                }
            }
        }

        public void AlbumTextBlock_Tapped(string albumName)
        {
            if (_parentPage != null)
            {
                _parentPage.SelectBarAlbum(albumName);
            }
        }

        public void AuthorTextBlock_Tapped(string artist)
        {
            if (_parentPage != null)
            {
                _parentPage.SelectBarArtist(artist);
            }
        }

        public void MusicDetail_Click()
        {
            if (SelectedMusic != null)
            {
                var musicDetailsWindow = new MusicDetailsWindow(SelectedMusic);
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
                    music.TrackNumber = musicItem.TrackNumber;
                    music.Lyrics = musicItem.Lyrics;
                    break;
                }
            }
        }

        public async Task IsFavouriteIconButton_Click(Music music)
        {
            if (music != null)
            {
                // 通过事件通知视图更新图标
                await _parentPage.AddToFavourite(music);
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            }
        }
    }
}
