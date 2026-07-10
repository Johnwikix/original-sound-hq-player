using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinRT;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class ArtistViewModel : ObservableObject
    {
        public Music SelectedItem { get; set => SetProperty(ref field, value); }
        public ObservableCollection<MenuModel> ArtistMenuOptions { get; set => SetProperty(ref field, value); } = [];
        public bool IsInDetailMode { get; set => SetProperty(ref field, value); }
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        //private ArtistPage? currentPage { get; set; }
        //private ContextMenuService _contextMenuService { get; }

        public ArtistViewModel(MusicBrowseViewModel? musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            _musicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            InitalizeOption();
        }

        public void ReceiveNavigation()
        {
            if (AppViewModel.CurrentArtistObj is not null && !string.IsNullOrEmpty(AppViewModel.CurrentArtistObj.Author))
            {
                IsInDetailMode = true;
                AppViewModel.PageType = "artist";
                AppViewModel.IsBackBtnEnable = true;
            }
            else
            {
                AppViewModel.CurrentArtistObj = null;
                IsInDetailMode = false;
                AppViewModel.PageType = "artistBrowse";
                AppViewModel.IsBackBtnEnable = false;
            }
        }

        private void InitalizeOption()
        {
            ArtistMenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPlayItem"), Tag = "Play", Command = PlayCommand });
            ArtistMenuOptions.Add(new() { Title = ToolUtils.GetString("AddToFavourite"), Tag = "AddToFavour", Command = AddToFavourCommand });
            ArtistMenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutAddToPlaylistItem"), Tag = "AddToPlayList", Children = [] });
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
            var usbFlyout = ArtistMenuOptions.AsValueEnumerable().FirstOrDefault(m => (string)m.Tag == "SendToUsbDevice");
            if (AppData.UsbStorageDevices.Count == 0)
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
            var pathLabel = ToolUtils.GetString("Path");
            var freeSpaceLabel = ToolUtils.GetString("FreeSpace");
            foreach (var usb in AppData.UsbStorageDevices)
            {
                var title = $"{usb.Name} , {pathLabel}：{usb.Path} , {freeSpaceLabel}：{usb.FreeSpaceInGB}GB";
                usbFlyout.Children.Add(new() { Title = title, Tag = usb, Command = TransmitFileToUsbCommand });
            }
        }

        public void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            GridViewItem? item = gridView?.ContainerFromItem(e.ClickedItem)?.As<GridViewItem>();
            if (item is not null)
            {
                Music? artist = item.Content as Music;
                if (_musicBrowseViewModel is not null && artist is not null)
                {
                    AppViewModel.PageType = "artist";
                    AppViewModel.CurrentArtistObj = artist;
                    IsInDetailMode = true;
                    AppViewModel.IsBackBtnEnable = true;
                }
            }
        }

        public void EnterDetailFromCrossLink()
        {
            if (AppViewModel.CurrentArtistObj is null) return;
            IsInDetailMode = true;
            AppViewModel.IsBackBtnEnable = true;
        }

        public void CollapseDetail()
        {
            if (!IsInDetailMode) return;
            IsInDetailMode = false;
            AppViewModel.CurrentArtistObj = null;
            AppViewModel.PageType = "artistBrowse";
            AppViewModel.IsBackBtnEnable = false;
        }

        public void RefreshDetailView() { }

        [RelayCommand]
        private async Task Play()
        {
            var srcSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppViewModel.SongsSource);
            var pool = System.Buffers.ArrayPool<Music>.Shared;
            var buf = pool.Rent(Math.Max(srcSpan.Length, 1));
            int count = 0;
            try
            {
                for (int i = 0; i < srcSpan.Length; i++)
                {
                    var m = srcSpan[i];
                    if (m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
                    {
                        buf[count++] = m;
                    }
                }
                if (count == 0 || _musicBrowseViewModel is null) return;

                var slice = buf.AsSpan(0, count);
                slice.Sort((a, b) => string.CompareOrdinal(a.Album, b.Album));

                AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(slice.ToArray());
                await _musicBrowseViewModel.PlayMusic(music: slice[0], IsChangeList: true);
            }
            finally
            {
                pool.Return(buf, clearArray: false);
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            var artists = AppViewModel.SongsSource.AsValueEnumerable()
                .Where(m => m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Album);
            foreach (var artist in artists)
            {
                MusicCommands.AddToFavouriteCommand.Execute(artist);
            }
        }

        [RelayCommand]
        private void AddToPlayList(int playListId)
        {
            var albums = AppViewModel.SongsSource
                .Where(m => m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Album);
            _ = _musicDatabaseService.AddMusicListToPlayList(albums, playListId);
        }

        [RelayCommand]
        public async Task TransmitFileToUsb(UsbStorageDevice usbDevice)
        {
            var artists = AppViewModel.SongsSource
                .Where(m => m.Author is not null && m.Author.Equals(SelectedItem.Author, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Album);
            await AppViewModel.TransmitFileToUsb(artists, usbDevice);
        }
    }
}
