using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AlbumViewModel: ObservableObject
    {
        private ObservableCollection<Music> _musicList = new ObservableCollection<Music>();
        public ObservableCollection<Music> MusicList
        {
            get => _musicList;
            set => SetProperty(ref _musicList, value);
        }
        private CollectionViewSource _groupedMusicViewSource;
        public CollectionViewSource GroupedMusicViewSource
        {
            get => _groupedMusicViewSource;
            set => SetProperty(ref _groupedMusicViewSource, value);
        }
        private List<MusicGroup> groupedByFirstLetter = new List<MusicGroup>();

        private string _lastSearchText = "";

        private MusicBrowsePage? parentPage;
        private AlbumPage? currentPage;
        private ContextMenuService _contextMenuService;

        public AlbumViewModel(MusicBrowsePage parent, ContextMenuService contextMenuService)
        {
            parentPage = parent;
            GroupedMusicViewSource = new CollectionViewSource
            {
                IsSourceGrouped = true
            };
            parentPage.refreshPage += RefreshAlbum;
            //parentPage.refreshUsbDeviceMusicList +=
            //    (s, e) =>
            //    {
            //        ToolUtils.RefreshIcon(MusicList, "album");
            //    };
            //parentPage.clearUsbDeviceMusicList +=
            //    (s, e) =>
            //    {
            //        ToolUtils.RefreshIcon(MusicList, "album");
            //    };
            _contextMenuService = contextMenuService;
            _contextMenuService.playingAlbumMusic += PlayingAlbum;
            _contextMenuService.showTransmission += (s, e) =>
            {
                if (parentPage != null)
                {
                    parentPage.ShowTransmission();
                }
            };
            _contextMenuService.hideTransmission += (s, e) =>
            {
                if (parentPage != null)
                {
                    parentPage.HideTransmission();
                }
            };
        }

        public void UpdateUsbIcon() {
            ToolUtils.RefreshIcon(MusicList, "album");
        }

        public void SetCurrentPage(AlbumPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            
            parentPage.CurrentAlbum = null;
            parentPage.ViewModel.PageType = "albumBrowse";
            parentPage.DisableBackButton();            
            Entance();
        }

        private void RefreshAlbum(object? sender, EventArgs e)
        {
            InitializeData();
        }

        public void Entance()
        {
            if (_lastSearchText != AppData.searchText || MusicList == null || MusicList.Count == 0)
            {
                _lastSearchText = AppData.searchText;
                InitializeData();
            }
            ToolUtils.RefreshIcon(MusicList, "album");
        }

        public void InitializeData()
        {
            MusicList = new ObservableCollection<Music>(MusicDatabaseService.GetMusicListFromMem(AppData.searchText).GroupBy(m => m.Album).Select(g => g.First()).OrderBy(m => m.Album).ToList());
            LoadMoreAlbumsAsync(true);            
        }
        public void SortMusicList(string sortOrder = "DefaultOrder")
        {
            if (sortOrder == "Artist")
            {
                groupedByFirstLetter = MusicList
                .GroupBy(item => ToolUtils.GetFirstLetterAdvanced(item.Author))
                .OrderBy(group => group.Key)
                .Select(group => new MusicGroup(group.Key, group.ToList()))
                .ToList();
            }
            else {
                groupedByFirstLetter = MusicList
                       .GroupBy(item => ToolUtils.GetFirstLetterAdvanced(item.Album))
                       .OrderBy(group => group.Key)
                       .Select(group => new MusicGroup(group.Key, group.ToList()))
                       .ToList();
            }            
            GroupedMusicViewSource.Source = groupedByFirstLetter;
        }
        private void LoadMoreAlbumsAsync(bool isFirstLoad = false)
        {
            try
            {
                groupedByFirstLetter = MusicList
                        .GroupBy(item => ToolUtils.GetFirstLetterAdvanced(item.Album))
                        .OrderBy(group => group.Key)
                        .Select(group => new MusicGroup(group.Key, group.ToList()))
                        .ToList();
                GroupedMusicViewSource.Source = groupedByFirstLetter;
                ToolUtils.AlbumPageLoadCoverAsync(groupedByFirstLetter);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }            
        }

        public async void OnAlbumDetailChanged(object sender, Music cover)
        {
            var musicToUpdate = MusicList
                .FirstOrDefault(music => music.Album == cover.Album);
            if (musicToUpdate != null)
            {
                musicToUpdate.Cover = cover.Cover;
                musicToUpdate.Year = cover.Year;
                musicToUpdate.Album = cover.Album;
            }
        }

        public void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item != null)
            {
                Music album = item.Content as Music;
                if (parentPage != null)
                {
                    parentPage.LoadAlbumMusic(album);
                }
            }
        }

        public void PlayingAlbum(object? sender, Music e)
        {
            List<Music> albums = MusicDatabaseService.GetAlbumMusicFromMem(e.Album).OrderBy(m => m.Album).ToList();
            if (albums != null && albums.Count > 0)
            {
                if (parentPage != null)
                {
                    parentPage.ViewModel.CurrentPlayingList = new ObservableCollection<Music>(albums);
                    parentPage.PlayMusic(albums[0]);
                }
            }
        }

        public async void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;

            // 向上遍历查找 GridViewItem
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            if (clickedItem != null)
            {
                // 从 GridViewItem 获取数据项
                var album = clickedItem.Content as Music;

                if (album != null)
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
