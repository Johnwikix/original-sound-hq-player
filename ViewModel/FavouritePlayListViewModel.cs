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
        private MusicBrowsePage parentPage;
        private FavouritePlayListPage currentPage;
        private AudioConverterService _converterService;
        private ProgressDialog _progressDialog;
        private int progressBarValue = 0;
        private bool isMutiFile = false;
        public FavouritePlayListViewModel(AudioConverterService converterService, MusicBrowsePage musicBrowsePage)
        {
            parentPage = musicBrowsePage;
            parentPage.refreshPage += RefreshMusicList;
            _converterService = converterService;
            _converterService.updateProgress += OnConverterProgressUpdated;
            _progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            _progressDialog.Title = ToolUtils.GetString("Processing");
        }

        private void OnConverterProgressUpdated(object? sender, double progress)
        {
            if (_progressDialog is not null)
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
                if (parentPage is not null)
                {
                    if (parentPage.ViewModel.CurrentPlayingMusic is not null)
                    {
                        var selectedMusic = MusicList.AsValueEnumerable().FirstOrDefault(music =>
                        music.Id == parentPage.ViewModel.CurrentPlayingMusic.Id);

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
                await MusicDatabaseService.UpdateAllAsync([.. MusicList]);
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
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
                parentPage.ViewModel.SequentialPlayingList = MusicList;
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
                parentPage.ViewModel.SequentialPlayingList = new ObservableCollection<Music>(uniqueSelectedMusics);
                parentPage.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First(), IsChangeList: true);
            }
            else
            {
                parentPage.ViewModel.CurrentPlayingList = MusicList;
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
                        await MusicDatabaseService.UpdateMusicInfo(music);
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
                    await MusicDatabaseService.UpdateMusicInfo(music);
                }
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
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
                            await MusicDatabaseService.RemoveMusic(MusicList[i].Id);
                            MusicList.RemoveAt(i);
                        }
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    await MusicDatabaseService.RemoveMusic(SelectedMusic.Id);
                    MusicList.Remove(SelectedMusic);
                }
            }
        }

        public async void SetAsFavoriteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null)
            {
                await MusicDatabaseService.CancelMusicsFavourite(uniqueSelectedMusics.AsValueEnumerable().ToList());
                for (int i = MusicList.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(MusicList[i]))
                    {
                        MusicList.RemoveAt(i);
                        if (parentPage.ViewModel.CurrentPlayingMusic is not null)
                        {
                            if (parentPage.ViewModel.CurrentPlayingMusic.Id == MusicList[i].Id)
                            {
                                parentPage.ViewModel.CurrentPlayingMusic.IsFavorite = MusicList[i].IsFavorite;
                            }
                        }
                    }
                }

                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
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
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                isMutiFile = true;
                if (menuItem is not null && menuItem.Tag.ToString() is not null)
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
                if (menuItem is not null && menuItem.Tag.ToString() is not null)
                {
                    if (SelectedMusic is not null)
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
            await parentPage.AddToFavourite(music);
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
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

        public void AddToCurrentPlayList(IEnumerable<Music> uniqueSelectedMusics)
        {
            int index = parentPage.ViewModel.CurrentPlayingList.IndexOf(parentPage.ViewModel.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(m => m.Id == parentPage.ViewModel.CurrentPlayingMusic.Id));
            // 如果找到匹配项，则在其后插入新列表
            if (index != -1 && uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Any())
            {
                var existingIds = new HashSet<int>(parentPage.ViewModel.CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
                var newMusicsToAdd = uniqueSelectedMusics.AsValueEnumerable()
                    .Where(music => !existingIds.Contains(music.Id)).ToList();
                for (int i = newMusicsToAdd.Count - 1; i >= 0; i--)
                {
                    parentPage.ViewModel.CurrentPlayingList.Insert(index + 1, newMusicsToAdd[i]);
                }
            }
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
            int index = parentPage.ViewModel.CurrentPlayingList.IndexOf(parentPage.ViewModel.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(m => m.Id == parentPage.ViewModel.CurrentPlayingMusic.Id));
            // 如果找到匹配项，则在其后插入新列表
            if (index != -1 && music is not null)
            {
                var existingIds = new HashSet<int>(parentPage.ViewModel.CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
                if (!existingIds.Contains(music.Id))
                {
                    parentPage.ViewModel.CurrentPlayingList.Insert(index + 1, music);
                }
            }
        }

        [RelayCommand]
        private void PlayMusic(Music music)
        {
            if (music is not null && parentPage is not null)
            {
                parentPage.ViewModel.SequentialPlayingList = MusicList;
                parentPage.PlayMusic(music: music, IsChangeList: true);
            }
        }
    }
}
