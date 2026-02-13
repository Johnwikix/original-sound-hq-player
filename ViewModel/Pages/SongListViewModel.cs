using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        public Music SelectedMusic { get; set => SetProperty(ref field, value); }
        public List<Music> SelectedMusics { get; set; } = [];
        public ObservableCollection<MenuModel> SongListMenuOptions { get; set => SetProperty(ref field, value); } = [];
        private MusicBrowsePage? _parentPage;
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private SongListPage currentPage { get; set; }
        private string _lastSearchText = "";

        public SongListViewModel(MusicBrowsePage parent, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            _parentPage = parent;
            //_parentPage.refreshPage += RefreshPage;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            InitalizeOption();
        }

        private void InitalizeOption()
        {
            SongListMenuOptions.Add(new() { Title = "播放", Tag = "Play", Command = PlayCommand });
            SongListMenuOptions.Add(new() { Title = "添加/取消最爱", Tag = "AddToFavour", Command = AddToFavourCommand });
            SongListMenuOptions.Add(new() { Title = "添加到播放列表", Tag = "AddToPlayList", Children = [] });
            SongListMenuOptions.Add(new() { Title = "转换为", Tag = "AddToPlayList", Children = [
                new(){ Title="WAV",Tag="wav"},
                new(){ Title="MP3",Tag="mp3"},
                new(){ Title="FLAC",Tag="flac"},
                new(){ Title="Ogg",Tag="ogg"},
                new(){ Title="Opus",Tag="opus"},
                ] });
            SongListMenuOptions.Add(new() { Title = "添加到当前播放列表", Tag = "AddToPlayList"});
            SongListMenuOptions.Add(new() { Title = "重新获取歌词", Tag = "AddToPlayList" });
            SongListMenuOptions.Add(new() { Title = "打开文件位置", Tag = "AddToPlayList"});
            SongListMenuOptions.Add(new() { Title = "属性", Tag = "AddToPlayList" });
            SongListMenuOptions.Add(new() { Title = "从磁盘中删除", Tag = "AddToPlayList" });
        }

        public void UpdateAlbumMenuOptionsPlayList()
        {
            var option = SongListMenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList)
            {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command = AddToPlayListCommand });
            }
        }

        public void SetCurrentPage(SongListPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {

            //if (_lastSearchText != AppData.searchText || MusicList is null || MusicList.Count == 0)
            //{
            //    _lastSearchText = AppData.searchText;
            //    InitializeDatabase();
            //}
            //else
            //{
            //    UpdateMusicListView();
            //    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
            //}
            UpdateMusicListView();
            App.MainWindow.IsBackBtnEnable = false;
            //RefreshUsbDeviceMusicList(null, null);
        }

        public async Task<bool> IsDeleteFromDisk()
        {
            if (_parentPage is null)
            {
                return false;
            }
            return await _parentPage.AreUSureDeleteFromDisk();
        }

        public void ClearUsbDeviceMusicList(object? sender, EventArgs e)
        {

            //App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            //{
            //    foreach (var music in MusicList)
            //    {
            //        music.IsExistOnDevice = 0;
            //    }
            //});
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

        public void RefreshUsbDeviceMusicList(object? sender, EventArgs e)
        {
            //ToolUtils.RefreshUsbDeviceMusicList(MusicList);
        }

        //private void RefreshPage(object? sender, bool e)
        //{
        //    InitializeDatabase();
        //}

        public void SortMusicList(string sortOrder)
        {
            var order = string.IsNullOrEmpty(sortOrder) ? "DefaultOrder" : sortOrder;

            //if (MusicList.Count > 0)
            //{
            //    ToolUtils.SortMusicListInPlace("song", order, MusicList);
            //}
        }

        private void InitializeDatabase()
        {
            //var query = _musicDatabaseService.GetMusicListFromMem(AppData.searchText);
            //LoadMusicAsync(query);
        }

        //public void LoadMusicAsync(IEnumerable<Music> musics)
        //{
        //    try
        //    {
        //        MusicList.Clear();
        //        foreach (var music in musics)
        //        {
        //            MusicList.Add(music);
        //        }
        //        SortMusicList(AppData.sortOrder);
        //        UpdateMusicListView();
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
        //    }
        //}

        public void UpdateMusicListView()
        {
            try
            {
                if (AppViewModel.CurrentPlayingMusic is not null)
                {
                    var selectedMusic = AppViewModel.AllSongs.AsValueEnumerable().FirstOrDefault(music =>
                        music.Id == AppViewModel.CurrentPlayingMusic.Id);

                    if (selectedMusic is not null)
                    {
                        SelectedMusic = selectedMusic;
                        currentPage?.OnScrollToMusic(selectedMusic);
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
                AppViewModel.SequentialPlayingList = new([.. AppViewModel.AllSongsView.Cast<Music>()]);
                _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
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

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                AppViewModel.SequentialPlayingList = new(uniqueSelectedMusics);
                _parentPage?.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First(), IsChangeList: true);
            }
            else
            {
                AppViewModel.SequentialPlayingList = new([.. AppViewModel.AllSongsView.Cast<Music>()]);
                _parentPage?.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public async Task ConvertAudio_Click(IEnumerable<Music> uniqueSelectedMusics, MenuFlyoutItem? menuItem)
        {
            _parentPage?.ViewModel?.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
        }

        //public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        //{
        //    //if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
        //    //{
        //    //    for (int i = MusicList.Count - 1; i >= 0; i--)
        //    //    {
        //    //        if (uniqueSelectedMusics.AsValueEnumerable().Contains(MusicList[i]))
        //    //        {
        //    //            if (ToolUtils.DeleteFileFromDisk(MusicList[i].Path))
        //    //            {
        //    //                await _musicDatabaseService.RemoveMusic(MusicList[i].Id);
        //    //                MusicList.RemoveAt(i);
        //    //            }
        //    //        }
        //    //    }
        //    //}
        //    //else
        //    //{
        //    //    if (SelectedMusic is not null)
        //    //    {
        //    //        if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
        //    //        {
        //    //            await _musicDatabaseService.RemoveMusic(SelectedMusic.Id);
        //    //            MusicList.Remove(SelectedMusic);
        //    //        }
        //    //    }
        //    //}
        //}
        public async Task DeleteMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                for (int i = AppViewModel.AllSongs.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(AppViewModel.AllSongs[i]))
                    {
                        if (ToolUtils.DeleteFileFromDisk((AppViewModel.AllSongs[i].Path)))
                        {
                            AppViewModel.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                        }
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.Remove();
                }
            }
        }

        public async Task SetAsFavoriteMenuItem_Click(List<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.Count() > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == item.Id)?.UpdateFavourite();
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppViewModel.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Id)?.UpdateFavourite();
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
        public async void ReGetLyrics_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            AppViewModel.ReGetLyrics(uniqueSelectedMusics,SelectedMusic);
        }

        [RelayCommand]
        private void Play()
        {
            if (SelectedMusics.Count == 1)
            {
                if (_parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new([.. AppViewModel.AllSongsView.Cast<Music>()]);
                    _parentPage.PlayMusic(music: SelectedMusic, IsChangeList: true);
                }
            }
            else if (SelectedMusics.Count > 1) {
                if (_parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new(SelectedMusics);
                    _parentPage.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            if (SelectedMusics.Count > 0)
            {
                foreach (var music in SelectedMusics)
                {
                    music.UpdateFavourite();
                }
            }
        }

        [RelayCommand]
        private void AddToPlayList(int playListId)
        {
            //var albums = AppViewModel.AllSongs
            //    .Where(m => m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
            //    .OrderBy(m => m.Album);
            if (SelectedMusics.Count > 0)
            {
                _ = _musicDatabaseService.AddMusicListToPlayList(SelectedMusics, playListId);
            }           
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
            AppViewModel.AddMusicToCurrentPlayList(music);
        }

        [RelayCommand]
        private async void IsFavouriteIconButtonChange(Music music)
        {
            if (music is not null)
            {
                await AppViewModel.AddToFavourite(music);
                AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
            }
        }

        [RelayCommand]
        private void PlayMusic(Music music)
        {
            if (music is not null && _parentPage is not null)
            {
                AppViewModel.SequentialPlayingList = new([.. AppViewModel.AllSongsView.Cast<Music>()]);
                _parentPage.PlayMusic(music: music, IsChangeList: true);
            }
        }
    }
}
