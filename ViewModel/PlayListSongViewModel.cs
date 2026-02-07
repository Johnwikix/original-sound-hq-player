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
    public partial class PlayListSongViewModel : ObservableObject
    {
        private ObservableCollection<Music> _musicList = [];
        public ObservableCollection<Music> MusicList
        {
            get => _musicList;
            set => SetProperty(ref _musicList, value);
        }

        private IEnumerable<Music> Playlist;

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
        private Music _currentMusicObject;
        public Music CurrentMusicObject
        {
            get => _currentMusicObject;
            set => SetProperty(ref _currentMusicObject, value);
        }
        private string _secondTitle;
        public string SecondTitle
        {
            get => _secondTitle;
            set => SetProperty(ref _secondTitle, value);
        }

        private MusicBrowsePage? _parentPage { get; }
        public AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private PlayListSongPage _currentPage { get; set; }
        private int _currentPlayListId;

        public PlayListSongViewModel(MusicBrowsePage parent, AppObservableObj appObservableObj,MusicDatabaseService musicDatabaseService) 
        {
            _parentPage = parent;
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
            _parentPage.refreshPage += RefreshPlayList;
        }

        public void SetCurrentPage(PlayListSongPage page)
        {
            _currentPage = page;
        }

        public void ReceiveNavigation()
        {
            //_parentPage.DisableBackButton();
            PlayListName = _parentPage.ViewModel.currentPlayList.Name;
            InitizeData();
            RefreshUsbDeviceMusicList(null, null);
        }

        private void InitizeTitle()
        {
            var albums = Playlist
                        .AsValueEnumerable()
                        .Select(music => music.Album)
                        .Distinct()
                        .Count();
            var authors = Playlist
                .AsValueEnumerable()
                .Select(music => music.Author)
                .Distinct()
                .Count();
            SecondTitle = $"{Playlist.AsValueEnumerable().Count()} {ToolUtils.GetString("NumberOfSongs")} · {albums} {ToolUtils.GetString("NumberOfAlbums")} · {authors} {ToolUtils.GetString("NumberOfArtists")}";
        }

        public void ShowTransmission()
        {
            if (_parentPage is not null)
            {
                _parentPage.ShowTransmission();
            }
        }

        public void HideTransmission()
        {
            if (_parentPage is not null)
            {
                _parentPage.HideTransmission();
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

        private void RefreshPlayList(object? sender, bool e)
        {
            if (e) InitizeData();
        }

        private void InitizeData()
        {
            if (_parentPage is not null)
            {
                Playlist = _musicDatabaseService.GetMusicByPlayListIdFromMem(_parentPage.ViewModel.currentPlayListId, AppData.searchText);
                InitizeTitle();
                _currentPlayListId = _parentPage.ViewModel.currentPlayListId;
                LoadMusicAsync(Playlist);
            }
        }

        public async void MusicListView_DragItemsCompleted()
        {
            if (AppData.sortOrder == "DefaultOrder")
            {
                if (_parentPage is not null)
                {
                    for (int i = 0; i < MusicList.Count; i++)
                    {
                        MusicList[i].PlayListOrder = MusicList.Count - i;
                    }
                    await _musicDatabaseService.UpdatePlayListMusicOrderBatch(_parentPage.ViewModel.currentPlayList.Id, MusicList.AsValueEnumerable().ToList());
                    await _musicDatabaseService.GetPlayListMusic();
                }
            }
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
                CurrentMusicObject = MusicList?.AsValueEnumerable().FirstOrDefault();
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
                if (AppObservableObj.CurrentPlayingMusic is not null)
                {
                    var selectedMusic = MusicList.AsValueEnumerable().FirstOrDefault(music =>
                        music.Id == AppObservableObj.CurrentPlayingMusic.Id);

                    if (selectedMusic is not null)
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
            if (MusicList is not null && MusicList.Count > 0)
            {
                Music? currentMusic = MusicList.AsValueEnumerable().FirstOrDefault(m => m.Id == music.Id);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (currentMusic is not null)
                    {
                        currentMusic.IsFavorite = music.IsFavorite;
                    }
                });
            }
        }

        public void MusicListView_DoubleTapped()
        {
            if (SelectedMusic is not null && _parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList =
                                        new ObservableCollection<Music>(MusicList);
                _parentPage.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                AppObservableObj.SequentialPlayingList = new(uniqueSelectedMusics);
                _parentPage?.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First(), IsChangeList: true);
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppObservableObj.SequentialPlayingList = new(MusicList);
                    _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
                }
            }
        }

        public async Task<bool> IsDeleteFromDisk()
        {
            if (_parentPage is null)
            {
                return false;
            }
            return await _parentPage.AreUSureDeleteFromDisk();
        }

        public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                await _musicDatabaseService.DeleteAllMusicFromPlayList(_currentPlayListId, uniqueSelectedMusics.AsValueEnumerable().Select(item => item.Id).ToImmutableList());
                for (int i = MusicList.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(MusicList[i]))
                    {
                        MusicList.RemoveAt(i);
                    }
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    await _musicDatabaseService.RemoveMusicFromPlayList(_currentPlayListId, SelectedMusic.Id);
                    MusicList.Remove(SelectedMusic);
                }
            }
            await _musicDatabaseService.GetPlayListMusic();
        }

        public async Task SetAsFavoriteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    await AppObservableObj.AddToFavourite(item);
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    await AppObservableObj.AddToFavourite(SelectedMusic);
                }
            }

            AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
        }

        public void OpenInExplorer_Click()
        {
            if (SelectedMusic is not null)
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
            _parentPage?.ViewModel?.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
        }

        public void AlbumTextBlock_Tapped(string albumName)
        {
            if (_parentPage is not null)
            {
                _parentPage.SelectBarAlbum(albumName);
            }
        }

        public void AuthorTextBlock_Tapped(string artist)
        {
            if (_parentPage is not null)
            {
                _parentPage.SelectBarArtist(artist);
            }
        }

        public void MusicDetail_Click()
        {
            var music = AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == SelectedMusic.Id);
            if (music is not null)
            {
                var musicDetailsWindow = new MusicDetailsWindow(music);
                //musicDetailsWindow.MusicDetailChanged += MusicDetailsWindow_MusicDetailChanged;
                musicDetailsWindow.Activate();
            }
        }

        //private void MusicDetailsWindow_MusicDetailChanged(object? sender, Music musicItem)
        //{
        //    foreach (var music in MusicList)
        //    {
        //        if (music.Path == musicItem.Path)
        //        {
        //            music.Title = musicItem.Title;
        //            music.Author = musicItem.Author;
        //            music.Album = musicItem.Album;
        //            music.Year = musicItem.Year;
        //            music.TrackNumber = musicItem.TrackNumber;
        //            music.Lyrics = musicItem.Lyrics;
        //            break;
        //        }
        //    }
        //}

        [RelayCommand]
        private async void IsFavouriteIconButtonChange(Music music)
        {
            if (music is not null)
            {
                // 通过事件通知视图更新图标
                await AppObservableObj.AddToFavourite(music);
                AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
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
                        await _musicDatabaseService.UpdateMusicInfo(music);
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
                    await _musicDatabaseService.UpdateMusicInfo(music);
                }
            }
            AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
        }

        public void AddToCurrentPlayList(IEnumerable<Music> uniqueSelectedMusics)
        {
            int index = AppObservableObj.CurrentPlayingList.IndexOf(AppObservableObj.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(m => m.Id == AppObservableObj.CurrentPlayingMusic.Id));
            // 如果找到匹配项，则在其后插入新列表
            if (index != -1 && uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Any())
            {
                var existingIds = new HashSet<int>(AppObservableObj.CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
                var newMusicsToAdd = uniqueSelectedMusics.AsValueEnumerable()
                    .Where(music => !existingIds.Contains(music.Id)).ToList();
                for (int i = newMusicsToAdd.Count - 1; i >= 0; i--)
                {
                    AppObservableObj.CurrentPlayingList.Insert(index + 1, newMusicsToAdd[i]);
                }
            }
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
            int index = AppObservableObj.CurrentPlayingList.IndexOf(AppObservableObj.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(m => m.Id == AppObservableObj.CurrentPlayingMusic.Id));
            // 如果找到匹配项，则在其后插入新列表
            if (index != -1 && music is not null)
            {
                var existingIds = new HashSet<int>(AppObservableObj.CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
                if (!existingIds.Contains(music.Id))
                {
                    AppObservableObj.CurrentPlayingList.Insert(index + 1, music);
                }
            }

        }

        [RelayCommand]
        private void PlayMusic(Music music)
        {
            if (music is not null && _parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new(MusicList);
                _parentPage.PlayMusic(music: music, IsChangeList: true);
            }
        }

        [RelayCommand]
        private void OnPlayAll()
        {
            if (_parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new(MusicList);
                if (MusicList.Count > 0)
                {
                    _parentPage.PlayMusic(music: MusicList[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        public void ExportPlayList()
        {
            ToolUtils.ExportPlayList(_parentPage.ViewModel.currentPlayList);
        }

        public async void EditPlayListName(Func<Task<string>> getNameCallback)
        {
            string newName = await getNameCallback();
            var playList = _parentPage.ViewModel.currentPlayList;
            if (!string.IsNullOrEmpty(newName))
            {
                playList.Name = newName;
                await _musicDatabaseService.UpdatePlayList(playList);
                PlayListName = newName;
            }
        }
    }
}
