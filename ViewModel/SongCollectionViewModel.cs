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
using Microsoft.UI.Xaml;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;

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

        private Music _currentMusicObject;
        public Music CurrentMusicObject
        {
            get => _currentMusicObject;
            set => SetProperty(ref _currentMusicObject, value);
        }

        private string _firstTitle;
        public string FirstTitle
        {
            get => _firstTitle;
            set => SetProperty(ref _firstTitle, value);
        }
        private string _secondTitle;
        public string SecondTitle
        {
            get => _secondTitle;
            set => SetProperty(ref _secondTitle, value);
        }
        private string _thirdTitle;
        public string ThirdTitle
        {
            get => _thirdTitle;
            set => SetProperty(ref _thirdTitle, value);
        }

        private Visibility _isAlbumImageVisible = Visibility.Visible;
        public Visibility IsAlbumImageVisible
        {
            get => _isAlbumImageVisible;
            set => SetProperty(ref _isAlbumImageVisible, value);
        }
        private ObservableCollection<PlayList> _playLists;
        public ObservableCollection<PlayList> PlayLists
        {
            get => _playLists;
            set => SetProperty(ref _playLists, value);
        }

        private MusicBrowsePage? _parentPage;
        private SongCollectionPage _currentPage;
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        private string? _currentPageType;
        public string? CurrentPageType
        {
            get => _currentPageType;
            set => SetProperty(ref _currentPageType, value);
        }
        private string? _currentAlbumName;
        private string? _currentArtistName;
        private string? _currentFolderName;
        MusicPlaybackService _musicPlaybackService;
        private int progressBarValue = 0;
        private bool isMutiFile = false;
        public SongCollectionViewModel(MusicBrowsePage parent,MusicPlaybackService musicPlaybackService, AudioConverterService converterService)
        {
            _parentPage = parent;           
            _parentPage.refreshSong += RefreshSong;
            //_parentPage.refreshUsbDeviceMusicList += RefreshUsbDeviceMusicList;
            //_parentPage.clearUsbDeviceMusicList += ClearUsbDeviceMusicList;
            _converterService = converterService;
            _musicPlaybackService = musicPlaybackService;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");
            _converterService.updateProgress += OnConverterProgressUpdated;
        }
        public void SetCurrentPage(SongCollectionPage page)
        {
            _currentPage = page;
        }
        public void ReceiveNavigation()
        {
            _parentPage.DisableBackButton();
            //ClearUsbDeviceMusicList(null, null);
            RefreshUsbDeviceMusicList(null, null);            
            RefreshPage();
        }

        private void OnConverterProgressUpdated(object sender, double progress)
        {
            if (_progressDialog != null)
            {
                if (progressBarValue < (int)progress)
                {
                    progressBarValue = (int)progress;
                }
                if (isMutiFile)
                {
                    if (progressBarValue < 100)
                    {
                        _ = _progressDialog.UpdateProgress(progressBarValue);
                    }
                }
                else
                {
                    _ = _progressDialog.UpdateProgress(progressBarValue);
                }
            }
        }

        public void ClearUsbDeviceMusicList(object? sender, EventArgs e)
        {

            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var music in MusicList)
                {
                    music.IsExistOnDevice = 0;
                }
            });
        }

        public void RefreshUsbDeviceMusicList(object? sender, EventArgs e)
        {
            ToolUtils.RefreshUsbDeviceMusicList(MusicList);
            //var usbMusicGroups = AppData.musicOnUsbDevice
            //                .GroupBy(u => u.Title)
            //                .ToDictionary(g => g.Key, g => g.ToList());

            //foreach (var music in MusicList)
            //{
            //    music.IsExistOnDevice = 0;

            //    if (usbMusicGroups.TryGetValue(music.Title, out var matchingItems))
            //    {
            //        music.IsExistOnDevice = 1;
            //        foreach (var usbMusic in matchingItems)
            //        {
            //            if (music.Author == usbMusic.Author &&
            //                music.Album == usbMusic.Album &&
            //                music.Extension == usbMusic.Extension)
            //            {
            //                music.IsExistOnDevice = 2;
            //                break;
            //            }
            //        }
            //    }
            //}
        }

        private void RefreshSong(object? sender, EventArgs e)
        {
            RefreshPage();
        }

        public void RefreshPage()
        {
            if (_parentPage != null)
            {
                CurrentPageType = _parentPage.ViewModel?.PageType;
                _currentAlbumName = _parentPage.CurrentAlbum?.Album;
                _currentArtistName = _parentPage.CurrentArtist?.Author;
                _currentFolderName = _parentPage.CurrentFolder?.LastLevelFolderPath;

                if (CurrentPageType == "album" && !string.IsNullOrEmpty(_currentAlbumName))
                {
                    IsAlbumImageVisible = Visibility.Visible;
                    CurrentMusicObject = _parentPage.CurrentAlbum;
                    _ = Task.Run(async () =>
                    {
                        if (CurrentMusicObject?.Cover == null)
                        {
                            BitmapImage cover = await ToolUtils.GetAlbumCover(CurrentMusicObject, AppSettings.CoverSize);
                            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                            {
                                CurrentMusicObject.Cover = cover;
                            });
                            if (AppSettings.isCoverCacheEnabled && cover != null)
                            {
                                AppData.albumCoverCache.SetValue(CurrentMusicObject.Album, cover);
                            }
                        }
                    });
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(
                        MusicDatabaseService.GetAlbumMusicFromMem(_currentAlbumName, null));
                    FirstTitle = CurrentMusicObject.Album;
                    SecondTitle = string.Join(" · ", AppData.allSongs
                        .Where(music => music.Album == CurrentMusicObject.Album)
                        .Select(music => music.Author)
                        .Distinct());
                    ThirdTitle = $"{(CurrentMusicObject.Year!=0 ? $"{CurrentMusicObject.Year} · ".ToString():"")}{AppData.allSongs.Count(music => music.Album == CurrentMusicObject.Album)} {ToolUtils.GetString("NumberOfSongs")}";
                    LoadMusicAsync(musics, CurrentPageType);
                }
                else if (CurrentPageType == "artist" && !string.IsNullOrEmpty(_currentArtistName))
                {
                    IsAlbumImageVisible = Visibility.Collapsed;
                    CurrentMusicObject = _parentPage.CurrentArtist;
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(
                        MusicDatabaseService.GetArtistMusicFromMem(_currentArtistName, null));
                    FirstTitle = CurrentMusicObject.Author;
                    var authorAlbums = AppData.allSongs
                        .Where(music => music.Author == CurrentMusicObject.Author)
                        .Select(music => music.Album)
                        .Distinct()
                        .Count();
                    SecondTitle = $"{AppData.allSongs.Count(music => music.Author == CurrentMusicObject.Author)} {ToolUtils.GetString("NumberOfSongs")} · {authorAlbums} {ToolUtils.GetString("NumberOfAlbums")}";
                    ThirdTitle = ToolUtils.GetString("Artist");
                    LoadMusicAsync(musics, CurrentPageType);
                }
                else if (CurrentPageType == "folder" && !string.IsNullOrEmpty(_currentFolderName))
                {
                    IsAlbumImageVisible = Visibility.Collapsed;
                    CurrentMusicObject = _parentPage.CurrentFolder;
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(
                        MusicDatabaseService.GetFolderMusicFromMem(_currentFolderName, AppData.searchText));
                    FirstTitle = CurrentMusicObject?.LastLevelFolderPath;
                    var albums = AppData.allSongs
                        .Where(music => music.LastLevelFolderPath == CurrentMusicObject.LastLevelFolderPath)
                        .Select(music => music.Album)
                        .Distinct()
                        .Count();
                    var authors = AppData.allSongs.Where(music => music.LastLevelFolderPath == CurrentMusicObject.LastLevelFolderPath)
                        .Select(music => music.Author)
                        .Distinct()
                        .Count();
                    SecondTitle = $"{AppData.allSongs.Count(music => music.LastLevelFolderPath == CurrentMusicObject.LastLevelFolderPath)} {ToolUtils.GetString("NumberOfSongs")} · {albums} {ToolUtils.GetString("NumberOfAlbums")} · {authors} {ToolUtils.GetString("NumberOfArtists")}";
                    ThirdTitle = ToolUtils.GetString("Folder");
                    LoadMusicAsync(musics, CurrentPageType);
                }
            }
        }

        public void SortMusicList(string sortOrder, string type)
        {
            var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;
            //List<Music> musics = new List<Music>();            

            if (MusicList.Count > 0)
            {
                MusicList = new ObservableCollection<Music>(ToolUtils.SortMusicList(type, order, MusicList));
            }
        }

        public void LoadMusicAsync(ObservableCollection<Music> musics, string type = null)
        {
            try
            {
                //if((type== "album" || type== "artist") && CompareMusicCollections(MusicList, musics)){
                //    return;
                //}
                //MusicList.Clear();
                //foreach (var music in musics)
                //{
                //    MusicList.Add(music);
                //}
                MusicList = musics;

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

        public bool CompareMusicCollections(ObservableCollection<Music> collection1, ObservableCollection<Music> collection2)
        {
            if (ReferenceEquals(collection1, collection2))
                return true;

            if (collection1 == null || collection2 == null)
                return false;

            if (collection1.Count != collection2.Count)
                return false;

            for (int i = 0; i < collection1.Count; i++)
            {
                Music item1 = collection1[i];
                Music item2 = collection2[i];

                if (item1 == null || item2 == null)
                {
                    if (item1 != item2)
                        return false;
                    continue;
                }
                if (item1.Path != item2.Path)
                    return false;
            }
            return true;
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

        public void UpdateMusicListView()
        {
            try
            {
                if (_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList != null)
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

        public void MusicListView_DoubleTapped()
        {            
            if (SelectedMusic != null && _parentPage != null)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList =
                                        new ObservableCollection<Music>(MusicList);
                _parentPage?.PlayMusic(SelectedMusic);
            }
        }

        public void PlayMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                _parentPage?.PlayMusic(uniqueSelectedMusics[0]);
            }
            else
            {
                if (SelectedMusic != null)
                {
                    _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = new ObservableCollection<Music>(MusicList);
                    _parentPage?.PlayMusic(SelectedMusic);
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
                    ToolUtils.DeleteFileFromDisk(item.Path);
                    MusicList.Remove(item);
                }
            }
            else
            {
                if (SelectedMusic != null)
                {                    
                    await MusicDatabaseService.RemoveMusic(SelectedMusic.Id);
                    ToolUtils.DeleteFileFromDisk(SelectedMusic.Path);
                    MusicList.Remove(SelectedMusic);
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
            progressBarValue = 0;
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                isMutiFile = true;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {    
                    _progressDialog.RequestedTheme = AppSettings.elementTheme;
                    await _progressDialog.UpdateProgress(progressBarValue);
                    //_converterService.updateProgress += (sender, progress) =>
                    //{
                    //    if (progressBarValue < (int)progress)
                    //    {
                    //        progressBarValue = (int)progress;
                    //    }
                    //    if (progressBarValue < 100)
                    //    {
                    //        _ = _progressDialog.UpdateProgress(progressBarValue);
                    //    }
                    //};
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
                isMutiFile = false;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    if (SelectedMusic != null) {
                        if (SelectedMusic.Extension.ToLower() == menuItem?.Tag?.ToString()?.ToLower())
                        {
                            _parentPage?.ViewModel.UpdateInfoBar(ToolUtils.GetString("InfoBarMessageConverter"));
                            return;
                        }
                        //int progressBarValue = 0;
                        _progressDialog.RequestedTheme = AppSettings.elementTheme;
                        _ = _progressDialog.UpdateProgress(progressBarValue);
                        _ = _converterService.ConvertAudio2Wav(SelectedMusic, menuItem.Tag.ToString());
                        //_converterService.updateProgress += (sender, progress) =>
                        //{
                        //    progressBarValue = (int)progress;
                        //    _ = _progressDialog.UpdateProgress(progressBarValue);
                        //};
                        if (progressBarValue < 100)
                        {
                            _progressDialog.XamlRoot = _currentPage.XamlRoot;
                            _ = _progressDialog.ShowAsync();
                        }
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

        [RelayCommand]
        private void OnPlayAll()
        {
            if (_parentPage != null)
            {
                _parentPage.ViewModel.CurrentPlayingList = new ObservableCollection<Music>(MusicList);
                if (MusicList.Count > 0)
                {
                    _parentPage.PlayMusic(MusicList[0]);
                }
            }
        }
        [RelayCommand]
        private void OnAlbumInfoChanged() {            
            if (CurrentMusicObject != null)
            {
                var albumDetailWindow = new AlbumDetailWindow(CurrentMusicObject);
                //if (albumPage != null)
                //{
                //    albumDetailWindow.AlbumDetailChanged += albumPage.OnAlbumDetailChanged;
                //}
                albumDetailWindow.Activate();
            }
        }

    }
}
