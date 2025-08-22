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
    public partial class PlayListSongViewModel : ObservableObject
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

        private string _playListName;
        public string PlayListName
        {
            get => _playListName;
            set => SetProperty(ref _playListName, value);
        }

        private MusicBrowsePage? _parentPage;
        private PlayListSongPage _currentPage;
        //private string _lastSearchText = "";
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        private int _currentPlayListId;
        private MusicPlaybackService _musicPlaybackService;
        private int progressBarValue = 0;
        private bool isMutiFile = false;

        public PlayListSongViewModel(MusicBrowsePage parent, MusicPlaybackService musicPlaybackService, AudioConverterService converterService)
        {
            _parentPage = parent;
            _parentPage.refreshPage += RefreshPlayList;
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
            PlayListName = _parentPage.ViewModel.currentPlayList.Name;
            InitizeData();
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

        private void RefreshPlayList(object? sender, EventArgs e)
        {
            InitizeData();
        }

        private void InitizeData()
        {
            if (_parentPage != null)
            {
                var musicList = MusicDatabaseService.GetMusicByPlayListIdFromMem(_parentPage.ViewModel.currentPlayListId, AppData.searchText);
                _currentPlayListId = _parentPage.ViewModel.currentPlayListId;
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
                    //await MusicDatabaseService.UpdatePlayListMusicOrder(_parentPage.currentPlayList.Id, MusicList[i]);
                }
                await MusicDatabaseService.UpdatePlayListMusicOrderBatch(_parentPage.ViewModel.currentPlayList.Id, MusicList.ToList());
                await MusicDatabaseService.GetPlayListMusic();
            }
        }

        public void LoadMusicAsync(IEnumerable<Music> musics)
        {
            try
            {
                //MusicList = new ObservableCollection<Music>(musics);
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

        public void SortMusicList(string sortOrder)
        {
            var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;
            if (MusicList.Count > 0)
            {
                ToolUtils.SortMusicListInPlace("playList", order, MusicList);
            }
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

        public void MusicListView_DoubleTapped()
        {
            if (SelectedMusic != null && _parentPage != null)
            {
                _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList =
                                        new ObservableCollection<Music>(MusicList);
                _parentPage.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                _parentPage?.PlayMusic(music: uniqueSelectedMusics.First(), IsChangeList: true);
            }
            else
            {
                if (SelectedMusic != null)
                {
                    _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList =
                                new ObservableCollection<Music>(MusicList);
                    _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
                }
            }
        }

        public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                await MusicDatabaseService.DeleteAllMusicFromPlayList(_currentPlayListId, uniqueSelectedMusics.AsValueEnumerable().Select(item => item.Id).ToImmutableList());
                for (int i = MusicList.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.Contains(MusicList[i]))
                    {
                        MusicList.RemoveAt(i);
                    }
                }
                //foreach (Music item in uniqueSelectedMusics)
                //{
                //    //await MusicDatabaseService.RemoveMusicFromPlayList(_currentPlayListId, item.Id);
                //    MusicList.Remove(item);
                //}
            }
            else
            {
                if (SelectedMusic != null)
                {
                    await MusicDatabaseService.RemoveMusicFromPlayList(_currentPlayListId, SelectedMusic.Id);
                    MusicList.Remove(SelectedMusic);
                }
            }
            await MusicDatabaseService.GetPlayListMusic();
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
                    _ = _progressDialog.UpdateProgress(progressBarValue);
                    _progressDialog.RequestedTheme = AppSettings.elementTheme;
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
                    if (SelectedMusic != null)
                    {
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

        [RelayCommand]
        private async void IsFavouriteIconButtonChange(Music music)
        {
            if (music != null)
            {
                // 通过事件通知视图更新图标
                await _parentPage.AddToFavourite(music);
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            }
        }

        //public async Task IsFavouriteIconButton_Click(Music music)
        //{
        //    if (music != null)
        //    {
        //        // 通过事件通知视图更新图标
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
