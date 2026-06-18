using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
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
            ContentFrame.Navigated += OnContentFrameNavigated;
            ViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            ViewModel.SetMusicBrowsePage(this);
            DataContext = this;
            Focus(FocusState.Programmatic);
            Loaded += OnPageLoaded;
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        }

        private void OnContentFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            var valid = new[] { "song", "album", "artist", "folder", "favourite" };
            var saved = ViewModel.AppViewModel.DefaultPlayListComboBoxTag;
            if (!valid.Contains(saved))
            {
                ViewModel.AppViewModel.DefaultPlayListComboBoxTag = "song";
            }
            SelectBarItem(ViewModel.AppViewModel.DefaultPlayListComboBoxTag);
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

        private void SelectBarItem(string name)
        {
            foreach (var item in selectPage.Items)
            {
                if (item is SelectorBarItem selectorBarItem && selectorBarItem.Tag.ToString() == name)
                {
                    ViewModel.SelectedPage = selectorBarItem;
                    break;
                }
            }
        }

        private async void UsbDeviceCombox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppData.UsbStorageDevice = null;
            AppData.MusicOnUsbDevice.Clear();
            ToolUtils.ClearAllUsbStatus();
            if (UsbDeviceCombox.SelectedItem is UsbStorageDevice usbStorageDevice)
            {
                ViewModel.UsbDeviceComboxSelectionChanged(usbStorageDevice);
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
            if (ContentFrame is not null && ContentFrame.Content is not null)
            {
                if (ContentFrame.Content is ArtistPage artistPage)
                {
                    artistPage.EnterDetailFromCrossLink();
                }
                else
                {
                    SelectBarItem("artist");
                }
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
