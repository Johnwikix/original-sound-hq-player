using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
        private ScrollViewer _gridViewScrollViewer;
        private string _lastSearchText = "";
        public AlbumPage()
        {
            this.InitializeComponent();
            musicList = new ObservableCollection<Music>();
            AlbumGridView.ItemsSource = musicList;
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            DateTime startTime = DateTime.Now;
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.currentAlbumName = null;
                parentPage.pageType = null;
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
                ToolUtils.RefreshIcon(musicList, "album");
                parentPage.refreshUsbDeviceMusicList +=
                    (s, e) =>
                    {
                        ToolUtils.RefreshIcon(musicList, "album");
                    };
                parentPage.clearUsbDeviceMusicList +=
                    (s, e) =>
                    {
                        ToolUtils.RefreshIcon(musicList, "album");
                    };
            }
            Debug.WriteLine($"专辑列表加载耗时: {(DateTime.Now - startTime).TotalMilliseconds} ms");
            
        }
       

        private async void RefreshAlbum(object? sender, EventArgs e)
        {
            InitializeDatabase();
        }


        public async void SortMusicList(string sortOrder = "DefaultOrder")
        {
            if (_allMusic.Count > 0)
            {
                musicList.Clear();
                _allMusic = ToolUtils.SortMusicList("albumCover", sortOrder, _allMusic.ToList());
                await LoadMoreAlbumsAsync(true);
            }
        }

        private async void InitializeDatabase()
        {
            musicList.Clear();
            if (parentPage != null)
            {
                _allMusic = MusicDatabaseService.GetMusicListFromMem(AppData.searchText).GroupBy(m => m.Album).Select(g => g.First()).OrderBy(m => m.Album).ToList();
                await LoadMoreAlbumsAsync(true);
            }
        }

        private async Task LoadMoreAlbumsAsync(bool isFirstLoad = false)
        {

            try
            {
                foreach (var item in _allMusic)
                {
                    musicList.Add(item);
                }
                //await AlbumCoverService.LoadAlbumCoversAsync(_allMusic);
                //Parallel.ForEach(_allMusic, async (music) =>
                //{
                //    DispatcherQueue.TryEnqueue(async () =>
                //    {
                //        await AlbumCoverService.LoadSingleCover(music);
                //    });                    
                //});
                // 页面已经显示，现在开始异步加载封面
                // 使用 Task.Run 避免阻塞 UI 线程
                _ = Task.Run(async () =>
                {
                    // 创建一个任务列表来控制并发数量
                    var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2); // 限制并发数
                    var tasks = _allMusic.Select(async music =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
                            {
                                music.Cover = cachedCover;
                            }
                            else
                            {
                                BitmapImage cover = await ToolUtils.GetAlbumCover(music, AppSettings.CoverSize);
                                DispatcherQueue.TryEnqueue( () =>
                                {
                                    music.Cover = cover;
                                });
                                
                                if (AppSettings.isCoverCacheEnabled)
                                {
                                    AppData.albumCoverCache[music.Album] = cover;
                                }
                            } 
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }).ToArray();

                    await Task.WhenAll(tasks);
                    semaphore.Dispose();
                });
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
                    ContextMenuService.showTransmission += (s, e) =>
                    {
                        if (parentPage != null)
                        {
                            parentPage.ShowTransmission();
                        }
                    };
                    ContextMenuService.hideTransmission += (s, e) =>
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

        private async void PlayingAlbum(object? sender, Music e)
        {
            List<Music> albums = MusicDatabaseService.GetAlbumMusicFromMem(e.Album).OrderBy(m => m.Album).ToList();
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

