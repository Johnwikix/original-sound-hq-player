using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicBrowsePage : Page
    {
        public MusicBrowseViewModel ViewModel { get; }

        // x:Bind 函数绑定用：空库占位可见时隐藏内容 Frame（空子页可能残留表头/滚动条）
        public static Visibility InvertVisibility(Visibility value) =>
            value == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        public MusicBrowsePage()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            ViewModel.SetMusicBrowsePage(this);
            DataContext = this;
            Focus(FocusState.Programmatic);
            Loaded += OnPageLoaded;
            NavigationCacheMode = NavigationCacheMode.Required;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RestoreSubPageState();
        }

        private void RestoreSubPageState()
        {
            switch (ContentFrame.Content)
            {
                case AlbumPage page:
                    AppData.CurrentPage = typeof(AlbumPage);
                    if (page.ViewModel.IsInDetailMode)
                    {
                        ViewModel.AppViewModel.PageType = "album";
                        ViewModel.AppViewModel.IsBackBtnEnable = true;
                    }
                    break;
                case ArtistPage page:
                    AppData.CurrentPage = typeof(ArtistPage);
                    if (page.ViewModel.IsInDetailMode)
                    {
                        ViewModel.AppViewModel.PageType = "artist";
                        ViewModel.AppViewModel.IsBackBtnEnable = true;
                    }
                    break;
                case FolderBrowsePage page:
                    AppData.CurrentPage = typeof(FolderBrowsePage);
                    if (page.ViewModel.IsInDetailMode)
                    {
                        ViewModel.AppViewModel.PageType = "folder";
                        ViewModel.AppViewModel.IsBackBtnEnable = true;
                    }
                    break;
                case SongListPage:
                    AppData.CurrentPage = typeof(SongListPage);
                    break;
                case FavouritePlayListPage:
                    AppData.CurrentPage = typeof(FavouritePlayListPage);
                    break;
            }
            ViewModel.AppViewModel.RefreshDataSource();
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            var valid = new[] { "song", "album", "artist", "folder", "favourite" };
            var saved = ViewModel.AppViewModel.DefaultPlayListComboBoxTag;
            if (!valid.Contains(saved))
            {
                ViewModel.AppViewModel.DefaultPlayListComboBoxTag = "song";
            }
            if (ContentFrame.Content is null)
            {
                SelectBarItem(ViewModel.AppViewModel.DefaultPlayListComboBoxTag);
            }
            Loaded -= OnPageLoaded;
        }

        public void NavigatePage(Type pageType, object? parameter = null, NavigationTransitionInfo? navigationTransitionInfo = null)
        {
            ContentFrame.Navigate(pageType, parameter, navigationTransitionInfo);
        }

        public Task<bool> AreUSureDeleteFromDisk()
        {
            return DialogHelper.ShowConfirmAsync(this.XamlRoot, "AreUSureDeleteFromDisk");
        }

        private bool _syncingSelectorBar;

        private void SelectPage_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (_syncingSelectorBar || sender.SelectedItem is not SelectorBarItem item || item == ViewModel.SelectedPage)
            {
                return;
            }
            ViewModel.SelectedPage = item;
        }

        public void SelectBarItem(string name)
        {
            foreach (var item in selectPage.Items)
            {
                if (item is SelectorBarItem selectorBarItem && selectorBarItem.Tag.ToString() == name)
                {
                    ViewModel.SelectedPage = selectorBarItem;
                    if (!IsLoaded)
                    {
                        var target = selectorBarItem;
                        Loaded += OnSelectorBarSyncLoaded;
                        void OnSelectorBarSyncLoaded(object sender, RoutedEventArgs e)
                        {
                            Loaded -= OnSelectorBarSyncLoaded;
                            if (ViewModel.SelectedPage != target) return;
                            ForceSelectorBarSelection(target);
                        }
                    }
                    break;
                }
            }
        }

        private void ForceSelectorBarSelection(SelectorBarItem target)
        {
            SelectorBarItem? other = null;
            foreach (var item in selectPage.Items)
            {
                if (item is SelectorBarItem sbi && sbi != target)
                {
                    other = sbi;
                    break;
                }
            }
            if (other is null) return;
            _syncingSelectorBar = true;
            selectPage.SelectedItem = other;
            selectPage.SelectedItem = target;
            _syncingSelectorBar = false;
            selectPage.UpdateLayout();
        }

        // USB 设备选择由 ComboBox SelectedItem 双向绑定 UsbDeviceService.SelectedDevice 完成，
        // 台账加载与扫描在服务内触发，此处无需代码后置逻辑。

        private void EmptyLibraryGrid_DragOver(object sender, DragEventArgs e)
        {
            bool canDrop = ViewModel.DropFoldersFromEmptyCommand.CanExecute(null)
                && e.DataView.Contains(StandardDataFormats.StorageItems);
            e.AcceptedOperation = canDrop
                ? DataPackageOperation.Link
                : DataPackageOperation.None;
            DropOverlay.Visibility = canDrop ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EmptyLibraryGrid_DragLeave(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void EmptyLibraryGrid_Unloaded(object sender, RoutedEventArgs e)
        {
            // 页面会缓存，卸载时清理瞬时拖放反馈。
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private async void EmptyLibraryGrid_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!ViewModel.DropFoldersFromEmptyCommand.CanExecute(null)
                || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var folders = items.AsValueEnumerable()
                    .Where(item => item.IsOfType(Windows.Storage.StorageItemTypes.Folder))
                    .ToList();
                if (folders.Count > 0)
                {
                    await ViewModel.DropFoldersFromEmptyCommand.ExecuteAsync(folders);
                }
            }
            catch (Exception ex)
            {
                App.GetLogger<MusicBrowsePage>().LogError(ex, $"EmptyLibraryGrid_Drop 拖放文件夹失败: {ex.Message}");
            }
        }

        public void SelectBarAlbum(string Album)
        {
            ViewModel.AppViewModel.PageType = "album";
            ViewModel.AppViewModel.CurrentAlbumObj = ViewModel.AppViewModel.FindFirstByAlbum(Album);
            if (ContentFrame?.Content is AlbumPage albumPage)
            {
                albumPage.EnterDetailFromCrossLink();
            }
            else
            {
                SelectBarItem("album");
            }
        }

        public void SelectBarArtist(string artist)
        {
            ViewModel.AppViewModel.PageType = "artist";
            ViewModel.AppViewModel.CurrentArtistObj = ViewModel.AppViewModel.FindFirstByArtist(artist);
            if (ContentFrame?.Content is ArtistPage artistPage)
            {
                artistPage.EnterDetailFromCrossLink();
            }
            else
            {
                SelectBarItem("artist");
            }
        }

        public void BackButton()
        {
            if (ContentFrame.Content is AlbumPage albumPage && albumPage.ViewModel.IsInDetailMode)
            {
                albumPage.CollapseDetail();
                return;
            }
            if (ContentFrame.Content is ArtistPage artistPage && artistPage.ViewModel.IsInDetailMode)
            {
                artistPage.CollapseDetail();
                return;
            }
            if (ContentFrame.Content is FolderBrowsePage folderPage && folderPage.ViewModel.IsInDetailMode)
            {
                folderPage.CollapseDetail();
                return;
            }
        }

        public void UpdateViewList()
        {
            if (ContentFrame.Content is SongListPage songListPage)
            {
                songListPage?.UpdateMusicListView();
            }
            if (ContentFrame.Content is AlbumPage albumPage && albumPage.ViewModel.IsInDetailMode)
            {
                albumPage.RefreshDetailView();
                albumPage.DetailView?.UpdateMusicListView();
            }
            if (ContentFrame.Content is ArtistPage artistPage && artistPage.ViewModel.IsInDetailMode)
            {
                artistPage.RefreshDetailView();
                artistPage.DetailView?.UpdateMusicListView();
            }
            if (ContentFrame.Content is FolderBrowsePage folderPage && folderPage.ViewModel.IsInDetailMode)
            {
                folderPage.RefreshDetailView();
                folderPage.DetailView?.UpdateMusicListView();
            }
            if (ContentFrame.Content is FavouritePlayListPage favouritePlayListPage)
            {
                favouritePlayListPage?.UpdateMusicListView();
            }
            if (ContentFrame.Content is PlayListPage playListPage && playListPage.ViewModel.IsInDetailMode)
            {
                playListPage.RefreshDetailView();
                playListPage.DetailView?.UpdateMusicListView();
            }
        }
    }
}
