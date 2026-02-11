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
    public partial class AlbumViewModel : ObservableObject
    {
        //private ObservableCollection<Music> _musicList = [];
        //public ObservableCollection<Music> MusicList
        //{
        //    get => _musicList;
        //    set => SetProperty(ref _musicList, value);
        //}
        //private CollectionViewSource _groupedMusicViewSource;
        //public CollectionViewSource GroupedMusicViewSource
        //{
        //    get => _groupedMusicViewSource;
        //    set => SetProperty(ref _groupedMusicViewSource, value);
        //}
        //private List<MusicGroup> _groupedByFirstLetter = [];

        //private readonly Queue<MusicGroup> _musicGroupPool = new();

        //private string _lastSearchText = "";

        private MusicBrowsePage? parentPage;
        private MusicBrowseViewModel? _musicBrowseViewModel;
        private MusicDatabaseService _musicDatabaseService { get; }
        public AppViewModel AppViewModel { get; }
        private AlbumPage? currentPage;
        private ContextMenuService _contextMenuService;

        public AlbumViewModel(MusicBrowsePage parent, ContextMenuService contextMenuService, MusicBrowseViewModel musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = parent;
            _musicBrowseViewModel = musicBrowseViewModel;
            _contextMenuService = contextMenuService;
            _contextMenuService.playingAlbumMusic += PlayingAlbum;
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
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
        }

        public void UpdateUsbIcon()
        {
            //ToolUtils.RefreshIcon(MusicList, "album");
        }

        public void SetCurrentPage(AlbumPage page)
        {
            currentPage = page;
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
                if (parentPage is not null && _musicBrowseViewModel is not null && album is not null)
                {
                    AppViewModel.PageType = "album";
                    AppViewModel.CurrentAlbumObj = album;
                    parentPage.NavigatePage(typeof(SongCollectionPage), new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);

                }
            }
        }

        public void PlayingAlbum(object? sender, Music e)
        {
            List<Music> albums = _musicDatabaseService.GetAlbumMusicFromMem(e.Album).AsValueEnumerable().OrderBy(m => m.Album).ToList();
            if (albums is not null && albums.Count > 0)
            {
                if (parentPage is not null && _musicBrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new(albums);
                    parentPage.PlayMusic(music: albums[0], IsChangeList: true);
                }
            }
        }

        public async void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;

            // 向上遍历查找 GridViewItem
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            if (clickedItem is not null)
            {
                // 从 GridViewItem 获取数据项
                var album = clickedItem.Content as Music;

                if (album is not null)
                {
                    _contextMenuService.SetAlbumPage(currentPage);
                    // 显示专辑右键菜单
                    await _contextMenuService.ShowAlbumContextMenu(
                        album,
                        originalSource,
                        e.GetPosition(originalSource),
                        "album"
                    );
                }
            }
            e.Handled = true;
        }
    }
}
