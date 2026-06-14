using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    public partial class SongFolderListViewModel : ObservableObject
    {
        public Music SelectedMusic { get; set => SetProperty(ref field, value); }
        public List<Music> SelectedMusics { get; set; } = [];
        public ObservableCollection<MenuModel> MenuOptions { get; set => SetProperty(ref field, value); } = [];
        public string SecondTitle { get; set => SetProperty(ref field, value); }
        public string ThirdTitle { get; set => SetProperty(ref field, value); }
        public AppViewModel AppViewModel { get; }
        private MusicBrowseViewModel MusicBrowseViewModel { get; set; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private SongFolderListPage _currentPage { get; set; }
        private ILogger<SongFolderListViewModel> _logger;
        public SongFolderListViewModel(MusicBrowseViewModel musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, ILogger<SongFolderListViewModel> logger)
        {
            MusicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
            InitalizeOption();
        }

        private void InitalizeOption()
        {
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPlayItem"), Tag = "Play", Command = PlayCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutFavoriteItem"), Tag = "AddToFavour", Command = AddToFavourCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutAddToPlaylistItem"), Tag = "AddToPlayList", Children = [] });
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
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutDeleteItem"), Tag = "DeleteMenuItem", Command = DeleteMenuItemCommand });
        }
        public void UpDateUsbDeviceMenuflyout()
        {
            var usbFlyout = MenuOptions.AsValueEnumerable().FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
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

        public void SetCurrentPage(SongFolderListPage page)
        {
            _currentPage = page;
        }

        public void ReceiveNavigation()
        {
            RefreshPage();
            UpdateMusicListView();
            AppViewModel.IsBackBtnEnable = true;
        }



        public void RefreshPage()
        {
            if (AppViewModel.CurrentFolderObj is not null)
            {
                var currentFolder = AppViewModel.CurrentFolderObj.LastLevelFolderPath;
                int count = 0;
                int albums = 0;
                int authors = 0;
                var seenAlbums = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                var seenAuthors = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                var srcSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppViewModel.SongsSource);
                for (int i = 0; i < srcSpan.Length; i++)
                {
                    var music = srcSpan[i];
                    if (music.LastLevelFolderPath != currentFolder) continue;
                    count++;
                    if (!string.IsNullOrEmpty(music.Album) && seenAlbums.Add(music.Album)) albums++;
                    if (!string.IsNullOrEmpty(music.Author) && seenAuthors.Add(music.Author)) authors++;
                }
                SecondTitle = $"{count} {ToolUtils.GetString("NumberOfSongs")} · {albums} {ToolUtils.GetString("NumberOfAlbums")} · {authors} {ToolUtils.GetString("NumberOfArtists")}";
                ThirdTitle = ToolUtils.GetString("Folder");
            }
        }

        public async Task<bool> IsDeleteFromDisk()
        {
            if (MusicBrowseViewModel is null)
            {
                return false;
            }
            return await MusicBrowseViewModel.AreUSureDeleteFromDisk();
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (AppViewModel.CurrentPlayingList is not null && AppViewModel.CurrentPlayingMusic is not null &&
                    AppViewModel.TryFindById(AppViewModel.CurrentPlayingMusic.Id, out var m) && m is not null)
                {
                    SelectedMusic = m;
                    _currentPage?.OnScrollToMusic(m);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UpdateMusicListView 滚动音乐失败: {ex.Message}");
            }
        }

        public async Task MusicListView_DoubleTappedAsync()
        {
            if (SelectedMusic is not null && MusicBrowseViewModel is not null)
            {
                AppViewModel.SequentialPlayingList = new ObservableCollection<Music>(AppViewModel.FolderSongs);
                await MusicBrowseViewModel.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        public void MusicListView_DoubleTapped() => _ = MusicListView_DoubleTappedAsync();

        public void AuthorTextBlock_Tapped(string artist)
        {
            MusicBrowseViewModel?.SelectBarArtist(artist);
        }

        public void AlbumTextBlock_Tapped(string albumName)
        {
            MusicBrowseViewModel?.SelectBarAlbum(albumName);
        }

        [RelayCommand]
        public async Task ConvertAudio(string tag)
        {
            _ = MusicBrowseViewModel.ConvertAudio_Click(SelectedMusics, tag);
        }

        [RelayCommand]
        public async Task DeleteMenuItem()
        {
            if (!await IsDeleteFromDisk()) return;
            if (SelectedMusics is not null && SelectedMusics.Count > 1)
            {
                foreach (var item in SelectedMusics)
                {
                    if (ToolUtils.DeleteFileFromDisk(item.Path))
                    {
                        AppViewModel.RemoveFromSongsSource(item);
                    }
                }
            }
            else
            {
                if (ToolUtils.DeleteFileFromDisk(SelectedMusic.Path))
                {
                    AppViewModel.RemoveFromSongsSource(SelectedMusic);
                }
            }
        }

        [RelayCommand]
        public void OpenInExplorer()
        {
            if (SelectedMusic is not null)
            {
                var filePath = SelectedMusic.Path;
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
                var musicDetailsWindow = new MusicDetailsWindow(SelectedMusics[0]);
                musicDetailsWindow.Activate();
            }
        }
        [RelayCommand]
        public async Task ReGetLyrics()
        {
            await AppViewModel.ReGetLyrics(SelectedMusics, SelectedMusic);
        }

        [RelayCommand]
        private async Task Play()
        {
            if (SelectedMusics.Count == 1)
            {
                if (MusicBrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new ObservableCollection<Music>(AppViewModel.FolderSongs);
                    await MusicBrowseViewModel.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
                }
            }
            if (SelectedMusics.Count > 1)
            {
                if (MusicBrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new ObservableCollection<Music>(SelectedMusics);
                    await MusicBrowseViewModel.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
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
            if (SelectedMusics.Count > 0)
            {
                _ = _musicDatabaseService.AddMusicListToPlayList(SelectedMusics, playListId);
            }
        }

        [RelayCommand]
        public void AddToCurrentPlayList()
        {
            if (SelectedMusics.Count > 0)
            {
                foreach (var item in SelectedMusics)
                {
                    AppViewModel.AddMusicToCurrentPlayList(item);
                }
            }
        }

        [RelayCommand]
        private async Task OnPlayAll()
        {
            if (MusicBrowseViewModel is not null)
            {
                AppViewModel.SequentialPlayingList = new ObservableCollection<Music>(AppViewModel.FolderSongs);
                if (AppViewModel.SequentialPlayingList.Count > 0)
                {
                    await MusicBrowseViewModel.PlayMusic(music: AppViewModel.SequentialPlayingList[0], IsChangeList: true);
                }
            }
        }

        [RelayCommand]
        public void AddMusicToCurrentPlayList(Music music)
        {
            AppViewModel.AddMusicToCurrentPlayList(music);
        }

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbStorageDevice usbDevice)
        {
            await AppViewModel.TransmitFileToUsb(SelectedMusics, usbDevice);
        }
    }
}
