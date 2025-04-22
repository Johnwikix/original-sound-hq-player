using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AlbumPage : Page
    {
        private ObservableCollection<Music> musicList;
        private MusicBrowsePage parentPage;
        private List<Music> _allMusic;
        private int _itemsPerPage = 44; // 每页加载的项目数
        private int _currentPage = 0;
        private bool _isLoading = false;
        private ScrollViewer _gridViewScrollViewer;
        private string _lastSearchText = "";
        public AlbumPage()
        {
            this.InitializeComponent();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.currentAlbumName = null;
                parentPage.DisableBackButton();
                parentPage.refreshPage += RefreshAlbum;
                if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0)
                {
                    _lastSearchText = AppData.searchText;
                    InitializeDatabase();
                }
                else
                {
                    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                }
            }

            AlbumGridView.Loaded += (s, e) =>
            {
                _gridViewScrollViewer = ToolUtils.FindVisualChild<ScrollViewer>(AlbumGridView);
                if (_gridViewScrollViewer != null)
                {
                    _gridViewScrollViewer.ViewChanged += GridViewScrollViewer_ViewChanged;
                }
            };
        }

        private async void RefreshAlbum(object? sender, EventArgs e)
        {
            _currentPage = 0;
            musicList.Clear();
            InitializeDatabase();
        }

        public async void SortMusicList(string sortOrder = "DefaultOrder")
        {
            if (_allMusic.Count > 0)
            {
                _allMusic = ToolUtils.SortMusicList("albumCover", sortOrder, _allMusic.ToList());
                await LoadMoreAlbumsAsync(true);
            }
        }

        private async void InitializeDatabase()
        {
            if (parentPage != null)
            {
                _allMusic = (await MusicDatabaseService.GetMusicListAsync(AppData.searchText)).GroupBy(m => m.Album).Select(g => g.First()).OrderBy(m => m.Album).ToList();
                await LoadMoreAlbumsAsync(true);
            }
        }

        private async Task LoadMoreAlbumsAsync(bool isFirstLoad = false)
        {
            if (_isLoading) return;

            try
            {
                _isLoading = true;

                if (isFirstLoad)
                {
                    musicList = new ObservableCollection<Music>();
                    _currentPage = 0;
                    AlbumGridView.ItemsSource = musicList;
                }

                int startIndex = _currentPage * _itemsPerPage;
                if (startIndex >= _allMusic.Count)
                {
                    _isLoading = false;
                    return;
                }
                var itemsToAdd = _allMusic.Skip(startIndex)
                                              .Take(_itemsPerPage).ToList();
                await AlbumCoverService.LoadAlbumCoversAsync(itemsToAdd);
                foreach (var item in itemsToAdd)
                {
                    musicList.Add(item);
                }
                _currentPage++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        // GridView 滚动事件处理
        private void GridViewScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null) return;

            if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight * 0.7 && !e.IsIntermediate && !_isLoading)
            {
                _ = LoadMoreAlbumsAsync();
            }
        }

        // 修改LoadAlbumsAsync方法为异步清空重新加载
        public async Task LoadAlbumsAsync(List<Music> musics)
        {
            try
            {
                _allMusic = musics;
                await LoadMoreAlbumsAsync(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }

        private async void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
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
                    ContextMenuService.Instance.SetAlbumPage(this);
                    // 显示专辑右键菜单
                    await ContextMenuService.Instance.ShowAlbumContextMenu(
                        album,
                        originalSource,
                        e.GetPosition(originalSource),
                        "album"
                    );
                    ContextMenuService.playingAlbumMusic += PlayingAlbum;
                }
            }
            e.Handled = true;
        }

        private async void PlayingAlbum(object? sender, Music e)
        {
            List<Music> albums = (await MusicDatabaseService.GetAlbumMusicAsync(e.Album)).OrderBy(m => m.Album).ToList();
            if (albums != null && albums.Count > 0)
            {
                if (parentPage != null)
                {
                    parentPage.musicPlaybackService.currentPlayingList = albums;
                    await parentPage.PlayMusic(albums[0]);
                }
            }
        }

        public async void OnAlbumDetailChanged(object sender, Music cover)
        {
            foreach (var music in musicList)
            {
                if (music.Album == cover.Album)
                {
                    music.Cover = cover.Cover;
                    music.Year = cover.Year;
                    music.Album = cover.Album;
                    break;
                }
            }
        }

        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item != null)
            {
                Music album = item.Content as Music;
                if (parentPage != null)
                {
                    parentPage.LoadAlbumMusic(album.Album);
                }
            }
        }
    }
}

