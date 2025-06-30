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
    public partial class SongListViewModel : ObservableObject
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
        private SongListPage currentPage;
        private string _lastSearchText = "";
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        private readonly IMessenger _messenger;
        private MusicPlaybackService _musicPlaybackService;

        public SongListViewModel(MusicBrowsePage parent,MusicPlaybackService musicPlaybackService,AudioConverterService converterService, IMessenger messenger)
        {
            _parentPage = parent;
            _parentPage.refreshPage += RefreshPage;
            _parentPage.refreshUsbDeviceMusicList += RefreshUsbDeviceMusicList;
            _parentPage.clearUsbDeviceMusicList += ClearUsbDeviceMusicList;
            _musicPlaybackService = musicPlaybackService;
            _converterService = converterService;
            _converterService.updateProgress += OnConverterProgressUpdated;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");           
            _messenger = messenger;
        }

        public void SetCurrentPage(SongListPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {            

            if (_lastSearchText != AppData.searchText || MusicList == null || MusicList.Count == 0)
            {
                _lastSearchText = AppData.searchText;
                InitializeDatabase();
            }
            else
            {
                UpdateMusicListView();
                Debug.WriteLine("搜索条件未变更，保留当前视图状态");
            }

            ClearUsbDeviceMusicList(null, null);
            RefreshUsbDeviceMusicList(null, null);
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

        private void RefreshPage(object? sender, EventArgs e)
        {
            InitializeDatabase();
        }

        public void SortMusicList(string sortOrder)
        {
            var order = "DefaultOrder";
            ObservableCollection<Music> musics = new ObservableCollection<Music>();

            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }

            if (MusicList.Count > 0)
            {
                musics = new ObservableCollection<Music>(ToolUtils.SortMusicList("song", order, MusicList.ToList()));
            }

            MusicList.Clear();
            foreach (var music in musics)
            {
                MusicList.Add(music);
            }
        }

        private void InitializeDatabase()
        {
            ObservableCollection<Music> musics = new ObservableCollection<Music>(MusicDatabaseService.GetMusicListFromMem(AppData.searchText));
            LoadMusicAsync(musics);
        }

        public void LoadMusicAsync(ObservableCollection<Music> musics)
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
                if (_parentPage != null && _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic != null)
                {
                    var selectedMusic = MusicList.FirstOrDefault(music =>
                        music.Id == _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic.Id);

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
               _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = MusicList;
                await _parentPage.PlayMusic(SelectedMusic);
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

        public async void PlayMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                await _parentPage.PlayMusic(uniqueSelectedMusics[0]);
            }
            else
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = MusicList;
                await _parentPage.PlayMusic(SelectedMusic);               
            }
        }

        public async Task ConvertAudio_Click(List<Music> uniqueSelectedMusics,MenuFlyoutItem menuItem)
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
                            _progressDialog.XamlRoot = currentPage.XamlRoot;
                            _ = _progressDialog.ShowAsync();
                        }                    
                }
            }
        }

        public async Task DeleteMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    MusicList.Remove(item);
                    await MusicDatabaseService.RemoveMusic(item.Id);
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
    }
}
