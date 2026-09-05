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
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.View.SubView;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AlbumViewModel : ObservableObject
    {
        public Music SelectedItem { get; set => SetProperty(ref field, value); }
        public ObservableCollection<MenuModel> AlbumMenuOptions { get; set => SetProperty(ref field, value); } = [];
        public bool IsInDetailMode { get; set => SetProperty(ref field, value); }
        private MusicDatabaseService _musicDatabaseService;
        private MusicBrowseViewModel MusicBrowseViewModel { get; set; }
        public AppViewModel AppViewModel { get; }

        public AlbumViewModel(MusicBrowseViewModel musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            MusicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            InitalizeOption();
        }

        private void InitalizeOption()
        {
            AlbumMenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPlayItem"), Tag = "Play", Command = PlayCommand });
            AlbumMenuOptions.Add(new() { Title = ToolUtils.GetString("AddToFavourite"), Tag = "AddToFavour", Command = AddToFavourCommand });
            AlbumMenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutAddToPlaylistItem"), Tag = "AddToPlayList", Children = [] });
            AlbumMenuOptions.Add(new() { Title = ToolUtils.GetString("FlyoutPropertiesItem"), Tag = "Property", Command = ShowPropertyWindowCommand });
        }

        public void UpdateAlbumMenuOptionsPlayList()
        {
            var option = AlbumMenuOptions.AsValueEnumerable().FirstOrDefault(a => (string)a.Tag == "AddToPlayList");
            option?.Children.Clear();
            foreach (var item in AppViewModel.AllPlayList)
            {
                option?.Children.Add(new() { Title = item.Name, Tag = item.Id, Command = AddToPlayListCommand });
            }
        }

        public void UpDateUsbDeviceMenuflyout()
            => ToolUtils.UpdateUsbSendMenu(AlbumMenuOptions, TransmitFileToUsbCommand);

        public void ReceiveNavigation()
        {
            if (AppViewModel.CurrentAlbumObj is not null && !string.IsNullOrEmpty(AppViewModel.CurrentAlbumObj.Album))
            {
                IsInDetailMode = true;
                AppViewModel.PageType = "album";
                AppViewModel.IsBackBtnEnable = true;
            }
            else
            {
                AppViewModel.CurrentAlbumObj = null;
                IsInDetailMode = false;
                AppViewModel.PageType = "albumBrowse";
                AppViewModel.IsBackBtnEnable = false;
            }
        }


        public void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView?.ContainerFromItem(e.ClickedItem)?.As<GridViewItem>();
            if (item is not null)
            {
                Music? album = item.Content as Music;
                if (MusicBrowseViewModel is not null && album is not null)
                {
                    AppViewModel.PageType = "album";
                    AppViewModel.CurrentAlbumObj = album;
                    IsInDetailMode = true;
                    AppViewModel.IsBackBtnEnable = true;
                }
            }
        }

        public void EnterDetailFromCrossLink()
        {
            if (AppViewModel.CurrentAlbumObj is null) return;
            IsInDetailMode = true;
            AppViewModel.IsBackBtnEnable = true;
        }

        public void CollapseDetail()
        {
            if (!IsInDetailMode) return;
            IsInDetailMode = false;
            AppViewModel.CurrentAlbumObj = null;
            AppViewModel.PageType = "albumBrowse";
            AppViewModel.IsBackBtnEnable = false;
        }

        public void RefreshDetailView()
        {
            // no-op now: MusicGroupDetailViewModel listens to AppViewModel directly.
            // This method is kept for future use (e.g. forced refresh).
        }

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
                    if (m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
                    {
                        buf[count++] = m;
                    }
                }
                if (count == 0 || MusicBrowseViewModel is null) return;

                var slice = buf.AsSpan(0, count);
                slice.Sort((a, b) => a.TrackNumber.CompareTo(b.TrackNumber));

                AppViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(slice.ToArray());
                await MusicBrowseViewModel.PlayMusic(music: slice[0], IsChangeList: true);
            }
            finally
            {
                pool.Return(buf, clearArray: false);
            }
        }
        [RelayCommand]
        private void AddToFavour()
        {
            var albums = AppViewModel.SongsSource.AsValueEnumerable()
               .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
               .OrderByDescending(m => m.TrackNumber);
            foreach (var album in albums)
            {
                MusicCommands.AddToFavouriteCommand.Execute(album);
            }
        }
        [RelayCommand]
        private void ShowPropertyWindow()
        {
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
        public async Task TransmitFileToUsb(UsbSendTarget? target)
        {
            var albums = AppViewModel.SongsSource
                .Where(m => m.Album is not null && m.Album.Equals(SelectedItem.Album, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.TrackNumber);
            if (target?.Device is null) return;
            await AppViewModel.TransmitFileToUsb(albums, target.Device, target.Format, target.BitrateKbps);
        }
    }
}
