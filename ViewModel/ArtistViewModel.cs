using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class ArtistViewModel: ObservableObject
    {
        private ObservableCollection<Music> _musicList = new ObservableCollection<Music>();
        public ObservableCollection<Music> MusicList
        {
            get => _musicList;
            set => SetProperty(ref _musicList, value);
        }
        private List<Music>? _allMusic;
        private string _lastSearchText = "";
        private MusicBrowsePage? parentPage;
        private ArtistPage? currentPage;
        private ContextMenuService _contextMenuService;

        public ArtistViewModel(MusicBrowsePage parent,ContextMenuService contextMenuService)
        {
            parentPage = parent;
            parentPage.refreshPage += RefreshArtist;
            parentPage.refreshUsbDeviceMusicList +=
                (s, e) =>
                {
                    ToolUtils.RefreshIcon(MusicList, "album");
                };
            parentPage.clearUsbDeviceMusicList +=
                (s, e) =>
                {
                    ToolUtils.RefreshIcon(MusicList, "album");
                };
            _contextMenuService = contextMenuService;
        }

        public void SetCurrentPage(ArtistPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {            
            parentPage.currentArtistName = null;
            parentPage.pageType = null;
            parentPage.DisableBackButton();            
            if (_lastSearchText != AppData.searchText || MusicList == null || MusicList.Count == 0)
            {
                _lastSearchText = AppData.searchText;
                InitializeData();
            }
            else
            {
                Debug.WriteLine("搜索条件未变更，保留当前视图状态");
            }
            ToolUtils.RefreshIcon(MusicList, "artist");            
        }

        private void RefreshArtist(object? sender, EventArgs e)
        {
            InitializeData();
        }

        private void InitializeData()
        {
            try
            {
                MusicList.Clear();
                if (parentPage != null)
                {
                    _allMusic = (MusicDatabaseService.GetMusicListFromMem(AppData.searchText)).GroupBy(m => m.Author).Select(g => g.First()).OrderBy(m => m.Author).ToList();
                    LoadMoreArtistAsync(true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化艺术家页面时出错: {ex.Message}");
            }
        }

        private void LoadMoreArtistAsync(bool isFirstLoad = false)
        {

            try
            {
                foreach (var item in _allMusic)
                {
                    MusicList.Add(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }

        public void SortMusicList(string sortOrder)
        {
            if (_allMusic.Count > 0)
            {
                MusicList.Clear();
                _allMusic = ToolUtils.SortMusicList("artistCover", sortOrder, _allMusic.ToList());
                LoadMoreArtistAsync(true);
            }
        }

        public void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            GridViewItem item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item != null)
            {
                Music artist = item.Content as Music;
                if (parentPage != null)
                {
                    parentPage.LoadArtistMusic(artist.Author);
                }
            }
        }

        public async void Artist_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;
            // 向上遍历查找 GridViewItem
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);
            if (clickedItem != null)
            {
                // 从 GridViewItem 获取数据项
                var artist = clickedItem.Content as Music;

                if (artist != null)
                {
                    // 显示专辑右键菜单
                    await _contextMenuService.ShowAlbumContextMenu(
                        artist,
                        originalSource,
                        e.GetPosition(originalSource),
                        "artist"
                    );
                    _contextMenuService.playingArtistMusic += PlayingArtist;
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
            }
            e.Handled = true;
        }

        private async void PlayingArtist(object? sender, Music e)
        {
            List<Music> artists = (MusicDatabaseService.GetArtistMusicFromMem(e.Author)).OrderBy(m => m.Album).ToList();
            if (artists != null && artists.Count > 0)
            {
                if (parentPage != null)
                {
                    parentPage.musicPlaybackService.MusicBrowseViewModel.CurrentPlayingList = new ObservableCollection<Music>(artists);
                    await parentPage.PlayMusic(artists[0]);
                }
            }
        }
    }
}
