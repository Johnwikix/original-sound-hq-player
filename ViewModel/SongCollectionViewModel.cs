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
using CommunityToolkit.Mvvm.Messaging;
using WinUIMusicPlayer.Helper;
using System.IO;
using Microsoft.UI.Xaml.Controls;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class SongCollectionViewModel : ObservableObject
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

        private MusicBrowsePage? _parentPage;
        private SongCollectionPage _currentPage;
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        private string? _currentPageType;
        private string? _currentAlbumName;
        private string? _currentArtistName;
        private string? _currentFolderName;
        private readonly IMessenger _messenger;
        MusicPlaybackService _musicPlaybackService;

        public SongCollectionViewModel(MusicPlaybackService musicPlaybackService, AudioConverterService converterService, IMessenger messenger)
        {
            _converterService = converterService;
            _musicPlaybackService = musicPlaybackService;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");
            _converterService.updateProgress += OnConverterProgressUpdated;
            _messenger = messenger;
        }
        public void SetCurrentPage(SongCollectionPage page)
        {
            _currentPage = page;
        }
        public void SetParentPage(MusicBrowsePage parent)
        {
            _parentPage = parent;
            _parentPage.DisableBackButton();
            _parentPage.refreshSong += RefreshSong;
            ClearUsbDeviceMusicList(null, null);
            RefreshUsbDeviceMusicList(null, null);
            _parentPage.refreshUsbDeviceMusicList += RefreshUsbDeviceMusicList;
            _parentPage.clearUsbDeviceMusicList += ClearUsbDeviceMusicList;
            RefreshPage();
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

        private void RefreshSong(object? sender, EventArgs e)
        {
            RefreshPage();
        }

        public async void RefreshPage()
        {
            if (_parentPage != null)
            {
                _currentPageType = _parentPage.pageType;
                _currentAlbumName = _parentPage.currentAlbumName;
                _currentArtistName = _parentPage.currentArtistName;
                _currentFolderName = _parentPage.currentFolderName;

                if (_currentPageType == "album" && !string.IsNullOrEmpty(_currentAlbumName))
                {
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(
                        MusicDatabaseService.GetAlbumMusicFromMem(_currentAlbumName, null));
                    await LoadMusicAsync(musics, _currentPageType);
                }
                else if (_currentPageType == "artist" && !string.IsNullOrEmpty(_currentArtistName))
                {
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(
                        MusicDatabaseService.GetArtistMusicFromMem(_currentArtistName, null));
                    await LoadMusicAsync(musics, _currentPageType);
                }
                else if (_currentPageType == "folder" && !string.IsNullOrEmpty(_currentFolderName))
                {
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(
                        MusicDatabaseService.GetFolderMusicFromMem(_currentFolderName, AppData.searchText));
                    await LoadMusicAsync(musics, _currentPageType);
                }
            }
        }

        public void SortMusicList(string sortOrder, string type)
        {
            var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;
            List<Music> musics = new List<Music>();

            if (MusicList.Count > 0)
            {
                musics = ToolUtils.SortMusicList(type, order, MusicList.ToList());
            }

            MusicList.Clear();
            foreach (var music in musics)
            {
                MusicList.Add(music);
            }
        }

        public async Task LoadMusicAsync(ObservableCollection<Music> musics, string type = null)
        {
            try
            {
                MusicList.Clear();
                foreach (var music in musics)
                {
                    MusicList.Add(music);
                }

                if (!string.IsNullOrEmpty(type))
                {
                    SortMusicList("DefaultOrder", type);
                }

                UpdateMusicListView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
            }
        }

        public void UpdateFavouriteMusic(Music music)
        {
            if (MusicList != null && MusicList.Count > 0)
            {
                var index = MusicList.IndexOf(MusicList.FirstOrDefault(m => m.Id == music.Id));
                if (index != -1)
                {
                    MusicList[index].IsFavorite = music.IsFavorite;
                }
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (_musicPlaybackService.currentPlayingMusic != null)
                {
                    var selectedMusic = MusicList.FirstOrDefault(music =>
                        music.Id == _musicPlaybackService.currentPlayingMusic.Id);

                    if (selectedMusic != null)
                    {
                        SelectedMusic = selectedMusic;
                        _messenger.Send(new ScrollToMusicMessageHepler(selectedMusic));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            }
        }

        public async Task MusicListView_DoubleTapped()
        {            
            if (SelectedMusic != null && _parentPage != null)
            {
                _musicPlaybackService.currentPlayingList = MusicList.ToList();
                await _parentPage.PlayMusic(SelectedMusic);
            }
        }

        public async Task PlayMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                _musicPlaybackService.currentPlayingList = uniqueSelectedMusics;
                await _parentPage.PlayMusic(uniqueSelectedMusics[0]);
            }
            else
            {
                if (SelectedMusic != null)
                {
                    _musicPlaybackService.currentPlayingList = MusicList.ToList();
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
                    await MusicDatabaseService.RemoveMusic(item.Id);
                    MusicList.Remove(item);
                }
            }
            else
            {
                if (SelectedMusic != null)
                {
                    MusicList.Remove(SelectedMusic);
                    await MusicDatabaseService.RemoveMusic(SelectedMusic.Id);
                }
            }

            if (_parentPage != null)
            {
                _parentPage.MainWindow_updateMusicList(null, null);
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

        public async Task IsFavouriteIconButton_Click(Music music)
        {
            if (music != null)
            {
                await _parentPage.AddToFavourite(music);
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            }
        }

        public void AuthorTextBlock_Tapped(string artist)
        {
            if (_parentPage != null)
            {
                _parentPage.SelectBarArtist(artist);
            }
        }

        public void AlbumTextBlock_Tapped(string albumName)
        {
            if (_parentPage != null)
            {
                _parentPage.SelectBarAlbum(albumName);
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
    }
}
