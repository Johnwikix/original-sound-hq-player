using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
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
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private MusicBrowseViewModel _musicBrowseViewModel { get; }
        private PlayListSongPage _currentPage { get; set; }
        private int _currentPlayListId { get; set; }
        private ILogger<PlayListSongViewModel> _logger;

        public PlayListSongViewModel(MusicBrowseViewModel musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, ILogger<PlayListSongViewModel> logger) 
        {
            _musicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
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

        public void UpDateUsbDeviceMenuflyout()
        {
            var usbFlyout = MenuOptions.FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
            if (AppData.UsbStorageDevices.Count == 0)
            {
                if (usbFlyout is not null) MenuOptions.Remove(usbFlyout);
                return;
            }
            if (usbFlyout is null)
            {
                usbFlyout = new MenuModel { Title = ToolUtils.GetString("SendToUsbDevice"), Tag = "SendToUsbDevice", Children = [] };
                MenuOptions.Add(usbFlyout);
            }
            usbFlyout.Children.Clear();
            foreach (var usb in AppData.UsbStorageDevices)
            {
                var title = $"{usb.Name} , {ToolUtils.GetString("Path")}：{usb.Path} , {ToolUtils.GetString("FreeSpace")}：{usb.FreeSpaceInGB}GB";
                usbFlyout.Children.Add(new() { Title = title, Tag = usb, Command = TransmitFileToUsbCommand });
            }
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
            AppViewModel.IsBackBtnEnable = true;
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
        private void InitizeData()
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
        public async void MusicListView_DragItemsCompleted()
        {
            if (AppViewModel.SelectedSortOption.Tag.ToString() == "DefaultOrder")
            {
                for (int i = 0; i < AppViewModel.PlayListSongs.Count; i++)
                {
                    AppViewModel.PlayListSongs[i].PlayListOrder = AppViewModel.PlayListSongs.Count - i;
                }
                await _musicDatabaseService.UpdatePlayListMusicOrderBatch(AppViewModel.CurrentPlayList.Id, AppViewModel.PlayListSongs);
                await _musicDatabaseService.GetPlayListMusic();
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
                _logger.LogError(ex, $"UpdateMusicListView 滚动音乐失败: {ex.Message}");
            }
        }

        public void MusicListView_DoubleTapped()
        {
            if (SelectedMusic is not null && _musicBrowseViewModel is not null)
            {
                AppViewModel.SequentialPlayingList = new ObservableCollection<Music>(AppViewModel.PlayListSongs.Select(x => x.Music));
                _musicBrowseViewModel.PlayMusic(music: SelectedMusic.Music, IsChangeList: true).Wait();
            }
        }        

        public void AlbumTextBlock_Tapped(string albumName)
        {
            _musicBrowseViewModel?.SelectBarAlbum(albumName);
        }

        public void AuthorTextBlock_Tapped(string artist)
        {
            _musicBrowseViewModel?.SelectBarArtist(artist);
        }       

        [RelayCommand]
        private async Task OnPlayAll()
        {
            AppViewModel.SequentialPlayingList = new(AppViewModel.PlayListSongs.Select(x => x.Music));
            if (AppViewModel.SequentialPlayingList.Count > 0)
            {
                await _musicBrowseViewModel.PlayMusic(music: AppViewModel.SequentialPlayingList[0], IsChangeList: true);
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
            _ = _musicBrowseViewModel.ConvertAudio_Click(SelectedMusics.Select(x => x.Music), tag);
        }

        [RelayCommand]
        public async Task DeleteMenuItem()
        {
            if (SelectedMusics is null) return;
            if (SelectedMusics.AsValueEnumerable().Count() > 1)
            {
                await _musicDatabaseService.DeleteAllMusicFromPlayList(_currentPlayListId, SelectedMusics.AsValueEnumerable().Select(item => item.Music.Id).ToImmutableList());
                foreach (var item in SelectedMusics)
                {
                    AppViewModel.PlayListSongs.Remove(item);
                }
            }
            else
            {
                await _musicDatabaseService.RemoveMusicFromPlayList(_currentPlayListId, SelectedMusic.Music.Id);
                AppViewModel.PlayListSongs.Remove(SelectedMusic);
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
                        _logger.LogError(ex, $"OpenInExplorer 打开资源管理器时出错: {ex.Message}");
                    }
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
        public async Task ReGetLyrics()
        {
            await AppViewModel.ReGetLyrics(SelectedMusics.Select(x=>x.Music), SelectedMusic.Music);
        }

        [RelayCommand]
        private async Task Play()
        {
            if (SelectedMusics.Count == 1)
            {
                if (_musicBrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new(AppViewModel.PlayListSongs.Select(x => x.Music));
                    await _musicBrowseViewModel.PlayMusic(music: SelectedMusics[0].Music, IsChangeList: true);
                }
            }
            if (SelectedMusics.Count > 1)
            {
                if (_musicBrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new(SelectedMusics.Select(x => x.Music));
                    await _musicBrowseViewModel.PlayMusic(music: SelectedMusics[0].Music, IsChangeList: true);
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

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbStorageDevice usbDevice)
        {
            await AppViewModel.TransmitFileToUsb(SelectedMusics.Select(x=>x.Music), usbDevice);
        }
    }
}
