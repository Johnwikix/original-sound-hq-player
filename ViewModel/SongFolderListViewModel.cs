using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class SongFolderListViewModel : ObservableObject
    {
        //private ObservableCollection<Music> _musicList = [];
        //public ObservableCollection<Music> MusicList
        //{
        //    get => _musicList;
        //    set => SetProperty(ref _musicList, value);
        //}
        private Music _selectedMusic;
        public Music SelectedMusic
        {
            get => _selectedMusic;
            set => SetProperty(ref _selectedMusic, value);
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
        private ObservableCollection<PlayList> _playLists;
        public ObservableCollection<PlayList> PlayLists
        {
            get => _playLists;
            set => SetProperty(ref _playLists, value);
        }

        private MusicBrowsePage? _parentPage;
        private MusicBrowseViewModel? _musicBrowseViewModel;
        public AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private SongFolderListPage _currentPage;
        public SongFolderListViewModel(MusicBrowsePage parent,MusicBrowseViewModel musicBrowseViewModel, AppObservableObj appObservableObj, MusicDatabaseService musicDatabaseService)
        {
            _parentPage = parent;
            _parentPage.refreshSong += RefreshSong;
            _musicBrowseViewModel = musicBrowseViewModel;
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
        }
        public void SetCurrentPage(SongFolderListPage page)
        {
            _currentPage = page;
        }
        public void ReceiveNavigation()
        {
            //_parentPage?.DisableBackButton();
            //if (_musicBrowseViewModel?.SortOptions.Count == 2)
            //{
            //    _musicBrowseViewModel?.AllSortOptions();
            //}
            RefreshUsbDeviceMusicList(null, null);
            RefreshPage();
        }

        //public void ClearUsbDeviceMusicList(object? sender, EventArgs e)
        //{

        //    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //    {
        //        foreach (var music in MusicList)
        //        {
        //            music.IsExistOnDevice = 0;
        //        }
        //    });
        //}

        public void RefreshUsbDeviceMusicList(object? sender, EventArgs e)
        {
            //ToolUtils.RefreshUsbDeviceMusicList(MusicList);
        }

        private void RefreshSong(object? sender, EventArgs e)
        {
            RefreshPage();
        }

        public void RefreshPage()
        {
            if (_parentPage is not null && AppObservableObj.CurrentFolderObj is not null)
            {
                var musics = _musicDatabaseService.GetFolderMusicFromMem(AppObservableObj.CurrentFolderObj.LastLevelFolderPath, AppData.searchText);
                FirstTitle = AppObservableObj.CurrentFolderObj.LastLevelFolderPath;
                var albums = AppData.allSongs.AsValueEnumerable()
                    .Where(music => music.LastLevelFolderPath == AppObservableObj.CurrentFolderObj.LastLevelFolderPath)
                    .Select(music => music.Album)
                    .Distinct()
                    .Count();
                var authors = AppData.allSongs.AsValueEnumerable().Where(music => music.LastLevelFolderPath == AppObservableObj.CurrentFolderObj.LastLevelFolderPath)
                    .Select(music => music.Author)
                    .Distinct()
                    .Count();
                SecondTitle = $"{AppData.allSongs.AsValueEnumerable().Count(music => music.LastLevelFolderPath == AppObservableObj.CurrentFolderObj.LastLevelFolderPath)} {ToolUtils.GetString("NumberOfSongs")} · {albums} {ToolUtils.GetString("NumberOfAlbums")} · {authors} {ToolUtils.GetString("NumberOfArtists")}";
                ThirdTitle = ToolUtils.GetString("Folder");
                //LoadMusicAsync(musics, "folder");
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

        //[RelayCommand]
        //private void PlayMusic(Music music)
        //{
        //    if (music is not null && _parentPage is not null)
        //    {
        //        AppObservableObj.SequentialPlayingList = new([.. AppObservableObj.FolderSongsView.Cast<Music>()]);
        //        _parentPage.PlayMusic(music: music, IsChangeList: true);
        //    }
        //}

        //public void SortMusicList(string sortOrder, string type)
        //{
        //    var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;

        //    if (MusicList.Count > 0)
        //    {
        //        ToolUtils.SortMusicListInPlace(type, order, MusicList);
        //    }
        //}

        //public void LoadMusicAsync(IEnumerable<Music> musics, string type = null)
        //{
        //    try
        //    {
        //        MusicList.Clear();
        //        foreach (var music in musics)
        //        {
        //            MusicList.Add(music);
        //        }
        //        if (!string.IsNullOrEmpty(type))
        //        {
        //            SortMusicList("DefaultOrder", type);
        //        }

        //        UpdateMusicListView();
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
        //    }
        //}

        public bool CompareMusicCollections(ObservableCollection<Music> collection1, ObservableCollection<Music> collection2)
        {
            if (ReferenceEquals(collection1, collection2))
                return true;

            if (collection1 is null || collection2 is null)
                return false;

            if (collection1.Count != collection2.Count)
                return false;

            for (int i = 0; i < collection1.Count; i++)
            {
                Music item1 = collection1[i];
                Music item2 = collection2[i];

                if (item1 is null || item2 is null)
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
        //public void UpdateFavouriteMusic(Music music)
        //{
        //    if (MusicList is not null && MusicList.Count > 0)
        //    {
        //        Music? currentMusic = MusicList.AsValueEnumerable().FirstOrDefault(m => m.Id == music.Id);
        //        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        //        {
        //            if (currentMusic is not null)
        //            {
        //                currentMusic.IsFavorite = music.IsFavorite;
        //            }
        //        });
        //    }
        //}

        public void UpdateMusicListView()
        {
            try
            {
                if (AppObservableObj.CurrentPlayingList is not null)
                {
                    var selectedMusic = AppObservableObj.AllSongs.AsValueEnumerable().FirstOrDefault(music =>
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

        public void MusicListView_DoubleTapped()
        {
            if (SelectedMusic is not null && _parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new([.. AppObservableObj.FolderSongsView.Cast<Music>()]);
                _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
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
                    AppObservableObj.SequentialPlayingList = new([.. AppObservableObj.FolderSongsView.Cast<Music>()]);
                    _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
                }
            }
        }

        public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                for (int i = AppObservableObj.AllSongs.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(AppObservableObj.AllSongs[i]))
                    {
                        if (ToolUtils.DeleteFileFromDisk((AppObservableObj.AllSongs[i].Path)))
                        {
                            AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                        }
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                }
            }
        }
        //public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        //{
        //    if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
        //    {
        //        for (int i = MusicList.Count - 1; i >= 0; i--)
        //        {
        //            if (uniqueSelectedMusics.AsValueEnumerable().Contains(MusicList[i]))
        //            {
        //                if (ToolUtils.DeleteFileFromDisk(MusicList[i].Path))
        //                {
        //                    await _musicDatabaseService.RemoveMusic(MusicList[i].Id);
        //                    MusicList.RemoveAt(i);
        //                }
        //            }
        //        }
        //    }
        //    else
        //    {
        //        if (SelectedMusic is not null)
        //        {
        //            if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
        //            {
        //                await _musicDatabaseService.RemoveMusic(SelectedMusic.Id);
        //                MusicList.Remove(SelectedMusic);
        //            }
        //        }
        //    }

        //    App.MainWindow.UpdateMusicList();
        //}

        public async Task SetAsFavoriteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == item.Id)?.UpdateFavourite();
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.UpdateFavourite();
                }
            }
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
            _musicBrowseViewModel?.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
        }

        [RelayCommand]
        private async void IsFavouriteIconButtonChange(Music music)
        {
            if (music is not null)
            {
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

        public void AuthorTextBlock_Tapped(string artist)
        {
            if (_parentPage is not null)
            {
                _parentPage.SelectBarArtist(artist);
            }
        }

        public void AlbumTextBlock_Tapped(string albumName)
        {
            if (_parentPage is not null)
            {
                _parentPage.SelectBarAlbum(albumName);
            }
        }

        public void MusicDetail_Click()
        {
            var musicDetailsWindow = new MusicDetailsWindow(SelectedMusic);
            musicDetailsWindow.Activate();
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

        [RelayCommand]
        private void OnPlayAll()
        {
            if (_parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new([.. AppObservableObj.FolderSongsView.Cast<Music>()]);
                if (AppObservableObj.SequentialPlayingList.Count > 0)
                {
                    _parentPage.PlayMusic(music: AppObservableObj.SequentialPlayingList[0], IsChangeList: true);
                }
            }
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
            AppObservableObj.AddMusicToCurrentPlayList(music);
        }
    }
}
