using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
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
        private MusicBrowsePage? parentPage { get; }
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }


        public FolderViewModel(MusicBrowsePage parent, MusicBrowseViewModel? musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = parent;
            _musicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            InitalizeOption();
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentFolderObj = null;
            AppViewModel.PageType = "folderBrowse";
        }

        private void InitalizeOption()
        {
            FolderMenuOptions.Add(new() { Title = "播放", Tag = "Play", Command = PlayCommand });
            FolderMenuOptions.Add(new() { Title = "添加到最爱", Tag = "AddToFavour", Command = AddToFavourCommand });
            FolderMenuOptions.Add(new() { Title = "添加到播放列表", Tag = "AddToPlayList", Children = [] });
            FolderMenuOptions.Add(new() { Title = "重新扫描", Tag = "Rescan",Command= RescanFolderCommand });
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
            var usbFlyout = FolderMenuOptions.FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
            if (AppData.usbStorageDevices.Count == 0)
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
            foreach (var usb in AppData.usbStorageDevices)
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
                if (parentPage is not null && _musicBrowseViewModel is not null && folder is not null)
                {
                    try
                    {
                        AppViewModel.PageType = "folder";
                        AppViewModel.CurrentFolderObj = folder;
                        parentPage.NavigatePage(typeof(SongFolderListPage),new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.Message);
                    }
                }
            }
        }

        [RelayCommand]
        private async Task RescanFolder()
        {
            await AppViewModel.RescanFolder(SelectedItem);
            App.MainWindow?.UpdateMusicList();
        }

        [RelayCommand]
        private void Play()
        {
            var folders = AppViewModel.AllSongs.AsValueEnumerable()
                .Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.Equals(SelectedItem.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.LastLevelFolderPath).ToList();
            if (folders is not null && folders.Count > 0)
            {
                if (parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new(folders);
                    parentPage.PlayMusic(music: folders[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            var folders = AppViewModel.AllSongs.AsValueEnumerable()
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
            var folders = AppViewModel.AllSongs
               .Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.Equals(SelectedItem.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
               .OrderBy(m => m.LastLevelFolderPath);
            _ = _musicDatabaseService.AddMusicListToPlayList(folders, playListId);
        }

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbStorageDevice usbDevice)
        {
            var folders = AppViewModel.AllSongs
               .Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.Equals(SelectedItem.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
               .OrderBy(m => m.LastLevelFolderPath);
            if (folders.Any())
            {
                parentPage?.ShowTransmission();
                using (var usbWriter = new UsbWriterHelper())
                {
                    usbWriter.hideTransmission += (sender, args) =>
                    {
                        parentPage?.HideTransmission();
                    };
                    await usbWriter.WriteToUsb(folders, usbDevice);
                }
                foreach (var music in folders)
                {
                    var existingMusic = AppData.musicOnUsbDevice.AsValueEnumerable().Where(m => m.Title == music.Title).FirstOrDefault();
                    if (existingMusic is not null)
                    {
                        continue; // 如果已经存在，则跳过
                    }
                    UsbDeviceMusic usbDeviceMusic = new()
                    {
                        Title = music.Title,
                        Author = music.Author,
                        Album = music.Album,
                        Extension = music.Extension,
                        UniqueDeviceId = AppData.usbStorageDevice.UniqueId
                    };
                    AppData.musicOnUsbDevice.Add(usbDeviceMusic);
                }
            }
            AppViewModel.RefreshUsbDeviceMusicList();
        }
    }
}
