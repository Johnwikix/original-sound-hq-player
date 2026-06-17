using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class FolderViewModel : ObservableObject
    {
        public Music SelectedItem { get; set => SetProperty(ref field, value); }
        public ObservableCollection<MenuModel> FolderMenuOptions { get; set => SetProperty(ref field, value); } = [];
        public bool IsInDetailMode { get; set => SetProperty(ref field, value); }
        private MusicBrowseViewModel? MusicBrowseViewModel { get; set; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private ILogger<FolderViewModel> _logger;

        public FolderViewModel(MusicBrowseViewModel? musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, ILogger<FolderViewModel> logger)
        {
            MusicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
            InitalizeOption();
        }

        public void ReceiveNavigation()
        {
            if (AppViewModel.CurrentFolderObj is not null && !string.IsNullOrEmpty(AppViewModel.CurrentFolderObj.LastLevelFolderPath))
            {
                IsInDetailMode = true;
                AppViewModel.PageType = "folder";
                AppViewModel.IsBackBtnEnable = true;
            }
            else
            {
                AppViewModel.CurrentFolderObj = null;
                IsInDetailMode = false;
                AppViewModel.PageType = "folderBrowse";
                AppViewModel.IsBackBtnEnable = false;
            }
        }

        private void InitalizeOption()
        {
            FolderMenuOptions.Add(new() { Title = "播放", Tag = "Play", Command = PlayCommand });
            FolderMenuOptions.Add(new() { Title = "添加到最爱", Tag = "AddToFavour", Command = AddToFavourCommand });
            FolderMenuOptions.Add(new() { Title = "添加到播放列表", Tag = "AddToPlayList", Children = [] });
            FolderMenuOptions.Add(new() { Title = "重新扫描", Tag = "Rescan", Command = RescanFolderCommand });
        }

        public void UpdateAlbumMenuOptionsPlayList()
        {
            var option = FolderMenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList)
            {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command = AddToPlayListCommand });
            }
        }

        public void UpDateUsbDeviceMenuflyout()
        {
            var usbFlyout = FolderMenuOptions.AsValueEnumerable().FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
            if (AppData.UsbStorageDevices.Count == 0)
            {
                if (usbFlyout is not null) FolderMenuOptions.Remove(usbFlyout);
                return;
            }
            if (usbFlyout is null)
            {
                usbFlyout = new MenuModel { Title = ToolUtils.GetString("SendToUsbDevice"), Tag = "SendToUsbDevice", Children = [] };
                FolderMenuOptions.Add(usbFlyout);
            }
            usbFlyout.Children.Clear();
            foreach (var usb in AppData.UsbStorageDevices)
            {
                var title = $"{usb.Name} , {ToolUtils.GetString("Path")}：{usb.Path} , {ToolUtils.GetString("FreeSpace")}：{usb.FreeSpaceInGB}GB";
                usbFlyout.Children.Add(new() { Title = title, Tag = usb, Command = TransmitFileToUsbCommand });
            }
        }

        public void FolderGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            GridViewItem item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item is not null)
            {
                Music folder = item.Content as Music;
                if (MusicBrowseViewModel is not null && folder is not null)
                {
                    try
                    {
                        AppViewModel.PageType = "folder";
                        AppViewModel.CurrentFolderObj = folder;
                        IsInDetailMode = true;
                        AppViewModel.IsBackBtnEnable = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"FolderGridView_ItemClick 导航失败: {ex.Message}");
                    }
                }
            }
        }

        public void EnterDetailFromCrossLink()
        {
            if (AppViewModel.CurrentFolderObj is null) return;
            IsInDetailMode = true;
            AppViewModel.IsBackBtnEnable = true;
        }

        public void CollapseDetail()
        {
            if (!IsInDetailMode) return;
            IsInDetailMode = false;
            AppViewModel.CurrentFolderObj = null;
            AppViewModel.PageType = "folderBrowse";
            AppViewModel.IsBackBtnEnable = false;
        }

        public void RefreshDetailView() { }

        [RelayCommand]
        private async Task RescanFolder()
        {
            await AppViewModel.RescanFolder(SelectedItem);
            await App.Services.GetRequiredService<AppViewModel>().RefreshSongsSourceAsync();
        }

        [RelayCommand]
        private void Play()
        {
            var folders = AppViewModel.SongsSource.AsValueEnumerable()
                .Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.Equals(SelectedItem.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.LastLevelFolderPath).ToList();
            if (folders is not null && folders.Count > 0)
            {
                if (MusicBrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new(folders);
                    MusicBrowseViewModel.PlayMusic(music: folders[0], IsChangeList: true).Wait();
                }
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            var folders = AppViewModel.SongsSource.AsValueEnumerable()
                .Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.Equals(SelectedItem.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.LastLevelFolderPath);
            foreach (var folder in folders)
            {
                folder.AddToFavourite();
            }
        }

        [RelayCommand]
        private void AddToPlayList(int playListId)
        {
            var folders = AppViewModel.SongsSource
               .Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.Equals(SelectedItem.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
               .OrderBy(m => m.LastLevelFolderPath);
            _ = _musicDatabaseService.AddMusicListToPlayList(folders, playListId);
        }

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbStorageDevice usbDevice)
        {
            var folders = AppViewModel.SongsSource
               .Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.Equals(SelectedItem.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
               .OrderBy(m => m.LastLevelFolderPath);
            await AppViewModel.TransmitFileToUsb(folders, usbDevice);
        }
    }
}
