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
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class ArtistViewModel : ObservableObject
    {
        public Music SelectedItem { get; set => SetProperty(ref field, value); }
        public ObservableCollection<MenuModel> ArtistMenuOptions { get; set => SetProperty(ref field, value); } = [];
        private MusicBrowsePage? parentPage { get; }
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        //private ArtistPage? currentPage { get; set; }
        //private ContextMenuService _contextMenuService { get; }

        public ArtistViewModel(MusicBrowsePage parent, MusicBrowseViewModel? musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = parent;
            _musicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            InitalizeOption();
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentArtistObj = null;
            AppViewModel.PageType = "artistBrowse";
            AppViewModel.IsBackBtnEnable = false;
        }

        private void InitalizeOption()
        {
            ArtistMenuOptions.Add(new() { Title = "播放", Tag = "Play", Command = PlayCommand });
            ArtistMenuOptions.Add(new() { Title = "添加到最爱", Tag = "AddToFavour", Command = AddToFavourCommand });
            ArtistMenuOptions.Add(new() { Title = "添加到播放列表", Tag = "AddToPlayList", Children = [] });
        }

        public void UpdateAlbumMenuOptionsPlayList()
        {
            var option = ArtistMenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList)
            {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command = AddToPlayListCommand });
            }
        }

        public void UpDateUsbDeviceMenuflyout()
        {
            var usbFlyout = ArtistMenuOptions.FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
            if (AppData.usbStorageDevices.Count == 0)
            {
                if (usbFlyout is not null) ArtistMenuOptions.Remove(usbFlyout);
                return;
            }
            if (usbFlyout is null)
            {
                usbFlyout = new MenuModel { Title = ToolUtils.GetString("SendToUsbDevice"), Tag = "SendToUsbDevice", Children = [] };
                ArtistMenuOptions.Add(usbFlyout);
            }
            usbFlyout.Children.Clear();
            foreach (var usb in AppData.usbStorageDevices)
            {
                var title = $"{usb.Name} , {ToolUtils.GetString("Path")}：{usb.Path} , {ToolUtils.GetString("FreeSpace")}：{usb.FreeSpaceInGB}GB";
                usbFlyout.Children.Add(new() { Title = title, Tag = usb, Command = TransmitFileToUsbCommand });
            }
        }

        public void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            GridViewItem item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item is not null)
            {
                Music artist = item.Content as Music;
                if (parentPage is not null && _musicBrowseViewModel is not null && artist is not null)
                {  
                    AppViewModel.PageType = "artist";
                    AppViewModel.CurrentArtistObj = artist;
                    parentPage.NavigatePage(typeof(SongArtistListPage), new DrillInNavigationTransitionInfo(), AppViewModel.DrillInAnimationTime);             
                }
            }
        }

        [RelayCommand]
        private void Play()
        {
            var artists = AppViewModel.AllSongs.AsValueEnumerable()
                .Where(m => m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Album).ToList();
            if (artists is not null && artists.Count > 0)
            {
                if (parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new(artists);
                    parentPage.PlayMusic(music: artists[0], IsChangeList: true);
                }
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            var artists = AppViewModel.AllSongs.AsValueEnumerable()
                .Where(m => m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Album);
            foreach (var artist in artists)
            {
                artist.AddToFavourite();
            }
        }

        [RelayCommand]
        private void AddToPlayList(int playListId)
        {
            var albums = AppViewModel.AllSongs
                .Where(m => m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Album);
            _ = _musicDatabaseService.AddMusicListToPlayList(albums, playListId);
        }

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbStorageDevice usbDevice)
        {
            var artists = AppViewModel.AllSongs
                .Where(m => m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Album);
            await AppViewModel.TransmitFileToUsb(artists, usbDevice);
        }
    }
}
