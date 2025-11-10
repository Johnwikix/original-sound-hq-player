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
        private DispatcherTimer typingTimer;
        private NotificationService notificationService;
        public EventHandler refreshSong;
        public EventHandler<bool> refreshPage;
        public EventHandler<PlayList> addPlayListEvent;
        private readonly INavigationService _navigationService;
        private EqualizerDialog equalizerDialog;
        private bool isSearching = false;
        private string lastSearchText = string.Empty;
        private AcrylicBrush acrylicBrush = new AcrylicBrush { TintOpacity = 0.5 };
        private TextBlock _currentAnimatingTextBlock;
        private DispatcherTimer _currentAnimationTimer;
        private CancellationTokenSource _scrollCancellation;
        private bool _isBlurApplied = false;

        public MusicBrowseViewModel ViewModel { get; }
        public MusicBrowsePage(BassPlayerCommandService musicPlaybackService,
            LyricsRefreshService lyricsRefreshService,
            NotificationService notificationService,
            MusicBrowseViewModel viewModel
            )
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            ViewModel.SetMusicService(musicPlaybackService);
            ViewModel.SetLyricsService(lyricsRefreshService);
            ViewModel.SetMusicBrowsePage(this);
            DataContext = this;
            ViewModel.IsInitialized = false;
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.ContentFrame = ContentFrame;
            _navigationService.RegisterPage<FavouritePlayListPage>();
            _navigationService.RegisterPage<SongCollectionPage>();
            _navigationService.RegisterPage<AlbumPage>();
            _navigationService.RegisterPage<ArtistPage>();
            _navigationService.RegisterPage<FolderBrowsePage>();
            _navigationService.RegisterPage<PlayListPage>();
            _navigationService.RegisterPage<PlayListSongPage>();
            _navigationService.RegisterPage<SongListPage>();
            this.Focus(FocusState.Programmatic);
            if (App.MainWindow is not null)
            {
                App.MainWindow.updateMusicList += MainWindow_updateMusicList;
            }
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
            InitializeTimer();
            SetAcrylicBrushBackground();
            ViewModel.IsInitialized = true;
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

        private void SetAcrylicBrushBackground()
        {

            ChangeAcrylicBrushBackground();
            AcrylicBrushBackground.Background = acrylicBrush;
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

        public void ChangeAcrylicBrushBackgroundOpacity()
        {
            ViewModel.IsAcrylicBrushOpacity = ViewModel.MusicDetailCover is not null && ViewModel.IsInPlayingDetailMode && AppSettings.IsBackgroundCoverEnabled ? true : false;
            AcrylicBrushBackground.Background.Opacity = ViewModel.IsAcrylicBrushOpacity ? 1.0 : 0;
        }

        public async void MainWindow_updateMusicList(object? sender, EventArgs e)
        {
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            if (ContentFrame is not null && ContentFrame.Content is not null)
            {
                refreshPage?.Invoke(this, false);
                refreshSong?.Invoke(this, EventArgs.Empty);
            }
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

        private void InitializeTimer()
        {
            typingTimer = new DispatcherTimer();
            typingTimer.Interval = TimeSpan.FromMilliseconds(300);
            typingTimer.Tick += TypingTimer_Tick;
        }
        public void ShowTransmission()
        {
            ViewModel.ProcessRingVisibility = Visibility.Visible;
        }

        public void HideTransmission()
        {
            ViewModel.ProcessRingVisibility = Visibility.Collapsed;
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

        public void DisableBackButton()
        {
            if (App.MainWindow is not null)
            {
                if (ViewModel.currentPage == typeof(SongCollectionPage) || ViewModel.currentPage == typeof(PlayListSongPage))
                {
                    App.MainWindow.DisableEnableBackButton(true);
                }
                else
                {
                    App.MainWindow.DisableEnableBackButton(false);
                }
            }
        }

        public void SelectBarAlbum(string Album)
        {
            ViewModel.PageType = "album";
            ViewModel.paramName = Album;
            ViewModel.CurrentAlbum = AppData.allSongs.AsValueEnumerable().Where(m => m.Album == Album).OrderBy(music => music.TrackNumber).FirstOrDefault();
            ViewModel.CurrentAlbum.Album = Album;
            ViewModel.currentPage = typeof(SongCollectionPage);
            if (ContentFrame is not null && ContentFrame.Content is not null)
            {
                if (ContentFrame.Content is AlbumPage)
                {
                    _navigationService.Navigate(ViewModel.currentPage, this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
                else
                {
                    SelectBarItem("album");
                }
            }
        }

        public void SelectBarArtist(string artist)
        {
            ViewModel.PageType = "artist";
            ViewModel.paramName = artist;
            ViewModel.CurrentArtist = AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Author == artist);
            ViewModel.CurrentArtist.Author = artist;
            ViewModel.currentPage = typeof(SongCollectionPage);
            if (ContentFrame is not null && ContentFrame.Content is not null)
            {
                if (ContentFrame.Content is ArtistPage)
                {
                    _navigationService.Navigate(ViewModel.currentPage, this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
                else
                {
                    SelectBarItem("artist");
                }
            }
        }

        public async Task AddToFavourite(Music music)
        {
            music.IsFavorite = !music.IsFavorite;
            await MusicDatabaseService.AddToFavourite(music, ViewModel.CurrentPlayingMusic);
            if (ViewModel.CurrentPlayingMusic is not null)
            {
                if (ViewModel.CurrentPlayingMusic.Id == music.Id)
                {
                    ViewModel.CurrentPlayingMusic.IsFavorite = music.IsFavorite;
                }
            }
        }


        private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            typingTimer.Stop();
            typingTimer.Start();
        }

        private async void TypingTimer_Tick(object sender, object e)
        {
            typingTimer.Stop();

            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    // 防止重入
                    if (isSearching)
                    {
                        return;
                    }

                    var currentText = SearchTextBox.Text;

                    // 防止重复搜索
                    if (currentText == lastSearchText)
                    {
                        return;
                    }

                    isSearching = true;
                    lastSearchText = currentText;

                    try
                    {
                        AppData.searchText = currentText;
                        if (ContentFrame?.Content is not null)
                        {
                            refreshPage?.Invoke(this, true);
                            refreshSong?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"搜索执行失败: {ex.Message}");
                    }
                    finally
                    {
                        isSearching = false;
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"搜索处理异常: {ex.Message}");
            }
        }

        public void BackButton()
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                switch (ViewModel.PageType)
                {
                    case "album":
                        ViewModel.currentPage = typeof(AlbumPage);
                        _navigationService.Navigate(typeof(AlbumPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    case "artist":
                        ViewModel.currentPage = typeof(ArtistPage);
                        _navigationService.Navigate(typeof(ArtistPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    case "folder":
                        ViewModel.currentPage = typeof(AlbumPage);
                        _navigationService.Navigate(typeof(FolderBrowsePage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    default:
                        break;
                }
            }
            if (ContentFrame.Content is PlayListSongPage)
            {
                ViewModel.currentPage = typeof(PlayListPage);
                _navigationService.Navigate(typeof(PlayListPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
            }
            DisableBackButton();
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
                    addPlayListEvent?.Invoke(this, newPlaylist);
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
                    await MusicDatabaseService.InsertPlayList(newPlaylist);
                    addPlayListEvent?.Invoke(this, newPlaylist);
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
            if (ViewModel.CurrentPlayingList is not null)
            {
                if (ViewModel.CurrentPlayingMusic is not null)
                {
                    var selectedMusic = ViewModel.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(music =>
                    music.Id == ViewModel.CurrentPlayingMusic.Id);

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

        private void UpdateViewList(Music music)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var songListPage = ContentFrame.Content as SongListPage;
                var songCollectionPage = ContentFrame.Content as SongCollectionPage;
                var FavouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
                var playListSongPage = ContentFrame.Content as PlayListSongPage;
                if (songListPage is not null)
                {
                    songListPage.UpdateMusicListView();
                }
                if (songCollectionPage is not null)
                {
                    songCollectionPage.UpdateMusicListView();
                }
                if (FavouritePlayListPage is not null)
                {
                    FavouritePlayListPage.UpdateMusicListView();
                }
                if (playListSongPage is not null)
                {
                    playListSongPage.UpdateMusicListView();
                }
            });
        }

        public void PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false, bool IsChangeList = false)
        {
            try
            {
                ViewModel.CurrentPlayingMusic = music;
                ViewModel.LoadLyricsToUI();
                ViewModel.UpdatePlayBar(ViewModel.CurrentPlayingMusic);
                ViewModel.UpdateLyricsMargin();
                UpdateViewList(music);
                UpdateCurrentPlayList();
                ViewModel._musicPlaybackService.UpdateCurrentPlayList(IsChangeList);
                ViewModel._musicPlaybackService.PlayMusic(music);
                ViewModel.UpdateProgressTimerUI();
                ViewModel.LyricsRefreshService.ResetLyrics();
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }

        public void SelectSortOptionChanged()
        {
            if (ContentFrame is not null && ContentFrame.Content is not null)
            {
                if (ContentFrame.Content is SongCollectionPage)
                {
                    var page = ContentFrame.Content as SongCollectionPage;
                    page.SortMusicList(AppData.sortOrder, ViewModel.PageType);
                }
                if (ContentFrame.Content is SongListPage)
                {
                    var page = ContentFrame.Content as SongListPage;
                    if (page is not null)
                    {
                        page.SortMusicList(AppData.sortOrder);
                    }
                }
                if (ContentFrame.Content is FavouritePlayListPage)
                {
                    var page = ContentFrame.Content as FavouritePlayListPage;
                    if (page is not null)
                    {
                        page.SortMusicList(AppData.sortOrder);
                    }
                }
                if (ContentFrame.Content is AlbumPage)
                {
                    var page = ContentFrame.Content as AlbumPage;
                    if (page is not null)
                    {
                        page.SortMusicList(AppData.sortOrder);
                    }
                }
                if (ContentFrame.Content is PlayListSongPage)
                {
                    var page = ContentFrame.Content as PlayListSongPage;
                    if (page is not null)
                    {
                        page.SortMusicList(AppData.sortOrder);
                    }
                }
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
            if (!ViewModel.IsInPlayingDetailMode && ViewModel.CurrentPlayingMusic is not null && AlbumCoverImage is not null)
            {
                ViewModel.IsInPlayingDetailMode = true;
                App.MainWindow.IsPlayingDetail = true;
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
                ViewModel.InfoBarIsOpen = false;
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
            ViewModel.IsInPlayingDetailMode = false;
            App.MainWindow.IsPlayingDetail = false;
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

        private void ApplyVerticalGradientBlurToLyricViewer()
        {
            if (!AppSettings.IsBackgroundCoverEnabled) return;

            // 检查是否已经应用了模糊效果
            if (_isBlurApplied) return;

            var element = LyricViewer;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // 创建容器视觉层
            var containerVisual = compositor.CreateContainerVisual();
            containerVisual.Size = new System.Numerics.Vector2((float)element.ActualWidth, (float)element.ActualHeight);

            // 1. 清晰层（中间部分）
            var clearBrush = compositor.CreateBackdropBrush();
            var clearVisual = compositor.CreateSpriteVisual();
            clearVisual.Brush = clearBrush;
            clearVisual.Size = containerVisual.Size;

            // 2. 创建顶部模糊层
            var topBlurEffect = new GaussianBlurEffect
            {
                BlurAmount = 2f,
                Source = new CompositionEffectSourceParameter("Source"),
                Optimization = EffectOptimization.Speed,
                BorderMode = EffectBorderMode.Soft
            };

            var topEffectFactory = compositor.CreateEffectFactory(topBlurEffect);
            var topBackdropBrush = compositor.CreateBackdropBrush();
            var topEffectBrush = topEffectFactory.CreateBrush();
            topEffectBrush.SetSourceParameter("Source", topBackdropBrush);

            var topBlurVisual = compositor.CreateSpriteVisual();
            topBlurVisual.Size = containerVisual.Size;
            topBlurVisual.Brush = topEffectBrush;

            // 顶部渐变遮罩（从上到中间：不透明到透明）
            var topGradient = compositor.CreateLinearGradientBrush();
            topGradient.StartPoint = new System.Numerics.Vector2(0, 0);
            topGradient.EndPoint = new System.Numerics.Vector2(0, 0.5f);

            var topStop1 = compositor.CreateColorGradientStop(0.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255));
            var topStop2 = compositor.CreateColorGradientStop(1.0f, Windows.UI.Color.FromArgb(0, 255, 255, 255));
            topGradient.ColorStops.Add(topStop1);
            topGradient.ColorStops.Add(topStop2);

            var topMaskVisual = compositor.CreateSpriteVisual();
            topMaskVisual.Size = containerVisual.Size;
            topMaskVisual.Brush = topGradient;

            topBlurVisual.Brush = compositor.CreateMaskBrush();
            ((CompositionMaskBrush)topBlurVisual.Brush).Source = topEffectBrush;
            ((CompositionMaskBrush)topBlurVisual.Brush).Mask = topGradient;

            // 3. 创建底部模糊层
            var bottomBlurEffect = new GaussianBlurEffect
            {
                BlurAmount = 2f,
                Source = new CompositionEffectSourceParameter("Source"),
                Optimization = EffectOptimization.Speed,
                BorderMode = EffectBorderMode.Soft
            };

            var bottomEffectFactory = compositor.CreateEffectFactory(bottomBlurEffect);
            var bottomBackdropBrush = compositor.CreateBackdropBrush();
            var bottomEffectBrush = bottomEffectFactory.CreateBrush();
            bottomEffectBrush.SetSourceParameter("Source", bottomBackdropBrush);

            var bottomBlurVisual = compositor.CreateSpriteVisual();
            bottomBlurVisual.Size = containerVisual.Size;
            bottomBlurVisual.Brush = bottomEffectBrush;

            // 底部渐变遮罩（从中间到下：透明到不透明）
            var bottomGradient = compositor.CreateLinearGradientBrush();
            bottomGradient.StartPoint = new System.Numerics.Vector2(0, 0.5f);
            bottomGradient.EndPoint = new System.Numerics.Vector2(0, 1.0f);

            var bottomStop1 = compositor.CreateColorGradientStop(0.0f, Windows.UI.Color.FromArgb(0, 255, 255, 255));
            var bottomStop2 = compositor.CreateColorGradientStop(1.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255));
            bottomGradient.ColorStops.Add(bottomStop1);
            bottomGradient.ColorStops.Add(bottomStop2);

            bottomBlurVisual.Brush = compositor.CreateMaskBrush();
            ((CompositionMaskBrush)bottomBlurVisual.Brush).Source = bottomEffectBrush;
            ((CompositionMaskBrush)bottomBlurVisual.Brush).Mask = bottomGradient;

            // 组合所有层
            containerVisual.Children.InsertAtTop(clearVisual);
            containerVisual.Children.InsertAtTop(topBlurVisual);
            containerVisual.Children.InsertAtTop(bottomBlurVisual);

            ElementCompositionPreview.SetElementChildVisual(element, containerVisual);

            // 标记已应用模糊效果
            _isBlurApplied = true;
        }

        private void ApplyBlurToArea(FrameworkElement element, float blurAmount)
        {
            if (element is null) return;
            if (!AppSettings.IsBackgroundCoverEnabled) {
                ElementCompositionPreview.SetElementChildVisual(element, null);
                return;
            }            
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // 1. 定义模糊效果
            var gaussianBlurEffect = new GaussianBlurEffect
            {
                BlurAmount = blurAmount,
                Source = new CompositionEffectSourceParameter("Source"),
                Optimization = EffectOptimization.Balanced,
                BorderMode = EffectBorderMode.Soft
            };

            // 2. 创建效果工厂
            var effectFactory = compositor.CreateEffectFactory(gaussianBlurEffect);

            var backdropBrush = compositor.CreateBackdropBrush();
            var effectBrush = effectFactory.CreateBrush();
            effectBrush.SetSourceParameter("Source", backdropBrush);

            var spriteVisual = compositor.CreateSpriteVisual();
            spriteVisual.Size = new System.Numerics.Vector2((float)element.ActualWidth, (float)element.ActualHeight);
            spriteVisual.Brush = effectBrush;

            ElementCompositionPreview.SetElementChildVisual(element, spriteVisual);
        }

        private void ClearBlurFromLyricViewer()
        {
            if (!_isBlurApplied) return;

            ElementCompositionPreview.SetElementChildVisual(LyricViewer, null);
            _isBlurApplied = false;
        }

        // 在LyricViewer的SizeChanged事件中调用
        private void LyricViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 尺寸变化时需要重新应用（先清除再应用）
            if (AppSettings.IsGradientBlurEnabled) {
                if (_isBlurApplied)
                {
                    ClearBlurFromLyricViewer();
                }
                ApplyVerticalGradientBlurToLyricViewer();
            }
        }

        private async void LyricsTextBlock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var textblock = (TextBlock)sender;
            bool isCurrentLyric = (!AppSettings.IsGlobalFontSizeEnabled &&
                                    (textblock.FontSize == 28 || textblock.FontSize == 32 ||
                                     textblock.FontSize == 36 || textblock.FontSize == 40 ||
                                     textblock.FontSize == 44)) ||
                                    (AppSettings.IsGlobalFontSizeEnabled &&
                                     textblock.FontSize == AppSettings.GlobalFontSize);
            var parentGrid = ToolUtils.FindParent<Grid>(textblock);
            if (isCurrentLyric)
            {
                if (!AppSettings.IsGradientBlurEnabled)
                {
                    ApplyBlurToArea(parentGrid, 0);
                }
                //StartTimerAnimation(textblock, ViewModel.LyricsDurationTime);
                var container = ToolUtils.FindParent<ListViewItem>(textblock);
                if (container == null) return;
                try
                {
                    _scrollCancellation?.Cancel();
                    _scrollCancellation = new CancellationTokenSource();
                    var transform = container.TransformToVisual(LyricViewer.Content as UIElement);
                    var targetPoint = transform.TransformPoint(new Point(0, 0));
                    double startOffset = LyricViewer.VerticalOffset;
                    double targetOffset = targetPoint.Y - (LyricViewer.ActualHeight / 2) + (container.ActualHeight / 2);
                    if (AppSettings.IsAnimateScrollEnabled)
                    {
                        await AnimateScrollAsync(startOffset, targetOffset, _scrollCancellation.Token);
                    }
                    else
                    {
                        LyricViewer.ChangeView(null, targetOffset, null, disableAnimation: false);
                    }

                }
                catch (OperationCanceledException) { }
                catch { }
            }
            else
            {
                if (!AppSettings.IsGradientBlurEnabled)
                {
                    ApplyBlurToArea(parentGrid, 1);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task AnimateScrollAsync(double startOffset, double targetOffset, CancellationToken cancellationToken, int duration = 1000, int fps = 100)
        {
            double distance = targetOffset - startOffset;
            if (Math.Abs(distance) < 1)
            {
                LyricViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
                return;
            }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long targetFrameTimeMs = 1000 / fps; 
            long nextFrameTimeMs = 0;  
            while (stopwatch.ElapsedMilliseconds < duration)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                double progress = (double)stopwatch.ElapsedMilliseconds / duration;
                progress = Math.Min(progress, 1.0);
                double easedProgress = 1 - Math.Pow(1 - progress, 3);
                double currentOffset = startOffset + distance * easedProgress;
                LyricViewer.ChangeView(null, currentOffset, null, disableAnimation: false);
                nextFrameTimeMs += targetFrameTimeMs;
                long timeToWait = nextFrameTimeMs - stopwatch.ElapsedMilliseconds;
                if (timeToWait > 0)
                {
                    await Task.Delay((int)timeToWait, cancellationToken);
                }
            }

            //LyricViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
        }
        private void LyricViewer_ViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
        {
            UpdateLyricsOpacity();
        }
        private void UpdateLyricsOpacity(double maxOpacity = 0.6, double minOpacity = 0.01)
        {
            double viewerHeight = LyricViewer.ActualHeight;
            double viewerCenter = LyricViewer.VerticalOffset + (viewerHeight / 2);
            double maxDistance = viewerHeight / 2.2;
            double opacityRange = maxOpacity - minOpacity;
            var panel = LyricsListView.ItemsPanelRoot as ItemsStackPanel;
            if (panel == null) return;
            for (int i = panel.FirstVisibleIndex; i <= panel.LastVisibleIndex; i++)
            {
                var itemContainer = LyricsListView.ContainerFromIndex(i) as ListViewItem;
                if (itemContainer == null) continue;

                var lyricGrid = itemContainer.ContentTemplateRoot as Grid;
                if (lyricGrid == null) continue;

                var baseTextBlock = lyricGrid.FindName("LyricsTextBlockBase") as TextBlock;
                if (baseTextBlock == null) continue;

                var transform = itemContainer.TransformToVisual(LyricViewer.Content as UIElement);
                var itemTop = transform.TransformPoint(new Point(0, 0)).Y;

                double itemCenter = itemTop + (itemContainer.ActualHeight / 2);

                double distance = Math.Abs(itemCenter - viewerCenter);

                double opacity;

                if (distance >= maxDistance)
                {
                    opacity = minOpacity;
                }
                else
                {
                    double normalizedDistance = distance / maxDistance;
                    opacity = maxOpacity - (normalizedDistance * opacityRange);
                }
                baseTextBlock.Opacity = opacity;
            }
        }

        private void StartTimerAnimation(TextBlock textBlock, TimeSpan duration)
        {
            CancelCurrentAnimation();
            // 初始化 Clip
            var clipGeometry = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, 0, textBlock.ActualHeight)
            };
            textBlock.Clip = clipGeometry;

            var startTime = DateTime.Now;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps

            timer.Tick += (s, e) =>
            {
                var elapsed = DateTime.Now - startTime;
                var progress = Math.Min(1.0, elapsed.TotalSeconds / duration.TotalSeconds);
                var width = textBlock.ActualWidth * progress;

                clipGeometry.Rect = new Windows.Foundation.Rect(0, 0, width, textBlock.ActualHeight);

                if (progress >= 1.0)
                {
                    timer.Stop();
                    textBlock.Clip = null;
                    _currentAnimationTimer = null;
                    _currentAnimatingTextBlock = null;
                }
            };
            // 缓存当前动画
            _currentAnimatingTextBlock = textBlock;
            _currentAnimationTimer = timer;
            timer.Start();
        }

        // 取消当前动画
        public void CancelCurrentAnimation()
        {
            if (_currentAnimationTimer != null)
            {
                _currentAnimationTimer.Stop();
                _currentAnimationTimer = null;
            }

            if (_currentAnimatingTextBlock != null)
            {
                _currentAnimatingTextBlock.Clip = null; // 清除裁剪,恢复完整显示
                _currentAnimatingTextBlock = null;
            }
        }

        private Color GetResourceColor(string key, Color fallbackColor)
        {
            if (Application.Current.Resources.TryGetValue(key, out object resourceValue))
            {
                if (resourceValue is SolidColorBrush solidBrush)
                {
                    return solidBrush.Color;
                }
            }
            return fallbackColor;
        }

        private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LyricLine lyricLine)
            {
                Task.Run(() =>
                {
                    int index = ViewModel.UILyrics.IndexOf(ViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.Time >= lyricLine.Time));
                    ViewModel.UpdateLyricsToUI(index);
                    ViewModel.isManualSelect = true;
                    ViewModel._musicPlaybackService.ChangeWaveChannelTime(lyricLine.Time);
                    ViewModel.isManualSelect = false;
                });
            }
        }

        private void LyricsTextBlock_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {                
                if (AppSettings.IsGradientBlurEnabled || !AppSettings.IsBackgroundCoverEnabled)
                {
                    textBlock.Opacity = textBlock.Opacity + 0.2;
                }
                else
                {
                    var parentGrid = ToolUtils.FindParent<Grid>(textBlock);
                    ApplyBlurToArea(parentGrid, 0);
                    
                }
            }
        }

        private void LyricsTextBlock_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.DataContext is LyricLine lyricLine)
            {
                var parentGrid = ToolUtils.FindParent<Grid>(textBlock);
                if (!lyricLine.IsCurrent)
                {
                    if (AppSettings.IsGradientBlurEnabled || !AppSettings.IsBackgroundCoverEnabled)
                    {
                        textBlock.Opacity = 0;                       
                    }
                    else {
                        ApplyBlurToArea(parentGrid, 1);
                    }
                }
                else
                {
                    if (AppSettings.IsGradientBlurEnabled || !AppSettings.IsBackgroundCoverEnabled)
                    {
                        textBlock.Opacity = textBlock.Opacity + 0.2;                       
                    }
                    else
                    {
                        ApplyBlurToArea(parentGrid, 0);
                    }
                }
            }
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
            ViewModel.TopControlsOpacity = 1.0f;
        }

        private void TopControl_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsPlayDetailButtonVisible) {
                ViewModel.TopControlsOpacity = 0.0f;
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
    }
}
