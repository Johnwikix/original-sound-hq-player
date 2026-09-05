using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
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

        private void SelectBarItem(string name)
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

        private void UsbDeviceCombox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 全局状态（选中设备/台账清理）由 UsbDeviceService.SelectAsync 统一处理
            ViewModel.UsbDeviceComboxSelectionChanged(UsbDeviceCombox.SelectedItem as UsbStorageDevice);
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
