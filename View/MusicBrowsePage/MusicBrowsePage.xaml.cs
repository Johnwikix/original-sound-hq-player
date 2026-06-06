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
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            SelectBarItem(ViewModel.AppViewModel.DefaultPlayListComboBoxTag);
            Loaded -= OnPageLoaded;
        }

        public void NavigatePage(Type pageType, object? parameter = null, NavigationTransitionInfo? navigationTransitionInfo = null)
        {
            ContentFrame.Navigate(pageType, parameter, navigationTransitionInfo);
        }

        public async Task<bool> AreUSureDeleteFromDisk()
        {
            ContentDialog contentDialog = new ContentDialog
            {
                Title = ToolUtils.GetString("AreUSureDeleteFromDisk"),
                PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                CloseButtonText = ToolUtils.GetString("CloseButton"),
                XamlRoot = this.XamlRoot
            };
            contentDialog.RequestedTheme = AppSettings.ElementTheme;
            ContentDialogResult result = await contentDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                return true;
            }
            return false;
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
            ViewModel.AppViewModel.CurrentAlbumObj = ViewModel.AppViewModel.SongsSource.AsValueEnumerable().Where(m => m.Album == Album).OrderBy(music => music.TrackNumber).FirstOrDefault();
            if (ContentFrame?.Content is not null)
            {
                if (ContentFrame.Content is AlbumPage)
                {
                    NavigatePage(typeof(SongCollectionPage), null, new DrillInNavigationTransitionInfo());
                }
                else
                {
                    SelectBarItem("album");
                }
            }
        }

        public void SelectBarArtist(string artist)
        {
            ViewModel.AppViewModel.PageType = "artist";
            ViewModel.AppViewModel.CurrentArtistObj = ViewModel.AppViewModel.SongsSource.AsValueEnumerable().FirstOrDefault(m => m.Author == artist);
            if (ContentFrame is not null && ContentFrame.Content is not null)
            {
                if (ContentFrame.Content is ArtistPage)
                {
                    NavigatePage(typeof(SongArtistListPage), null, new DrillInNavigationTransitionInfo());
                }
                else if (ContentFrame.Content is SongArtistListPage)
                {
                    App.Services.GetRequiredService<AppViewModel>().RefreshSongsSource();
                }
                else
                {
                    SelectBarItem("artist");
                }
            }
        }

        public void BackButton()
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                AppData.CurrentPage = typeof(AlbumPage);
                NavigatePage(typeof(AlbumPage), null, new DrillInNavigationTransitionInfo());
            }
            if (ContentFrame.Content is SongArtistListPage)
            {
                AppData.CurrentPage = typeof(ArtistPage);
                NavigatePage(typeof(ArtistPage), null, new DrillInNavigationTransitionInfo());
            }
            if (ContentFrame.Content is SongFolderListPage)
            {
                AppData.CurrentPage = typeof(FolderBrowsePage);
                NavigatePage(typeof(FolderBrowsePage), null, new DrillInNavigationTransitionInfo());
            }
            if (ContentFrame.Content is PlayListSongPage)
            {
                AppData.CurrentPage = typeof(PlayListPage);
                NavigatePage(typeof(PlayListPage), null, new DrillInNavigationTransitionInfo());
            }
        }
        private async void AddPlayList_Click(object sender, RoutedEventArgs e)
        {
            var titlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8
            };
            titlePanel.Children.Add(new TextBlock
            {
                Text = ToolUtils.GetString("FlyoutAddToPlaylist"),
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center
            });
            var customButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE8B5", FontSize = 16 },
                Style = Application.Current.Resources["AccentButtonStyle"] as Style,
                Margin = new Thickness(4, 2, 4, 0),
                Padding = new Thickness(6, 3, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(customButton, GetString("ImportM3u8"));
            titlePanel.Children.Add(customButton);

            ContentDialog contentDialog = new ContentDialog
            {
                Title = titlePanel,
                Content = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = ToolUtils.GetString("EnterPlaylistName") },
                PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                CloseButtonText = ToolUtils.GetString("CloseButton"),
                XamlRoot = this.XamlRoot
            };
            contentDialog.RequestedTheme = AppSettings.ElementTheme;

            async void buttonClickHandler(object s, RoutedEventArgs e)
            {
                List<PlayList> newPlaylists = await OpenM3u8File();
                if (newPlaylists is not null && newPlaylists.Count > 0)
                {
                    ViewModel.AppViewModel.AllPlayList.AddRange(newPlaylists);
                }
                contentDialog.Hide();
                customButton.Click -= buttonClickHandler;
            }

            customButton.Click += buttonClickHandler;

            ContentDialogResult result = await contentDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Microsoft.UI.Xaml.Controls.TextBox textBox = (Microsoft.UI.Xaml.Controls.TextBox)contentDialog.Content;
                string playlistName = textBox.Text;
                if (!string.IsNullOrEmpty(playlistName))
                {
                    PlayList newPlaylist = new() { Name = playlistName };
                    await ViewModel.InsertPlayList(newPlaylist);
                    ViewModel.AppViewModel.AllPlayList.Add(newPlaylist);
                }
            }
            customButton.Click -= buttonClickHandler;
        }

        public void UpdateViewList()
        {
            if (ContentFrame.Content is SongListPage songListPage)
            {
                songListPage?.UpdateMusicListView();
            }
            if (ContentFrame.Content is SongCollectionPage songCollectionPage)
            {
                songCollectionPage?.UpdateMusicListView();
            }
            if (ContentFrame.Content is SongArtistListPage artistListPage)
            {
                artistListPage?.UpdateMusicListView();
            }
            if (ContentFrame.Content is SongFolderListPage folderListPage)
            {
                folderListPage?.UpdateMusicListView();
            }
            if (ContentFrame.Content is FavouritePlayListPage favouritePlayListPage)
            {
                favouritePlayListPage?.UpdateMusicListView();
            }
            if (ContentFrame.Content is PlayListSongPage playListSongPage)
            {
                playListSongPage?.UpdateMusicListView();
            }
        }
    }
}
