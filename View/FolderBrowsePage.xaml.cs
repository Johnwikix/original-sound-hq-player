using Microsoft.Extensions.DependencyInjection;
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
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class FolderBrowsePage : Page, INavigatable
    {
        //private MusicBrowsePage parentPage;
        //private ObservableCollection<Music> musicList;
        //private List<Music> _allMusic;
        //private string _lastSearchText = "";
        public FolderViewModel ViewModel { get; }
        public FolderBrowsePage()
        {
            ViewModel = App.Services.GetRequiredService<FolderViewModel>();
            ViewModel.SetCurrentPage(this);
            this.InitializeComponent();
            DataContext = this;
            //musicList = new ObservableCollection<Music>();
            //FolderGridView.ItemsSource = musicList;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                ViewModel.ReceiveNavigation();
                //this.parentPage = parentPage;
                //parentPage.currentFolderName = null;
                //parentPage.pageType = null;
                //parentPage.DisableBackButton();
                //parentPage.refreshPage += RefreshFolder;
                //if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0)
                //{
                //    _lastSearchText = AppData.searchText;
                //    InitializeData();
                //}
                //else
                //{
                //    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                //}
                //ToolUtils.RefreshIcon(musicList, "folder");
                //parentPage.refreshUsbDeviceMusicList +=
                //    (s, e) =>
                //    {
                //        ToolUtils.RefreshIcon(musicList, "album");
                //    };
                //parentPage.clearUsbDeviceMusicList +=
                //    (s, e) =>
                //    {
                //        ToolUtils.RefreshIcon(musicList, "album");
                //    };
            }

            //FolderGridView.Loaded += (s, e) =>
            //{
            //    _gridViewScrollViewer = ToolUtils.FindVisualChild<ScrollViewer>(FolderGridView);
            //    if (_gridViewScrollViewer != null)
            //    {
            //        _gridViewScrollViewer.ViewChanged += GridViewScrollViewer_ViewChanged;
            //    }
            //};
        }

        //private void RefreshFolder(object? sender, EventArgs e)
        //{
        //    //musicList.Clear();
        //    InitializeData();
        //}

        public void SortMusicList(string sortOrder)
        {
            ViewModel.SortMusicList(sortOrder);
            //if (_allMusic.Count > 0)
            //{
            //    musicList.Clear();
            //    _allMusic = ToolUtils.SortMusicList("folderCover", sortOrder, _allMusic.ToList());
            //    await LoadMoreFolderAsync(true);
            //}
        }

        //private async void InitializeData()
        //{
        //    try
        //    {
        //        musicList.Clear();
        //        if (parentPage != null)
        //        {
        //            _allMusic = (MusicDatabaseService.GetMusicListFromMemWithFolderSearchOption(AppData.searchText))
        //                .GroupBy(m => m.LastLevelFolderPath)
        //                .Select(g => g.First())
        //                .OrderBy(m => m.LastLevelFolderPath)
        //                .ToList();
        //            await LoadMoreFolderAsync(true);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debug.WriteLine($"初始化文件夹时出错: {ex.Message}");
        //    }
        //}

        //private async Task LoadMoreFolderAsync(bool isFirstLoad = false)
        //{
        //    //if (_isLoading) return;

        //    try
        //    {
        //        foreach (var item in _allMusic)
        //        {
        //            musicList.Add(item);
        //        }
        //        //_isLoading = true;

        //        //if (isFirstLoad)
        //        //{
        //        //    musicList.Clear();
        //        //    _currentPage = 0;
        //        //}

        //        //int startIndex = _currentPage * _itemsPerPage;
        //        //if (startIndex >= _allMusic.Count)
        //        //{
        //        //    _isLoading = false;
        //        //    return;
        //        //}
        //        //var itemsToAdd = _allMusic.Skip(startIndex)
        //        //                              .Take(_itemsPerPage).ToList();
        //        //foreach (var item in itemsToAdd)
        //        //{
        //        //    musicList.Add(item);
        //        //}
        //        //_currentPage++;
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
        //    }
        //    //finally
        //    //{
        //    //    _isLoading = false;
        //    //}
        //}

        //private void GridViewScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        //{
        //    var scrollViewer = sender as ScrollViewer;
        //    if (scrollViewer == null) return;

        //    if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight * 0.7 && !e.IsIntermediate && !_isLoading)
        //    {
        //        _ = LoadMoreFolderAsync();
        //    }
        //}

        private void FolderGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.FolderGridView_ItemClick(sender, e);
            //var gridView = sender as GridView;
            //var item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            //if (item != null)
            //{
            //    Music folder = item.Content as Music;
            //    if (parentPage != null)
            //    {
            //        parentPage.LoadFolderMusic(folder.LastLevelFolderPath);
            //    }
            //}
        }

        private void Folder_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ViewModel.Folder_RightTapped(sender, e);
            //var originalSource = e.OriginalSource as FrameworkElement;

            //// 向上遍历查找 GridViewItem
            //GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            //if (clickedItem != null)
            //{
            //    // 从 GridViewItem 获取数据项
            //    var folder = clickedItem.Content as Music;

            //    if (folder != null)
            //    {
            //        // 显示专辑右键菜单
            //        await ContextMenuService.Instance.ShowAlbumContextMenu(
            //            folder,
            //            originalSource,
            //            e.GetPosition(originalSource),
            //            "folder"
            //        );
            //        ContextMenuService.Instance.playingFolderMusic += PlayingFolder;
            //        ContextMenuService.Instance.rescanFolderEnd += RescanFolderEnd;
            //        ContextMenuService.Instance.showTransmission += (s, e) =>
            //        {
            //            if (parentPage != null)
            //            {
            //                parentPage.ShowTransmission();
            //            }
            //        };
            //        ContextMenuService.Instance.hideTransmission += (s, e) =>
            //        {
            //            if (parentPage != null)
            //            {
            //                parentPage.HideTransmission();
            //            }
            //        };
            //    }
            //}

            //e.Handled = true;
        }

        //private void RescanFolderEnd(object? sender, EventArgs e)
        //{
        //    var mainWindow = (App.MainWindow as MainWindow);
        //    if (mainWindow != null)
        //    {
        //        mainWindow.UpdateMusicList();
        //    }
        //}

        //private async void PlayingFolder(object? sender, Music e)
        //{
        //    List<Music> folders = (MusicDatabaseService.GetFolderMusicFromMem(e.LastLevelFolderPath)).OrderBy(m => m.Album).ToList();
        //    if (folders != null && folders.Count > 0)
        //    {
        //        if (parentPage != null)
        //        {
        //            parentPage.musicPlaybackService.currentPlayingList = folders;
        //            await parentPage.PlayMusic(folders[0]);
        //        }
        //    }
        //}
    }
}
