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
    public partial class SongListViewModel : ObservableObject
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

        private bool _isCoverVisible = false;
        public bool IsCoverVisible
        {
            get => _isCoverVisible;
            set => SetProperty(ref _isCoverVisible, value);
        }

        private MusicBrowsePage? _parentPage;
        private SongListPage currentPage;
        private string _lastSearchText = "";
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        private BassMusicPlaybackService _musicPlaybackService;
        private int progressBarValue = 0;
        private bool isMutiFile = false;

        public SongListViewModel(MusicBrowsePage parent, BassMusicPlaybackService musicPlaybackService, AudioConverterService converterService)
        {
            _parentPage = parent;
            _parentPage.refreshPage += RefreshPage;
            //_parentPage.refreshUsbDeviceMusicList += RefreshUsbDeviceMusicList;
            //_parentPage.clearUsbDeviceMusicList += ClearUsbDeviceMusicList;
            _musicPlaybackService = musicPlaybackService;
            _converterService = converterService;
            _converterService.updateProgress += OnConverterProgressUpdated;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");
            //_messenger = messenger;
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

            //ClearUsbDeviceMusicList(null, null);
            RefreshUsbDeviceMusicList(null, null);
        }

        public async Task<bool> IsDeleteFromDisk()
        {
            if (_parentPage == null)
            {
                return false;
            }
            return await _parentPage.AreUSureDeleteFromDisk();
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
            ToolUtils.RefreshUsbDeviceMusicList(MusicList);
        }

        private void RefreshPage(object? sender, bool e)
        {
            InitializeDatabase();
        }

        public void SortMusicList(string sortOrder)
        {
            var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;

            if (MusicList.Count > 0)
            {
                ToolUtils.SortMusicListInPlace("song", order, MusicList);
            }
        }

        private void InitializeDatabase()
        {
            var query = MusicDatabaseService.GetMusicListFromMem(AppData.searchText);
            LoadMusicAsync(query);
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
                if (_parentPage != null && _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic != null)
                {
                    var selectedMusic = MusicList.FirstOrDefault(music =>
                        music.Id == _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingMusic.Id);

                    if (selectedMusic != null)
                    {
                        SelectedMusic = selectedMusic;
                        currentPage.OnScrollToMusic(selectedMusic);
                        //_messenger.Send(new ScrollToMusicMessageHepler(selectedMusic));
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
                _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList = MusicList;
                _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
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

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                _parentPage?.PlayMusic(music: uniqueSelectedMusics.First(), IsChangeList: true);
            }
            else
            {
                _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList = MusicList;
                _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public async Task ConvertAudio_Click(IEnumerable<Music> uniqueSelectedMusics, MenuFlyoutItem menuItem)
        {
            progressBarValue = 0;
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                isMutiFile = true;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    _progressDialog.RequestedTheme = AppSettings.elementTheme;
                    await _progressDialog.UpdateProgress(progressBarValue);
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
                            _progressDialog.XamlRoot = currentPage.XamlRoot;
                            _ = _progressDialog.ShowAsync();
                        }
                    }
                }
            }
        }

        public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                for (int i = MusicList.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.Contains(MusicList[i]))
                    {                        
                        if (ToolUtils.DeleteFileFromDisk(MusicList[i].Path)) {
                            await MusicDatabaseService.RemoveMusic(MusicList[i].Id);
                            MusicList.RemoveAt(i);
                        }
                    }
                }
            }
            else
            {
                if (SelectedMusic != null)
                {                    
                    if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path)) {
                        await MusicDatabaseService.RemoveMusic(SelectedMusic.Id);
                        MusicList.Remove(SelectedMusic);
                    }
                }
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

        //public async Task IsFavouriteIconButton_Click(Music music)
        //{
        //    if (music != null)
        //    {
        //        await _parentPage.AddToFavourite(music);
        //        AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
        //    }
        //}

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
            var music = AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == SelectedMusic.Id);
            if (music != null)
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
                    music.TrackNumber = musicItem.TrackNumber;
                    music.Lyrics = musicItem.Lyrics;
                    break;
                }
            }
        }

        public async void ReGetLyrics_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    string lyrics = await ToolUtils.GetLyricsFromNet(item);
                    Music? music = AppData.allSongs.Where(m => m.Id == item.Id).AsValueEnumerable().FirstOrDefault();
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
                Music? music = AppData.allSongs.Where(m => m.Id == SelectedMusic.Id).AsValueEnumerable().FirstOrDefault();
                if (music != null)
                {
                    music.Lyrics = lyrics;
                    await MusicDatabaseService.UpdateMusicInfo(music);
                }
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
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

        [RelayCommand]
        private async void IsFavouriteIconButtonChange(Music music)
        {
            if (music != null)
            {
                await _parentPage.AddToFavourite(music);
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            }
        }

        [RelayCommand]
        private void PlayMusic(Music music)
        {
            if (music != null && _parentPage != null)
            {
                _musicPlaybackService.MusicBrowseViewModel.SequentialPlayingList = MusicList;
                _parentPage.PlayMusic(music: music, IsChangeList: true);
            }
        }
    }
}
