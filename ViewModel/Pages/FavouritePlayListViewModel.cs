using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
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
    public partial class FavouritePlayListViewModel : ObservableObject
    {
        public Music SelectedMusic { get; set => SetProperty(ref field, value); }
        public List<Music> SelectedMusics { get; set; } = [];
        public ObservableCollection<MenuModel> MenuOptions { get; set => SetProperty(ref field, value); } = [];
        public MusicBrowseViewModel BrowseViewModel { get; set; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private FavouritePlayListPage currentPage { get; set; }
        private ILogger<FavouritePlayListViewModel> _logger;
        public FavouritePlayListViewModel(MusicBrowseViewModel browseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, ILogger<FavouritePlayListViewModel> logger)
        {
            BrowseViewModel = browseViewModel;
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
                Children = ToolUtils.BuildConvertMenuChildren(ConvertAudioCommand)
            });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutAddToCurrentPlayList"), Tag = "AddMusicToCurrentPlayList", Command = AddToCurrentPlayListCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("ReGetLyrics"), Tag = "ReGetLyrics", Command = ReGetLyricsCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutOpenLocationItem"), Tag = "OpenInExplorer", Command = OpenInExplorerCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPropertiesItem"), Tag = "MusicDetail", Command = MusicDetailCommand });
            MenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutDeleteItem"), Tag = "DeleteMenuItem", Command = DeleteMenuItemCommand });
        }

        public void UpDateUsbDeviceMenuflyout()
            => ToolUtils.UpdateUsbSendMenu(MenuOptions, TransmitFileToUsbCommand);

        public void UpdateAlbumMenuOptionsPlayList()
        {
            var option = MenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList)
            {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command = AddToPlayListCommand });
            }
        }

        public void SetCurrentPage(FavouritePlayListPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            UpdateMusicListView();
            AppViewModel.IsBackBtnEnable = false;
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (AppViewModel.CurrentPlayingMusic is not null &&
                    AppViewModel.TryFindById(AppViewModel.CurrentPlayingMusic.Id, out var m) && m is not null)
                {
                    SelectedMusic = m;
                    currentPage?.OnScrollToMusic(m);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UpdateMusicListView 滚动音乐失败: {ex.Message}");
            }
        }

        public async Task DragItems()
        {
            if (AppViewModel.SelectedSortOption.Tag == "DefaultOrder")
            {
                for (int i = 0; i < AppViewModel.FavoriteSongs.Count; i++)
                {
                    AppViewModel.FavoriteSongs[i].Order = AppViewModel.FavoriteSongs.Count - i;
                }
                await _musicDatabaseService.UpdateAllAsync(AppViewModel.FavoriteSongs);
            }
        }

        public async Task<bool> IsDeleteFromDisk()
        {
            if (BrowseViewModel is null)
            {
                return false;
            }
            return await BrowseViewModel.AreUSureDeleteFromDisk();
        }

        public async Task MusicListView_DoubleTappedAsync(Music selectedMusic)
        {
            if (selectedMusic is not null && BrowseViewModel is not null)
            {
                AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(AppViewModel.FavoriteSongs);
                await BrowseViewModel.PlayMusic(music: selectedMusic, IsChangeList: true);
            }
        }

        public void MusicListView_DoubleTapped(Music selectedMusic) => _ = MusicListView_DoubleTappedAsync(selectedMusic);

        public void PlayMenuItem_Click(IEnumerable<Music> uniqueSelectedMusics)
        {
            if (uniqueSelectedMusics is ICollection<Music> col && col.Count > 1)
            {
                AppViewModel.SequentialPlayingList = new(col);
                var first = col.AsValueEnumerable().First();
                _ = BrowseViewModel.PlayMusic(music: first, IsChangeList: true);
            }
            else if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Any())
            {
                AppViewModel.SequentialPlayingList = new(uniqueSelectedMusics);
                _ = BrowseViewModel.PlayMusic(music: uniqueSelectedMusics.AsValueEnumerable().First(), IsChangeList: true);
            }
            else
            {
                AppViewModel.SequentialPlayingList = new(AppViewModel.FavoriteSongs);
                _ = BrowseViewModel.PlayMusic(music: SelectedMusic, IsChangeList: true);
            }
        }

        [RelayCommand]
        public async Task ConvertAudio(string tag)
        {
            BrowseViewModel?.ConvertAudio_Click(SelectedMusics, tag);
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
                if (BrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(AppViewModel.FavoriteSongs);
                    await BrowseViewModel.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
                }
            }
            if (SelectedMusics.Count > 1)
            {
                if (BrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(SelectedMusics);
                    await BrowseViewModel.PlayMusic(music: SelectedMusics[0], IsChangeList: true);
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
                    MusicCommands.UpdateFavouriteCommand.Execute(music);
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
                AppViewModel.AddMusicRangeToCurrentPlayList(SelectedMusics);
            }
        }

        public void AlbumTextBlock_Tapped(TextBlock textBlock)
        {
            string albumName = textBlock.Text;
            BrowseViewModel?.SelectBarAlbum(albumName);
        }

        public void AuthorTextBlock_Tapped(TextBlock textBlock)
        {
            string artist = textBlock.Text;
            BrowseViewModel?.SelectBarArtist(artist);
        }

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbSendTarget? target)
        {
            if (target?.Device is null) return;
            await AppViewModel.TransmitFileToUsb(SelectedMusics, target.Device, target.Format, target.BitrateKbps);
        }
    }
}
