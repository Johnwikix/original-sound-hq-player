using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class ArtistViewModel : ObservableObject
    {
        private MusicBrowsePage? parentPage { get; }
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private ArtistPage? currentPage { get; set; }
        private ContextMenuService _contextMenuService { get; }

        public ArtistViewModel(MusicBrowsePage parent, ContextMenuService contextMenuService, MusicBrowseViewModel? musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = parent;
            _contextMenuService = contextMenuService;
            _contextMenuService.showTransmission += (s, e) =>
            {
                if (parentPage is not null)
                {
                    parentPage.ShowTransmission();
                }
            };
            _contextMenuService.hideTransmission += (s, e) =>
            {
                if (parentPage is not null)
                {
                    parentPage.HideTransmission();
                }
            };
            _musicBrowseViewModel = musicBrowseViewModel;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
        }

        public void UpdateUsbIcon()
        {
            //ToolUtils.RefreshIcon(MusicList, "artist");
        }

        public void SetCurrentPage(ArtistPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentArtistObj = null;
            AppViewModel.PageType = "artistBrowse";
            App.MainWindow.IsBackBtnEnable = false;
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
                    parentPage.NavigatePage(typeof(SongArtistListPage), new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);             
                }
            }
        }

        public async void Artist_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;
            // 向上遍历查找 GridViewItem
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);
            if (clickedItem is not null)
            {
                // 从 GridViewItem 获取数据项
                var artist = clickedItem.Content as Music;

                if (artist is not null)
                {
                    // 显示专辑右键菜单
                    await _contextMenuService.ShowAlbumContextMenu(
                        artist,
                        originalSource,
                        e.GetPosition(originalSource),
                        "artist"
                    );
                    _contextMenuService.playingArtistMusic += PlayingArtist;
                }
            }
            e.Handled = true;
        }

        private void PlayingArtist(object? sender, Music e)
        {
            List<Music> artists = (_musicDatabaseService.GetArtistMusicFromMem(e.Author)).AsValueEnumerable().OrderBy(m => m.Album).ToList();
            if (artists is not null && artists.Count > 0)
            {
                if (parentPage is not null)
                {
                    AppViewModel.SequentialPlayingList = new(artists);
                    parentPage.PlayMusic(music: artists[0], IsChangeList: true);
                }
            }
        }
    }
}
