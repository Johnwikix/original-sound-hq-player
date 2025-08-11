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
using Microsoft.Extensions.DependencyInjection;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class SongCollectionViewModel : ObservableObject
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
        private MusicBrowseViewModel? _musicBrowseViewModel;
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
        public SongCollectionViewModel(MusicBrowsePage parent,MusicPlaybackService musicPlaybackService, AudioConverterService converterService, MusicBrowseViewModel musicBrowseViewModel)
        {
            _parentPage = parent;           
            _parentPage.refreshSong += RefreshSong;
            _converterService = converterService;
            _musicPlaybackService = musicPlaybackService;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");
            _converterService.updateProgress += OnConverterProgressUpdated;
            _musicBrowseViewModel = musicBrowseViewModel;
        }
        public void SetCurrentPage(SongCollectionPage page)
        {
            _currentPage = page;
        }
        public void ReceiveNavigation()
        {
            _parentPage?.DisableBackButton();
            if (_musicBrowseViewModel?.SortOptions.Count == 2) {
                _musicBrowseViewModel?.AllSortOptions();
            }
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
                _currentAlbumName = _parentPage.ViewModel.CurrentAlbum?.Album;
                _currentArtistName = _parentPage.ViewModel.CurrentArtist?.Author;
                _currentFolderName = _parentPage.ViewModel.CurrentFolder?.LastLevelFolderPath;

                if (CurrentPageType == "album" && !string.IsNullOrEmpty(_currentAlbumName))
                {
                    IsAlbumImageVisible = Visibility.Visible;
                    CurrentMusicObject = _parentPage.ViewModel.CurrentAlbum;
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
                    var musics = MusicDatabaseService.GetAlbumMusicFromMem(_currentAlbumName, null);
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
                    CurrentMusicObject = _parentPage.ViewModel.CurrentArtist;
                    var musics = MusicDatabaseService.GetArtistMusicFromMem(_currentArtistName, null);
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
                    CurrentMusicObject = _parentPage.ViewModel.CurrentFolder;
                    var musics = MusicDatabaseService.GetFolderMusicFromMem(_currentFolderName, AppData.searchText);
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

            if (MusicList.Count > 0)
            {
                ToolUtils.SortMusicListInPlace(type, order, MusicList);
            }
        }

        public void LoadMusicAsync(IEnumerable<Music> musics, string type = null)
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
                _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList =
                                        new ObservableCollection<Music>(MusicList);
                _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                _parentPage?.PlayMusic(music:uniqueSelectedMusics.First(), IsChangeList: true);
            }
            else
            {
                if (SelectedMusic != null)
                {
                    _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList = new ObservableCollection<Music>(MusicList);
                    _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
                }
            }
        }

        public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
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

        public async Task SetAsFavoriteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
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

        public async Task ConvertAudio_Click(IEnumerable<Music> uniqueSelectedMusics, MenuFlyoutItem? menuItem)
        {
            progressBarValue = 0;
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                isMutiFile = true;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {    
                    _progressDialog.RequestedTheme = AppSettings.elementTheme;
                    await _progressDialog.UpdateProgress(progressBarValue);
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
                        _progressDialog.RequestedTheme = AppSettings.elementTheme;
                        _ = _progressDialog.UpdateProgress(progressBarValue);
                        _ = _converterService.ConvertAudio2Wav(SelectedMusic, menuItem.Tag.ToString());
                        if (progressBarValue < 100)
                        {
                            _progressDialog.XamlRoot = _currentPage.XamlRoot;
                            _ = _progressDialog.ShowAsync();
                        }
                    }
                }
            }
        }

        [RelayCommand]
        private async void IsFavouriteIconButtonChange(Music music)
        {
            if (music != null)
            {
                await _parentPage.AddToFavourite(music);
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            }
        }

        //public async Task IsFavouriteIconButton_Click(Music music)
        //{
        //    if (music != null)
        //    {
        //        await _parentPage.AddToFavourite(music);
        //        AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
        //    }
        //}

        public async void ReGetLyrics_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    string lyrics = await ToolUtils.GetLyricsFromNet(item);
                    Music? music = AppData.allSongs.Where(m => m.Id == item.Id).FirstOrDefault();
                    if (music != null)
                    {
                        music.Lyrics = lyrics;
                        await MusicDatabaseService.UpdateMusicInfo(music);
                    }
                }
            }
            else
            {
                string lyrics = await ToolUtils.GetLyricsFromNet(SelectedMusic);
                Music? music = AppData.allSongs.Where(m => m.Id == SelectedMusic.Id).FirstOrDefault();
                if (music != null)
                {
                    music.Lyrics = lyrics;
                    await MusicDatabaseService.UpdateMusicInfo(music);
                }
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
                _parentPage.ViewModel.SequentialPlayingList = new ObservableCollection<Music>(MusicList);
                if (MusicList.Count > 0)
                {
                    _parentPage.PlayMusic(music:MusicList[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        private void OnAlbumInfoChanged() {            
            if (CurrentMusicObject != null)
            {
                var albumDetailWindow = new AlbumDetailWindow(CurrentMusicObject);
                albumDetailWindow.Activate();
            }
        }

        public void AddToCurrentPlayList(IEnumerable<Music> uniqueSelectedMusics)
        {
            int index = _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.IndexOf(_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.FirstOrDefault(m => m.Id == _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic.Id));
            // 如果找到匹配项，则在其后插入新列表
            if (index != -1 && uniqueSelectedMusics != null && uniqueSelectedMusics.Any())
            {
                var existingIds = new HashSet<int>(_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.Select(m => m.Id));
                var newMusicsToAdd = uniqueSelectedMusics
                    .Where(music => !existingIds.Contains(music.Id)).ToList();
                for (int i = newMusicsToAdd.Count - 1; i >= 0; i--)
                {
                    _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.Insert(index + 1, newMusicsToAdd[i]);
                }
            }
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
            int index = _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.IndexOf(_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.FirstOrDefault(m => m.Id == _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic.Id));
            // 如果找到匹配项，则在其后插入新列表
            if (index != -1 && music != null)
            {
                var existingIds = new HashSet<int>(_musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.Select(m => m.Id));
                if (!existingIds.Contains(music.Id))
                {
                    _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList.Insert(index + 1, music);
                }
            }

        }

    }
}
