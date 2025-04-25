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
    public sealed partial class ArtistPage : Page
    {
        private MusicBrowsePage parentPage;
        private ObservableCollection<Music> musicList;
        private List<Music> _allMusic;
        private int _itemsPerPage = 100; // 每页加载的项目数
        private int _currentPage = 0;
        private bool _isLoading = false;
        private ScrollViewer _gridViewScrollViewer;
        private string _lastSearchText = "";
        public ArtistPage()
        {
            this.InitializeComponent();
            musicList = new ObservableCollection<Music>();
            ArtistsGridView.ItemsSource = musicList;
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.currentArtistName = null;
                parentPage.DisableBackButton();
                parentPage.refreshPage += RefreshArtist;
                if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0)
                {
                    _lastSearchText = AppData.searchText;
                    InitializeData();
                }
                else
                {
                    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                }
            }

            ArtistsGridView.Loaded += (s, e) =>
            {
                _gridViewScrollViewer = ToolUtils.FindVisualChild<ScrollViewer>(ArtistsGridView);
                if (_gridViewScrollViewer != null)
                {
                    _gridViewScrollViewer.ViewChanged += GridViewScrollViewer_ViewChanged;
                }
            };
        }

        private void RefreshArtist(object? sender, EventArgs e)
        {
            //_currentPage = 0;
            //musicList.Clear();
            InitializeData();
        }

        public async void SortMusicList(string sortOrder)
        {
            if (_allMusic.Count > 0)
            {
                _allMusic = ToolUtils.SortMusicList("artistCover", sortOrder, _allMusic.ToList());
                await LoadMoreArtistAsync(true);
            }
        }

        private async void InitializeData()
        {
            try
            {
                if (parentPage != null)
                {
                    _allMusic = (MusicDatabaseService.GetMusicListFromMem(AppData.searchText)).GroupBy(m => m.Author).Select(g => g.First()).OrderBy(m => m.Author).ToList();
                    await LoadMoreArtistAsync(true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化艺术家页面时出错: {ex.Message}");
            }
        }

        private async Task LoadMoreArtistAsync(bool isFirstLoad = false)
        {
            if (_isLoading) return;

            try
            {
                _isLoading = true;

                if (isFirstLoad)
                {  
                    musicList.Clear();
                    _currentPage = 0;
                }

                int startIndex = _currentPage * _itemsPerPage;
                if (startIndex >= _allMusic.Count)
                {
                    _isLoading = false;
                    return;
                }
                var itemsToAdd = _allMusic.Skip(startIndex)
                                              .Take(_itemsPerPage).ToList();
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
                _ = LoadMoreArtistAsync();
            }
        }

        private void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item != null)
            {
                Music artist = item.Content as Music;
                if (parentPage != null)
                {
                    parentPage.LoadArtistMusic(artist.Author);
                }
            }
        }

        private async void Artist_RightTapped(object sender, RightTappedRoutedEventArgs e)
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
                    await ContextMenuService.Instance.ShowAlbumContextMenu(
                        artist,
                        originalSource,
                        e.GetPosition(originalSource),
                        "artist"
                    );
                    ContextMenuService.playingArtistMusic += PlayingArtist;
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
                    parentPage.musicPlaybackService.currentPlayingList = artists;
                    await parentPage.PlayMusic(artists[0]);
                }
            }
        }

        //private async void AddToFavourite_Click(object sender, RoutedEventArgs e)
        //{
        //    var menuItem = sender as MenuFlyoutItem;
        //    var music = menuItem?.DataContext as Music;
        //    Debug.WriteLine($"专辑名称: {music.Album}, 艺术家: {music.Author}");
        //    if (music != null)
        //    {
        //        List<Music> musicList = await MusicDatabaseService.FindMusicListByArtist(music.Author);
        //        if (musicList != null)
        //        {
        //            _ = MusicDatabaseService.AddMusicListToFavour(musicList);
        //        }
        //    }
        //}
    }

}
