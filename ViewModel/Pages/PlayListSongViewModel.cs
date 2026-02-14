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
        public List<PlayListMusicItem> SelectedMusics { get; set; } = [];
        public ObservableCollection<MenuModel> MenuOptions { get; set => SetProperty(ref field, value); } = [];
        public Music CurrentMusicObject { get; set => SetProperty(ref field, value); }
        public string SecondTitle { get; set => SetProperty(ref field, value); }
        private MusicBrowsePage? _parentPage { get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private PlayListSongPage _currentPage { get; set; }
        private int _currentPlayListId { get; set; }

        public PlayListSongViewModel(MusicBrowsePage parent, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService) 
        {
            _parentPage = parent;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            //_parentPage.refreshPage += RefreshPlayList;
            InitalizeOption();
        }
        private void InitalizeOption()
        {
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPlayItem"), Tag = "Play", Command = PlayCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutFavoriteItem"), Tag = "AddToFavour", Command = AddToFavourCommand });
            MenuOptions.Add(new()
            {
                Title = ToolUtils.GetString("FlyoutConvertItem"),
                Tag = "ConvertAudio",
                Children = [
                new(){ Title="Wav",Tag="wav",Command=ConvertAudioCommand},
                new(){ Title="Mp3",Tag="mp3",Command=ConvertAudioCommand},
                new(){ Title="Flac",Tag="flac",Command=ConvertAudioCommand},
                new(){ Title="Ogg",Tag="ogg",Command=ConvertAudioCommand},
                new(){ Title="Opus",Tag="opus",Command=ConvertAudioCommand},
                ]
            });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutAddToCurrentPlayList"), Tag = "AddMusicToCurrentPlayList", Command = AddToCurrentPlayListCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("ReGetLyrics"), Tag = "ReGetLyrics", Command = ReGetLyricsCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutOpenLocationItem"), Tag = "OpenInExplorer", Command = OpenInExplorerCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPropertiesItem"), Tag = "MusicDetail", Command = MusicDetailCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutRemoveFromPlaylistItem"), Tag = "DeleteMenuItem", Command = DeleteMenuItemCommand });
        }

        public void UpdateAlbumMenuOptionsPlayList()
        {
            var option = MenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList)
            {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command = AddToPlayListCommand });
            }
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
            var albums = AppViewModel.PlayListSongs
                        .AsValueEnumerable()
                        .Select(music => music.Music.Album)
                        .Distinct()
                        .Count();
            var authors = AppViewModel.PlayListSongs
                .AsValueEnumerable()
                .Select(music => music.Music.Author)
                .Distinct()
                .Count();
            SecondTitle = $"{AppViewModel.PlayListSongs.AsValueEnumerable().Count()} {ToolUtils.GetString("NumberOfSongs")} · {albums} {ToolUtils.GetString("NumberOfAlbums")} · {authors} {ToolUtils.GetString("NumberOfArtists")}";
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

        //private void RefreshPlayList(object? sender, bool e)
        //{
        //    //if (e) InitizeData();
        //}

        private void InitizeData()
        {
            if (_parentPage is not null)
            {
                AppViewModel.PlayListSongs.Clear();
                var musics = _musicDatabaseService.GetMusicByPlayListIdFromMem(AppViewModel.CurrentPlayListId, AppViewModel.SearchText);                
                foreach (var music in musics)
                {
                    AppViewModel.PlayListSongs.Add(music);
                }
                CurrentMusicObject = AppViewModel.PlayListSongs.AsValueEnumerable().FirstOrDefault()?.Music;
                InitizeTitle();
                _currentPlayListId = AppViewModel.CurrentPlayListId;
            }
        }

        public async void MusicListView_DragItemsCompleted()
        {
            if (AppViewModel.SelectedSortOption.Tag.ToString() == "DefaultOrder")
            {
                if (_parentPage is not null)
                {
                    for (int i = 0; i < AppViewModel.PlayListSongs.Count; i++)
                    {
                        AppViewModel.PlayListSongs[i].PlayListOrder = AppViewModel.PlayListSongs.Count - i;
                    }
                    await _musicDatabaseService.UpdatePlayListMusicOrderBatch(AppViewModel.CurrentPlayList.Id, AppViewModel.PlayListSongs);
                    await _musicDatabaseService.GetPlayListMusic();
                }
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (AppViewModel.CurrentPlayingMusic is not null)
                {
                    var selectedMusic = AppViewModel.PlayListSongs.AsValueEnumerable().FirstOrDefault(music =>
                        music.Music.Id == AppViewModel.CurrentPlayingMusic.Id);

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
                AppViewModel.SequentialPlayingList = new ObservableCollection<Music>(AppViewModel.PlayListSongs.Select(x => x.Music));
                _parentPage.PlayMusic(music: SelectedMusic.Music, IsChangeList: true);
            }
        }        

        public void AlbumTextBlock_Tapped(string albumName)
        {
            _parentPage?.SelectBarAlbum(albumName);
        }

        public void AuthorTextBlock_Tapped(string artist)
        {
            _parentPage?.SelectBarArtist(artist);
        }       

        [RelayCommand]
        private void OnPlayAll()
        {
            if (_parentPage is not null)
            {
                AppViewModel.SequentialPlayingList = new(AppViewModel.PlayListSongs.Select(x => x.Music));
                if (AppViewModel.SequentialPlayingList.Count > 0)
                {
                    _parentPage.PlayMusic(music: AppViewModel.SequentialPlayingList[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        public void ExportPlayList()
        {
            ToolUtils.ExportPlayList(AppViewModel.CurrentPlayList);
        }

        [RelayCommand]
        public async Task ConvertAudio(string tag)
        {
            _parentPage?.ViewModel?.ConvertAudio_Click(SelectedMusics.Select(x => x.Music), tag);
        }

        [RelayCommand]
        public async Task DeleteMenuItem()
        {
            if (SelectedMusics is not null && SelectedMusics.AsValueEnumerable().Count() > 1)
            {
                await _musicDatabaseService.DeleteAllMusicFromPlayList(_currentPlayListId, SelectedMusics.AsValueEnumerable().Select(item => item.Music.Id).ToImmutableList());
                foreach (var item in SelectedMusics)
                {
                    AppViewModel.PlayListSongs.Remove(item);
                }
            }
            else
            {
                if (SelectedMusic is not null)
                {
                    await _musicDatabaseService.RemoveMusicFromPlayList(_currentPlayListId, SelectedMusic.Music.Id);
                    AppViewModel.PlayListSongs.Remove(SelectedMusic);
                }
            }
            await _musicDatabaseService.GetPlayListMusic();
        }

        [RelayCommand]
        public void OpenInExplorer()
        {
            if (SelectedMusic is not null)
            {
                var filePath = SelectedMusic.Music.Path;
                if (System.IO.File.Exists(filePath))
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

        [RelayCommand]
        public void MusicDetail()
        {
            if (SelectedMusics.Count > 0)
            {
                var musicDetailsWindow = new MusicDetailsWindow(SelectedMusics[0].Music);
                musicDetailsWindow.Activate();
            }
        }
        [RelayCommand]
        public void ReGetLyrics()
        {
            _ = AppViewModel.ReGetLyrics(SelectedMusics.Select(x=>x.Music), SelectedMusic.Music);
        }

        [RelayCommand]
        private void Play()
        {
            if (SelectedMusics.Count == 1)
            {
                if (_parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new(AppViewModel.PlayListSongs.Select(x => x.Music));
                    _parentPage.PlayMusic(music: SelectedMusics[0].Music, IsChangeList: true);
                }
            }
            if (SelectedMusics.Count > 1)
            {
                if (_parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new(SelectedMusics.Select(x => x.Music));
                    _parentPage.PlayMusic(music: SelectedMusics[0].Music, IsChangeList: true);
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
                    music.Music.UpdateFavourite();
                }
            }
        }

        [RelayCommand]
        private void AddToPlayList(int playListId)
        {
            if (SelectedMusics.Count > 0)
            {
                _ = _musicDatabaseService.AddMusicListToPlayList(SelectedMusics.Select(x => x.Music), playListId);
            }
        }

        [RelayCommand]
        public void AddToCurrentPlayList()
        {
            if (SelectedMusics.Count > 0)
            {
                foreach (var item in SelectedMusics)
                {
                    AppViewModel.AddMusicToCurrentPlayList(item.Music);
                }
            }
        }
    }
}
