using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using TagLib.Ape;
using Windows.Media.Playlists;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using ZLinq;
using ZLinq.Traversables;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AlbumViewModel : ObservableObject
    {
        public Music SelectedItem { get; set => SetProperty(ref field, value); }
        public ObservableCollection<MenuModel> AlbumMenuOptions { get; set => SetProperty(ref field, value); } = [];
        private MusicDatabaseService _musicDatabaseService;
        private MusicBrowsePage? parentPage;
        public AppViewModel AppViewModel { get; }

        public AlbumViewModel(MusicBrowsePage parent,AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = parent;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            InitalizeOption();
        }

        private void InitalizeOption() {
            AlbumMenuOptions.Add(new() { Title = "播放", Tag = "Play", Command = PlayCommand });
            AlbumMenuOptions.Add(new() { Title = "添加到最爱", Tag = "AddToFavour" ,Command = AddToFavourCommand});
            AlbumMenuOptions.Add(new(){ Title = "添加到播放列表",Tag = "AddToPlayList",Children = []});
            AlbumMenuOptions.Add(new() { Title = "属性", Tag = "Property",Command = ShowPropertyWindowCommand});
        }

        public void UpdateAlbumMenuOptionsPlayList() {
            var option = AlbumMenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList) {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command=AddToPlayListCommand});
            }
        }

        public void UpDateUsbDeviceMenuflyout()
        {
            var usbFlyout = AlbumMenuOptions.FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
            if (AppData.usbStorageDevices.Count == 0)
            {
                if (usbFlyout is not null) AlbumMenuOptions.Remove(usbFlyout);
                return;
            }
            if (usbFlyout is null)
            {
                usbFlyout = new MenuModel { Title = ToolUtils.GetString("SendToUsbDevice"), Tag = "SendToUsbDevice", Children = [] };
                AlbumMenuOptions.Add(usbFlyout);
            }
            usbFlyout.Children.Clear();
            foreach (var usb in AppData.usbStorageDevices)
            {
                var title = $"{usb.Name} , {ToolUtils.GetString("Path")}：{usb.Path} , {ToolUtils.GetString("FreeSpace")}：{usb.FreeSpaceInGB}GB";
                usbFlyout.Children.Add(new() { Title = title, Tag = usb, Command = TransmitFileToUsbCommand });
            }
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentAlbumObj = null;
            AppViewModel.PageType = "albumBrowse";
            App.MainWindow.IsBackBtnEnable = false;
        }
        

        public void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item is not null)
            {
                Music album = item.Content as Music;
                if (parentPage is not null && album is not null)
                {
                    AppViewModel.PageType = "album";
                    AppViewModel.CurrentAlbumObj = album;
                    parentPage.NavigatePage(typeof(SongCollectionPage), new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);

                }
            }
        }

        [RelayCommand]
        private void Play() {
            var albums = AppViewModel.AllSongs.AsValueEnumerable()
                .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album,StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.TrackNumber).ToList();
            if (albums is not null &&albums.Count > 0)
            {
                if (parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new(albums);
                    parentPage.PlayMusic(music: albums[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            var albums = AppViewModel.AllSongs.AsValueEnumerable()
               .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
               .OrderBy(m => m.TrackNumber);
            foreach (var album in albums) {
                album.AddToFavourite();
            }
        }
        [RelayCommand]
        private void ShowPropertyWindow() {
            var albumDetailWindow = new AlbumDetailWindow(SelectedItem);
            albumDetailWindow.Activate();
        }

        [RelayCommand]
        private void AddToPlayList(int playListId)
        {
            var albums = AppViewModel.AllSongs
              .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
              .OrderBy(m => m.TrackNumber);
            _ = _musicDatabaseService.AddMusicListToPlayList(albums, playListId);
        }

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbStorageDevice usbDevice)
        {
            var albums = AppViewModel.AllSongs
                .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.TrackNumber);
            if (albums.Any())
            {
                parentPage?.ShowTransmission();
                using (var usbWriter = new UsbWriterHelper())
                {
                    usbWriter.hideTransmission += (sender, args) =>
                    {
                        parentPage?.HideTransmission();
                    };
                    await usbWriter.WriteToUsb(albums, usbDevice);
                }
                foreach (var music in albums)
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
