using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
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
        private MusicBrowsePage parentPage;
        private FavouritePlayListPage currentPage;
        private MusicPlaybackService _musicPlaybackService;
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        private int progressBarValue = 0;
        private bool isMutiFile = false;
        public FavouritePlayListViewModel(MusicPlaybackService musicPlaybackService, AudioConverterService converterService, MusicBrowsePage musicBrowsePage)
        {
            parentPage = musicBrowsePage;
            parentPage.refreshPage += RefreshMusicList;
            _musicPlaybackService = musicPlaybackService;
            _converterService = converterService;
            _converterService.updateProgress += OnConverterProgressUpdated;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");            
        }

        private void OnConverterProgressUpdated(object? sender, double progress)
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
            }
            await MusicDatabaseService.UpdateAllAsync([.. MusicList]);
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
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

        public void MusicListView_DoubleTapped(Music selectedMusic)
        {
            if (selectedMusic != null && parentPage != null)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = MusicList;
                parentPage.PlayMusic(selectedMusic);
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
                    music.DiskNumber = musicItem.DiskNumber;
                    music.TrackNumber = musicItem.TrackNumber;
                    music.Lyrics = musicItem.Lyrics;
                    break;
                }
            }
        }
        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                parentPage.PlayMusic(uniqueSelectedMusics.First());
            }
            else
            {
                _musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = MusicList;
                parentPage.PlayMusic(SelectedMusic);
            }
        }

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

        public async void DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
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
                await MusicDatabaseService.RemoveMusic(SelectedMusic.Id);
                ToolUtils.DeleteFileFromDisk(SelectedMusic.Path);
                MusicList.Remove(SelectedMusic);
            }
        }

        public async void SetAsFavoriteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
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

        public async void ConvertAudio_Click(MenuFlyoutItem? menuItem, IEnumerable<Music> uniqueSelectedMusics)
        {
            progressBarValue = 0;
            _progressDialog.RequestedTheme = AppSettings.elementTheme;
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count() > 1)
            {
                isMutiFile = true;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
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
                            parentPage?.ViewModel.UpdateInfoBar(ToolUtils.GetString("InfoBarMessageConverter"));
                            return;
                        }
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

        [RelayCommand]
        private async void IsFavouriteIconButtonChange(Music music)
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
