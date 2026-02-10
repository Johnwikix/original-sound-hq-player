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
        public PlayListMusicItem SelectedMusic { get; set => SetProperty(ref field, value); }
        public Music CurrentMusicObject { get; set => SetProperty(ref field, value); }
        public string SecondTitle { get; set => SetProperty(ref field, value); }
        private MusicBrowsePage? _parentPage { get; }
        public AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private PlayListSongPage _currentPage { get; set; }
        private int _currentPlayListId { get; set; }

        public PlayListSongViewModel(MusicBrowsePage parent, AppObservableObj appObservableObj,MusicDatabaseService musicDatabaseService) 
        {
            _parentPage = parent;
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
            //_parentPage.refreshPage += RefreshPlayList;
        }

        public void SetCurrentPage(PlayListSongPage page)
        {
            _currentPage = page;
        }

        public void ReceiveNavigation()
        {
            InitizeData();
            UpdateMusicListView();
            App.MainWindow.IsBackBtnEnable = true;
            //RefreshUsbDeviceMusicList(null, null);
        }

        private void InitizeTitle()
        {
            var albums = AppObservableObj.PlayListSongs
                        .AsValueEnumerable()
                        .Select(music => music.Music.Album)
                        .Distinct()
                        .Count();
            var authors = AppObservableObj.PlayListSongs
                .AsValueEnumerable()
                .Select(music => music.Music.Author)
                .Distinct()
                .Count();
            SecondTitle = $"{AppObservableObj.PlayListSongs.AsValueEnumerable().Count()} {ToolUtils.GetString("NumberOfSongs")} · {albums} {ToolUtils.GetString("NumberOfAlbums")} · {authors} {ToolUtils.GetString("NumberOfArtists")}";
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

        private void RefreshPlayList(object? sender, bool e)
        {
            //if (e) InitizeData();
        }

        private void InitizeData()
        {
            if (_parentPage is not null)
            {
                AppObservableObj.PlayListSongs.Clear();
                var musics = _musicDatabaseService.GetMusicByPlayListIdFromMem(AppObservableObj.CurrentPlayListId, AppObservableObj.SearchText);                
                foreach (var music in musics)
                {
                    AppObservableObj.PlayListSongs.Add(music);
                }
                CurrentMusicObject = AppObservableObj.PlayListSongs.AsValueEnumerable().FirstOrDefault()?.Music;
                InitizeTitle();
                _currentPlayListId = AppObservableObj.CurrentPlayListId;
            }
        }

        public async void MusicListView_DragItemsCompleted()
        {
            if (AppObservableObj.SelectedSortOption.Tag.ToString() == "DefaultOrder")
            {
                if (_parentPage is not null)
                {
                    for (int i = 0; i < AppObservableObj.PlayListSongs.Count; i++)
                    {
                        AppObservableObj.PlayListSongs[i].PlayListOrder = AppObservableObj.PlayListSongs.Count - i;
                    }
                    await _musicDatabaseService.UpdatePlayListMusicOrderBatch(AppObservableObj.CurrentPlayList.Id, AppObservableObj.PlayListSongs);
                    await _musicDatabaseService.GetPlayListMusic();
                }
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (AppObservableObj.CurrentPlayingMusic is not null)
                {
                    var selectedMusic = AppObservableObj.PlayListSongs.AsValueEnumerable().FirstOrDefault(music =>
                        music.Music.Id == AppObservableObj.CurrentPlayingMusic.Id);

                    if (selectedMusic is not null)
                    {
                        SelectedMusic = selectedMusic;
                        _currentPage?.OnScrollToMusic(selectedMusic);
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
                AppObservableObj.SequentialPlayingList = new ObservableCollection<Music>(AppObservableObj.PlayListSongs.Select(x => x.Music));
                _parentPage.PlayMusic(music: SelectedMusic.Music, IsChangeList: true);
            }
        }

        public void PlayMenuItem_Click(IEnumerable<PlayListMusicItem> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                AppObservableObj.SequentialPlayingList = new(uniqueSelectedMusics.Select(x => x.Music));
                _parentPage?.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First().Music, IsChangeList: true);
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppObservableObj.SequentialPlayingList = new(AppObservableObj.PlayListSongs.Select(x => x.Music));
                    _parentPage?.PlayMusic(music: SelectedMusic.Music, IsChangeList: true);
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

        public async Task DeleteMenuItem_Click(IEnumerable<PlayListMusicItem> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            {
                await _musicDatabaseService.DeleteAllMusicFromPlayList(_currentPlayListId, uniqueSelectedMusics.AsValueEnumerable().Select(item => item.Music.Id).ToImmutableList());
                for (int i = AppObservableObj.PlayListSongs.Count - 1; i >= 0; i--)
                {
                    if (uniqueSelectedMusics.AsValueEnumerable().Contains(AppObservableObj.PlayListSongs[i]))
                    {
                        AppObservableObj.PlayListSongs.RemoveAt(i);
                    }
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    await _musicDatabaseService.RemoveMusicFromPlayList(_currentPlayListId, SelectedMusic.Music.Id);
                    AppObservableObj.PlayListSongs.Remove(SelectedMusic);
                }
            }
            await _musicDatabaseService.GetPlayListMusic();
        }

        public async Task SetAsFavoriteMenuItem_Click(IEnumerable<PlayListMusicItem> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.Count() > 1)
            {
                foreach (PlayListMusicItem item in uniqueSelectedMusics)
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == item.Music.Id)?.UpdateFavourite();
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    AppObservableObj.AllSongs.FirstOrDefault(m => m.Id == SelectedMusic.Music.Id)?.UpdateFavourite();
                }
            }
        }

        public void OpenInExplorer_Click()
        {
            if (SelectedMusic is not null)
            {
                var filePath = SelectedMusic.Music.Path;
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

        public async Task ConvertAudio_Click(IEnumerable<PlayListMusicItem> uniqueSelectedMusics, MenuFlyoutItem? menuItem)
        {
            _parentPage?.ViewModel?.ConvertAudio_Click(uniqueSelectedMusics.Select(x=>x.Music), menuItem);
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
            if (SelectedMusic is not null)
            {
                var musicDetailsWindow = new MusicDetailsWindow(SelectedMusic.Music);
                musicDetailsWindow.Activate();
            }
        }

        public async void ReGetLyrics_Click(IEnumerable<PlayListMusicItem> uniqueSelectedMusics)
        {
            AppObservableObj.ReGetLyrics(uniqueSelectedMusics.Select(x=>x.Music), SelectedMusic.Music);
            //if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
            //{
            //    foreach (PlayListMusicItem item in uniqueSelectedMusics)
            //    {
            //        (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(item.Music);
            //        Music? music = AppObservableObj.AllSongs.AsValueEnumerable().Where(m => m.Id == item.Music.Id).FirstOrDefault();
            //        if (music is not null)
            //        {
            //            music.Lyrics = lyrics;
            //            music.TranslatdeLyrics = transLrc;
            //            await _musicDatabaseService.UpdateMusicInfo(music);
            //        }
            //    }
            //}
            //else
            //{
            //    (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(SelectedMusic.Music);
            //    Music? music = AppObservableObj.AllSongs.AsValueEnumerable().Where(m => m.Id == SelectedMusic.Music.Id).FirstOrDefault();
            //    if (music is not null)
            //    {
            //        music.Lyrics = lyrics;
            //        music.TranslatdeLyrics = transLrc;
            //        await _musicDatabaseService.UpdateMusicInfo(music);
            //    }
            //}
            //AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
        }

        //public void AddToCurrentPlayList(IEnumerable<Music> uniqueSelectedMusics)
        //{
        //    int index = AppObservableObj.CurrentPlayingList.IndexOf(AppObservableObj.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(m => m.Id == AppObservableObj.CurrentPlayingMusic.Id));
        //    // 如果找到匹配项，则在其后插入新列表
        //    if (index != -1 && uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Any())
        //    {
        //        var existingIds = new HashSet<int>(AppObservableObj.CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
        //        var newMusicsToAdd = uniqueSelectedMusics.AsValueEnumerable()
        //            .Where(music => !existingIds.Contains(music.Id)).ToList();
        //        for (int i = newMusicsToAdd.Count - 1; i >= 0; i--)
        //        {
        //            AppObservableObj.CurrentPlayingList.Insert(index + 1, newMusicsToAdd[i]);
        //        }
        //    }
        //}

        //[RelayCommand]
        //public void AddMusicToCurrentPlayList(Music music)
        //{
        //    int index = AppObservableObj.CurrentPlayingList.IndexOf(AppObservableObj.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(m => m.Id == AppObservableObj.CurrentPlayingMusic.Id));
        //    // 如果找到匹配项，则在其后插入新列表
        //    if (index != -1 && music is not null)
        //    {
        //        var existingIds = new HashSet<int>(AppObservableObj.CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
        //        if (!existingIds.Contains(music.Id))
        //        {
        //            AppObservableObj.CurrentPlayingList.Insert(index + 1, music);
        //        }
        //    }

        //}

        //[RelayCommand]
        //private void PlayMusic(Music music)
        //{
        //    if (music is not null && _parentPage is not null)
        //    {
        //        AppObservableObj.SequentialPlayingList = newAppObservableObj.PlayListSongs);
        //        _parentPage.PlayMusic(music: music, IsChangeList: true);
        //    }
        //}

        [RelayCommand]
        private void OnPlayAll()
        {
            if (_parentPage is not null)
            {
                AppObservableObj.SequentialPlayingList = new(AppObservableObj.PlayListSongs.Select(x => x.Music));
                if (AppObservableObj.SequentialPlayingList.Count > 0)
                {
                    _parentPage.PlayMusic(music: AppObservableObj.SequentialPlayingList[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        public void ExportPlayList()
        {
            ToolUtils.ExportPlayList(AppObservableObj.CurrentPlayList);
        }

        public async void EditPlayListName(Func<Task<string>> getNameCallback)
        {
            string newName = await getNameCallback();
            if (!string.IsNullOrEmpty(newName))
            {
                AppObservableObj.CurrentPlayList.Name = newName;
                await _musicDatabaseService.UpdatePlayList(AppObservableObj.CurrentPlayList);
            }
        }
    }
}
