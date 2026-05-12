using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AlbumViewModel : ObservableObject
    {
        public Music SelectedItem { get; set => SetProperty(ref field, value); }
        public ObservableCollection<MenuModel> AlbumMenuOptions { get; set => SetProperty(ref field, value); } = [];
        private MusicDatabaseService _musicDatabaseService;
        private MusicBrowseViewModel MusicBrowseViewModel { get; set; }
        public AppViewModel AppViewModel { get; }

        public AlbumViewModel(MusicBrowseViewModel musicBrowseViewModel,AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            MusicBrowseViewModel = musicBrowseViewModel;
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
            if (AppData.UsbStorageDevices.Count == 0)
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
            foreach (var usb in AppData.UsbStorageDevices)
            {
                var title = $"{usb.Name} , {ToolUtils.GetString("Path")}：{usb.Path} , {ToolUtils.GetString("FreeSpace")}：{usb.FreeSpaceInGB}GB";
                usbFlyout.Children.Add(new() { Title = title, Tag = usb, Command = TransmitFileToUsbCommand });
            }
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentAlbumObj = null;
            AppViewModel.PageType = "albumBrowse";
            AppViewModel.IsBackBtnEnable = false;
        }
        

        public void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item is not null)
            {
                Music album = item.Content as Music;
                if (MusicBrowseViewModel is not null && album is not null)
                {
                    AppViewModel.PageType = "album";
                    AppViewModel.CurrentAlbumObj = album;
                    MusicBrowseViewModel.NavigatePage(typeof(SongCollectionPage),null, new DrillInNavigationTransitionInfo());

                }
            }
        }

        [RelayCommand]
        private async Task Play() {
            var albums = AppViewModel.SongsSource.AsValueEnumerable()
                .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album,StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.TrackNumber).ToList();
            if (albums is not null &&albums.Count > 0)
            {
                if (MusicBrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new(albums);
                    await MusicBrowseViewModel.PlayMusic(music: albums[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            var albums = AppViewModel.SongsSource.AsValueEnumerable()
               .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
               .OrderByDescending(m => m.TrackNumber);
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
            var albums = AppViewModel.SongsSource
              .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
              .OrderBy(m => m.TrackNumber);
            _ = _musicDatabaseService.AddMusicListToPlayList(albums, playListId);
        }

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbStorageDevice usbDevice)
        {
            var albums = AppViewModel.SongsSource
                .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.TrackNumber);
            await AppViewModel.TransmitFileToUsb(albums, usbDevice);
        }
    }
}
