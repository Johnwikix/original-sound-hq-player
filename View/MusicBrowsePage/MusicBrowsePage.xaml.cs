using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
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
        private NotificationService notificationService;
        private readonly INavigationService _navigationService;        

        private MusicDatabaseService _musicDatabaseService { get; }
        public MusicBrowseViewModel ViewModel { get; }
        public MusicBrowsePage(BassPlayerCommandService musicPlaybackService,
            NotificationService notificationService,
            MusicBrowseViewModel viewModel,
            MusicDatabaseService musicDatabaseService
            )
        {
            this.InitializeComponent();
            ViewModel = viewModel;            
            ViewModel.SetMusicService(musicPlaybackService);
            ViewModel.SetMusicBrowsePage(this);
            DataContext = this;
            _musicDatabaseService = musicDatabaseService;
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.ContentFrame = ContentFrame;
            _navigationService.RegisterPage<FavouritePlayListPage>();
            _navigationService.RegisterPage<SongCollectionPage>();
            _navigationService.RegisterPage<SongArtistListPage>();
            _navigationService.RegisterPage<SongFolderListPage>();
            _navigationService.RegisterPage<AlbumPage>();
            _navigationService.RegisterPage<ArtistPage>();
            _navigationService.RegisterPage<FolderBrowsePage>();
            _navigationService.RegisterPage<PlayListPage>();
            _navigationService.RegisterPage<PlayListSongPage>();
            _navigationService.RegisterPage<SongListPage>();
            this.Focus(FocusState.Programmatic);
            this.notificationService = notificationService;
            this.Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            SelectBarItem(ViewModel.AppViewModel.DefaultPlayListComboBoxTag);
            this.Loaded -= OnPageLoaded;
        }

        public void NavigatePage(System.Type currentPage, NavigationTransitionInfo navigationTransitionInfo, int animeTime)
        {
            _navigationService.Navigate(currentPage, this, navigationTransitionInfo, animeTime, true);
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

        public void ThemeChangedUpdateCover()
        {
            ViewModel.ThemeChangedUpdateCover();
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

        public void ShowTransmission()
        {
            ViewModel.AppViewModel.ProcessRingVisibility = Visibility.Visible;
        }

        public void HideTransmission()
        {
            ViewModel.AppViewModel.ProcessRingVisibility = Visibility.Collapsed;
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
            ViewModel.AppViewModel.CurrentAlbumObj = ViewModel.AppViewModel.AllSongs.AsValueEnumerable().Where(m => m.Album == Album).OrderBy(music => music.TrackNumber).FirstOrDefault();
            if (ContentFrame?.Content is not null)
            {
                if (ContentFrame.Content is AlbumPage)
                {
                    _navigationService.Navigate(typeof(SongCollectionPage), this, new DrillInNavigationTransitionInfo(), ViewModel.AppViewModel.DrillInAnimationTime);
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
            ViewModel.AppViewModel.CurrentArtistObj = ViewModel.AppViewModel.AllSongs.AsValueEnumerable().FirstOrDefault(m => m.Author == artist);
            if (ContentFrame is not null && ContentFrame.Content is not null)
            {
                if (ContentFrame.Content is ArtistPage)
                {
                    _navigationService.Navigate(typeof(SongArtistListPage), this, new DrillInNavigationTransitionInfo(), ViewModel.AppViewModel.DrillInAnimationTime);
                }
                else if (ContentFrame.Content is SongArtistListPage) {
                    App.Services.GetRequiredService<AppViewModel>().RefreshAllSongs();
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
                _navigationService.Navigate(typeof(AlbumPage), this, new DrillInNavigationTransitionInfo(), ViewModel.AppViewModel.DrillInAnimationTime);
            }
            if (ContentFrame.Content is SongArtistListPage)
            {
                AppData.CurrentPage = typeof(ArtistPage);
                _navigationService.Navigate(typeof(ArtistPage), this, new DrillInNavigationTransitionInfo(), ViewModel.AppViewModel.DrillInAnimationTime);
            }
            if (ContentFrame.Content is SongFolderListPage)
            {
                AppData.CurrentPage = typeof(FolderBrowsePage);
                _navigationService.Navigate(typeof(FolderBrowsePage), this, new DrillInNavigationTransitionInfo(), ViewModel.AppViewModel.DrillInAnimationTime);
            }
            if (ContentFrame.Content is PlayListSongPage)
            {
                AppData.CurrentPage = typeof(PlayListPage);
                _navigationService.Navigate(typeof(PlayListPage), this, new DrillInNavigationTransitionInfo(), ViewModel.AppViewModel.DrillInAnimationTime);
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
                // 使用自定义面板作为标题
                Title = titlePanel,
                Content = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = ToolUtils.GetString("EnterPlaylistName") },
                PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                CloseButtonText = ToolUtils.GetString("CloseButton"),
                XamlRoot = this.XamlRoot
            };
            contentDialog.RequestedTheme = AppSettings.ElementTheme;

            // 声明事件处理方法（方便后续解除订阅）
            RoutedEventHandler buttonClickHandler = null;
            buttonClickHandler = async (s, e) =>
            {
                PlayList newPlaylist = await OpenM3u8File();
                if (newPlaylist is not null)
                {
                    ViewModel.AppViewModel.AllPlayList.Add(newPlaylist);
                }
                contentDialog.Hide();
                customButton.Click -= buttonClickHandler;
            };
            customButton.Click += buttonClickHandler;

            ContentDialogResult result = await contentDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Microsoft.UI.Xaml.Controls.TextBox textBox = (Microsoft.UI.Xaml.Controls.TextBox)contentDialog.Content;
                string playlistName = textBox.Text;
                if (!string.IsNullOrEmpty(playlistName))
                {
                    PlayList newPlaylist = new() { Name = playlistName };
                    await _musicDatabaseService.InsertPlayList(newPlaylist);
                    ViewModel.AppViewModel.AllPlayList.Add(newPlaylist);
                }
            }
            customButton.Click -= buttonClickHandler;
        }

        private async void CurrentPlayListButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTip.IsOpen = true;
            UpdateCurrentPlayList();
        }

        private void UpdateCurrentPlayList()
        {
            if (ViewModel.AppViewModel.CurrentPlayingList is not null)
            {
                if (ViewModel.AppViewModel.CurrentPlayingMusic is not null)
                {
                    var selectedMusic = ViewModel.AppViewModel.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(music =>
                    music.Id == ViewModel.AppViewModel.CurrentPlayingMusic.Id);

                    if (selectedMusic is not null)
                    {
                        _ = Task.Delay(100).ContinueWith(_ =>
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                CurrentPlayListView.SelectedItem = selectedMusic;
                                CurrentPlayListView.ScrollIntoView(selectedMusic);
                            });
                        });
                    }
                }
            }
        }

        private void CurrentPlayListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = CurrentPlayListView.SelectedItem as Music;
            if (selectedMusic is not null)
            {
                PlayMusic(music: selectedMusic, IsChangeList: false);
            }
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

        public void PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false, bool IsChangeList = false)
        {
            try
            {
                ViewModel.AppViewModel.CurrentPlayingMusic = music;                
                ViewModel.UpdatePlayBar(ViewModel.AppViewModel.CurrentPlayingMusic);
                ViewModel.AppViewModel.LoadLyricsToUI();
                UpdateViewList();
                UpdateCurrentPlayList();
                ViewModel.MusicPlaybackService.PlayMusic(music);
                ViewModel.AppViewModel.UpdateProgressTimerUI();
                App.Services.GetRequiredService<LyricsRefreshService>().ResetLyrics();                
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }       

        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = false;
        }

        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverProgressBar = true;
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverProgressBar = false;
        }
        private void ProgressSlider_Loaded(object sender, RoutedEventArgs e)
        {
            var thumb = FindVisualChild<Thumb>(ProgressSlider);
            if (thumb is not null)
            {
                thumb.DragStarted += Thumb_DragStarted;
                thumb.DragCompleted += Thumb_DragCompleted;
            }
        }

        private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.IsMouseOverVolumeSlider)
            {
                var delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
                if (delta > 0)
                {
                    ViewModel.AppViewModel.AdjustVolume(1);
                }
                else if (delta < 0)
                {
                    ViewModel.AppViewModel.AdjustVolume(-1);
                }
                e.Handled = true;
            }
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                SelectBarArtist(artist);
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                SelectBarAlbum(albumName);
            }
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            ViewModel.AppViewModel.IsUserDraggingProgressSlider = true;
        }

        private async void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            ViewModel.AppViewModel.IsUserDraggingProgressSlider = false;
            double newPosition = Math.Max(0, Math.Min(ViewModel.AppViewModel.ProgressSlider, await ViewModel.MusicPlaybackService.GetTotalPosition()));
            _ = Task.Run(() =>
            {
                ViewModel.AppViewModel.IsManualSelect = true;
                ViewModel.MusicPlaybackService.ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
                ViewModel.AppViewModel.IsManualSelect = false;
            });
        }

        private void AlbumCoverImage_Click(object sender, RoutedEventArgs e)
        {
            App.Services.GetRequiredService<MainPage>().NavigateToPlayingDetailPage();
        }

        private void EqualizerButton_Click(object sender, RoutedEventArgs e)
        {
            App.Services.GetRequiredService<MainPage>().EqualizerDialog.RequestedTheme = AppSettings.ElementTheme;
            App.Services.GetRequiredService<MainPage>().EqualizerDialog.XamlRoot = this.XamlRoot;
            _ = App.Services.GetRequiredService<MainPage>().EqualizerDialog.ShowAsync();
        }

        private void CurrentPlayListTeachingTipCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTip.IsOpen = false;
        }

        private void AutoScrollHover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = true;
            }
        }

        private void AutoScrollHover_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }

        private void AutoScrollHover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }
    }
}
