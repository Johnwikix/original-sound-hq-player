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
        private bool isMouseOverVolumeSlider = false;
        private NotificationService notificationService;
        private readonly INavigationService _navigationService;
        private EqualizerDialog equalizerDialog;
        private readonly AcrylicBrush acrylicBrush = new() { TintOpacity = 0.5 };
        private Storyboard? _lyricImgRTAni;
        private MusicDatabaseService _musicDatabaseService { get; }
        public MusicBrowseViewModel ViewModel { get; }
        public MusicBrowsePage(BassPlayerCommandService musicPlaybackService,
            LyricsRefreshService lyricsRefreshService,
            NotificationService notificationService,
            MusicBrowseViewModel viewModel,
            MusicDatabaseService musicDatabaseService
            )
        {
            this.InitializeComponent();
            ViewModel = viewModel;            
            ViewModel.SetLyricsService(lyricsRefreshService);
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
            //if (App.MainWindow is not null)
            //{
            //    App.MainWindow.updateMusicList += MainWindow_updateMusicList;
            //}
            equalizerDialog = new EqualizerDialog();
            equalizerDialog.EqualizerGainChanged += (s, frequency) =>
            {
                int feq = FrequencyIndexMap[frequency];
                musicPlaybackService.SetEqualizerGain(feq, (float)AppSettings.equalizer[frequency]);
            };
            equalizerDialog.clearEqualizer += (s, e) =>
            {
                musicPlaybackService.UpdateSettings();
                if (AppSettings.IsEqualizerEnabled)
                {
                    musicPlaybackService.ToggleEqualizer();
                    musicPlaybackService.SetEqualizer();
                }
                else
                {
                    musicPlaybackService.ClearEqualizer();
                }
            };
            this.notificationService = notificationService;
            SetAcrylicBrushBackground();
            this.Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            SelectBarItem(AppSettings.DefualtPlayList);
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
            contentDialog.RequestedTheme = AppSettings.elementTheme;
            ContentDialogResult result = await contentDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                return true;
            }
            return false;
        }

        public void BeginOrPauseLyricImgAnimation(bool play)
        {
            var transformTarget = ContentGridBrushTransform;

            if (_lyricImgRTAni == null)
            {
                double currentAngle = transformTarget.Rotation;
                _lyricImgRTAni = new Storyboard
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                var rotationAnimation = new DoubleAnimation
                {
                    From = currentAngle,
                    To = currentAngle + 360,
                    Duration = new Duration(TimeSpan.FromSeconds(67)),
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(rotationAnimation, transformTarget);
                Storyboard.SetTargetProperty(rotationAnimation, "Rotation"); 
                _lyricImgRTAni.Children.Add(rotationAnimation);
                _lyricImgRTAni.Begin();
            }
            else
            {
                if (play)
                {
                    _lyricImgRTAni.Resume();
                }
                else
                {
                    _lyricImgRTAni.Pause();
                }
            }
        }

        public void DisposeLyricImgAnimation()
        {
            if (_lyricImgRTAni != null)
            {
                _lyricImgRTAni.Stop();
                _lyricImgRTAni = null;
            }
        }

        private void SetAcrylicBrushBackground()
        {

            ChangeAcrylicBrushBackground();
            ChangeAcrylicBrushBackgroundOpacity();
        }

        public void ChangeAcrylicBrushBackground()
        {

            if (((FrameworkElement)App.MainWindow!.Content).ActualTheme == ElementTheme.Dark)
            {
                acrylicBrush.TintColor = Colors.Black;
            }
            else
            {
                acrylicBrush.TintColor = Colors.White;
            }            
        }

        public void ThemeChangedUpdateCover()
        {
            ViewModel.ThemeChangedUpdateCover();
        }

        public void ChangeAcrylicBrushBackgroundOpacity()
        {
            ViewModel.AppViewModel.IsAcrylicBrushOpacity = ViewModel.AppViewModel.MusicDetailCover is not null && ViewModel.AppViewModel.IsInPlayingDetailMode && AppSettings.IsBackgroundCoverEnabled ? true : false;
        }

        //public async void MainWindow_updateMusicList(object? sender, EventArgs e)
        //{
        //    //AppData.allSongs = await _musicDatabaseService.GetMusicListAsync();
        //    await ViewModel.AppViewModel.AllSongs.ReplaceAllAsync(await _musicDatabaseService.GetMusicListAsync());
        //}

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
            AppData.usbStorageDevice = null;
            AppData.musicOnUsbDevice.Clear();
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
                    _navigationService.Navigate(typeof(SongCollectionPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
                else if (ContentFrame.Content is SongCollectionPage) {
                    //App.MainWindow.UpdateMusicList();
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
                    _navigationService.Navigate(typeof(SongArtistListPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
                else if (ContentFrame.Content is SongArtistListPage) {
                    //App.MainWindow.UpdateMusicList();
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
                _navigationService.Navigate(typeof(AlbumPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
            }
            if (ContentFrame.Content is SongArtistListPage)
            {
                AppData.CurrentPage = typeof(ArtistPage);
                _navigationService.Navigate(typeof(ArtistPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
            }
            if (ContentFrame.Content is SongFolderListPage)
            {
                AppData.CurrentPage = typeof(FolderBrowsePage);
                _navigationService.Navigate(typeof(FolderBrowsePage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
            }
            if (ContentFrame.Content is PlayListSongPage)
            {
                AppData.CurrentPage = typeof(PlayListPage);
                _navigationService.Navigate(typeof(PlayListPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
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
            contentDialog.RequestedTheme = AppSettings.elementTheme;

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
                    PlayList newPlaylist = new PlayList { Name = playlistName };
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
                                CurrentPlayListViewPlayingDetail.SelectedItem = selectedMusic;
                                CurrentPlayListView.ScrollIntoView(selectedMusic);
                                CurrentPlayListViewPlayingDetail.ScrollIntoView(selectedMusic);
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
                ViewModel.LoadLyricsToUI();
                UpdateViewList();
                UpdateCurrentPlayList();
                ViewModel._musicPlaybackService.PlayMusic(music);
                ViewModel.UpdateProgressTimerUI();
                ViewModel.LyricsRefreshService.ResetLyrics();                
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }       

        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverVolumeSlider = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverVolumeSlider = false;
        }

        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.IsMouseOverProgressBar = true;
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.IsMouseOverProgressBar = false;
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
        private void ProgressSliderPlayingDetail_Loaded(object sender, RoutedEventArgs e)
        {
            var thumb = FindVisualChild<Thumb>(ProgressSliderPlayingDetail);
            if (thumb is not null)
            {
                thumb.DragStarted += Thumb_DragStarted;
                thumb.DragCompleted += Thumb_DragCompleted;
            }
        }



        private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (isMouseOverVolumeSlider)
            {
                var delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
                if (delta > 0)
                {
                    ViewModel.AdjustVolume(1);
                }
                else if (delta < 0)
                {
                    ViewModel.AdjustVolume(-1);
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
            ViewModel.IsUserDraggingProgressSlider = true;
        }

        private async void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            ViewModel.IsUserDraggingProgressSlider = false;
            double newPosition = Math.Max(0, Math.Min(ViewModel.ProgressSlider, await ViewModel._musicPlaybackService.GetTotalPosition()));
            _ = Task.Run(() =>
            {
                ViewModel.isManualSelect = true;
                ViewModel._musicPlaybackService.ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
                ViewModel.isManualSelect = false;
            });
        }

        private void AlbumCoverImage_Click(object sender, RoutedEventArgs e)
        {
            ShowPlayingDetail();
        }

        public void ShowPlayingDetail()
        {
            if (!ViewModel.AppViewModel.IsInPlayingDetailMode && ViewModel.AppViewModel.CurrentPlayingMusic is not null)
            {
                ViewModel.AppViewModel.IsInPlayingDetailMode = true;
                AppData.IsPlayingDetail = true;
                App.MainWindow.NavigationViewCollapsed();
                App.MainWindow?.AppTitleBarVisibility(false);
                ChangeAcrylicBrushBackgroundOpacity();
                ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("CoverToDetail", AlbumCoverImageGrid);
                ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("MusicInfoToDetail", AlbumCoverAuthorTitleModel);
                ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("ControlBarToDetail", BottomControlBar);
                //ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("MusicInfoToDetailLyrics", VolumeDown);
                TopPanel.Visibility = Visibility.Collapsed;
                ContentFrame.Visibility = Visibility.Collapsed;
                AlbumCoverAuthorTitleModel.Visibility = Visibility.Collapsed;
                ViewModel.AppViewModel.InfoBarIsOpen = false;
                PlayingDetail.Visibility = Visibility.Visible;
                BottomControlBar.Visibility = Visibility.Collapsed;
                PlayingDetailControlBar.Visibility = Visibility.Visible;                
                ConnectedAnimationService.GetForCurrentView().GetAnimation("CoverToDetail").Configuration = new DirectConnectedAnimationConfiguration();
                ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicInfoToDetail").Configuration = new DirectConnectedAnimationConfiguration();
                ConnectedAnimationService.GetForCurrentView().GetAnimation("ControlBarToDetail").Configuration = new DirectConnectedAnimationConfiguration();
                //ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicInfoToDetailLyrics").Configuration = new DirectConnectedAnimationConfiguration();
                ConnectedAnimationService.GetForCurrentView().GetAnimation("CoverToDetail").TryStart(PlayingDetailAlbumCoverImageGrid);
                ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicInfoToDetail").TryStart(MusicInfoPanel);
                ConnectedAnimationService.GetForCurrentView().GetAnimation("ControlBarToDetail").TryStart(PlayingDetailControlBar);
                //ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicInfoToDetailLyrics").TryStart(LyricViewer);
                ProgressSliderPlayingDetail.Loaded += ProgressSliderPlayingDetail_Loaded;
            }
        }

        private void CancelPlayingDetailButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsInPlayingDetailMode = false;
            AppData.IsPlayingDetail = false;
            App.MainWindow.NavigationViewExpanded();
            ChangeAcrylicBrushBackgroundOpacity();
            App.MainWindow?.AppTitleBarVisibility(true);
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("DetailToCover", PlayingDetailAlbumCoverImageGrid);
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("DetailToMusicInfo", MusicInfoPanel);
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("DetailToControlBar", PlayingDetailControlBar);
            TopPanel.Visibility = Visibility.Visible;
            ContentFrame.Visibility = Visibility.Visible;
            PlayingDetail.Visibility = Visibility.Collapsed;
            AlbumCoverAuthorTitleModel.Visibility = Visibility.Visible;
            BottomControlBar.Visibility = Visibility.Visible;
            PlayingDetailControlBar.Visibility = Visibility.Collapsed;
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToCover").Configuration = new DirectConnectedAnimationConfiguration();
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToMusicInfo").Configuration = new DirectConnectedAnimationConfiguration();
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToControlBar").Configuration = new DirectConnectedAnimationConfiguration();
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToCover").TryStart(AlbumCoverImageGrid);
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToMusicInfo").TryStart(AlbumCoverAuthorTitleModel);
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToControlBar").TryStart(BottomControlBar);
            ProgressSliderPlayingDetail.Loaded -= ProgressSliderPlayingDetail_Loaded;
        }

        private void EqualizerButton_Click(object sender, RoutedEventArgs e)
        {
            equalizerDialog.RequestedTheme = AppSettings.elementTheme;
            equalizerDialog.XamlRoot = this.XamlRoot;
            _ = equalizerDialog.ShowAsync();
        }        

        private void CurrentPlayListButtonPlayingDetail_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTipPlayingDetail.IsOpen = true;
            UpdateCurrentPlayList();
        }

        private void CurrentPlayListViewPlayingDetail_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = CurrentPlayListViewPlayingDetail.SelectedItem as Music;
            if (selectedMusic is not null)
            {
                PlayMusic(music: selectedMusic, IsChangeList: false);
            }
        }

        private void TopControl_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.TopControlsOpacity = 1.0f;
        }

        private void TopControl_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.AppViewModel.IsPlayDetailButtonVisible) {
                ViewModel.AppViewModel.TopControlsOpacity = 0.0f;
            }           
        }

        private void CurrentPlayListTeachingTipPlayingDetailCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTipPlayingDetail.IsOpen = false;
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
